using System.Security.Claims;
using System.Text.Json;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Backups;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>本人のデータ出力と、内容確認後の空アカウントへの復元を提供する</summary>
public sealed class DataController(AppDbContext db, BackupService backups, BackupRestore restore, SessionService sessions,
    AuthWriteGate gate, AuthAudit audit, TimeProvider clock) : Controller
{
    /// <summary>管理者も他人の記録へアクセスしない所有者境界</summary>
    private string Owner => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException();

    /// <summary>出力対象と復元手順を表示する</summary>
    [HttpGet("/data")]
    public async Task<IActionResult> Index() => View(await db.Notebooks.AsNoTracking().Where(x => x.UserId == Owner)
        .OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync());

    /// <summary>生成中だけ更新を排他し、ダウンロード中はゲートを保持しない</summary>
    [HttpPost("/data/export")]
    [ServiceFilter(typeof(AuthOperationFilter))]
    public async Task<IActionResult> Export(string kind, string? bookId)
    {
        if (kind is not ("backup" or "markdown"))
        {
            return BadRequest();
        }

        try
        {
            var path = await backups.ExportAsync(Owner, kind == "backup", string.IsNullOrEmpty(bookId) ? null : bookId, HttpContext.RequestAborted);
            try
            {
                audit.Add(HttpContext, "export." + kind, Owner);
                await db.SaveChangesAsync();
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                return File(stream, "application/zip", $"hibitsuzuri-{kind}-{clock.GetLocalNow():yyyyMMdd-HHmmss}.zip");
            }
            catch
            {
                System.IO.File.Delete(path);
                throw;
            }
        }
        catch (InvalidDataException error)
        {
            return BadRequest(error.Message);
        }
    }

    /// <summary>アップロード前に操作IDを返し、通信が切れても状態を照会できるようにする</summary>
    [HttpPost("/api/data/restore")]
    [ServiceFilter(typeof(AuthOperationFilter))]
    public async Task<IActionResult> Start()
    {
        // 一人の大量並行アップロードによる一時領域の占有を防ぐ。
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (await db.RestoreTransfers.AnyAsync(x => x.UserId == Owner && !x.Completed && x.ExpiresAt > now))
        {
            return Conflict(new
            {
                error = "確認待ちの復元があります。状態を確認するか取り消してください。"
            });
        }

        var transfer = new RestoreTransfer { UserId = Owner, ExpiresAt = clock.GetUtcNow().AddDays(1).ToUnixTimeMilliseconds() };
        db.RestoreTransfers.Add(transfer);
        await db.SaveChangesAsync();
        return Ok(new
        {
            transfer.Id
        });
    }

    /// <summary>フォーム全体をメモリへ読み込まず、ZIPを直接非公開領域へ保存する</summary>
    [HttpPost("/api/data/restore/{id}/upload")]
    [RequestSizeLimit(BackupArchive.CompressedLimit)]
    public async Task<IActionResult> Upload(string id)
    {
        var transfer = await PendingAsync(id);
        if (transfer is null || transfer.Ready || transfer.Completed)
        {
            return Conflict(new
            {
                error = "この復元操作にはアップロードできません。状態を確認してください。"
            });
        }

        var path = backups.PathFor(id);
        FileStream output;
        try
        {
            output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        }
        catch (IOException) when (System.IO.File.Exists(path))
        {
            return Conflict(new
            {
                error = "アップロード済み、またはアップロード中です。状態を確認してください。"
            });
        }

        var ready = false;
        try
        {
            await using (output)
            {
                await BackupArchive.CopyAsync(Request.Body, output, BackupArchive.CompressedLimit, HttpContext.RequestAborted);
            }

            var inspection = await backups.InspectAsync(id, true, HttpContext.RequestAborted);
            await gate.Semaphore.WaitAsync(HttpContext.RequestAborted);
            try
            {
                if (await sessions.CurrentUserAsync(User) is null || await PendingAsync(id) is not { Completed: false })
                {
                    return Unauthorized();
                }

                var expires = clock.GetUtcNow().AddHours(1).ToUnixTimeMilliseconds();
                await db.RestoreTransfers.Where(x => x.Id == id && x.UserId == Owner && !x.Completed)
                    .ExecuteUpdateAsync(set => set.SetProperty(x => x.Ready, true).SetProperty(x => x.ExpiresAt, expires));
                ready = true;
                return Ok(await PreviewAsync(id, inspection));
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or OverflowException)
        {
            return BadRequest(new
            {
                error = "バックアップを読み込めません。形式・破損・容量・参照関係を確認してください。"
            });
        }
        finally
        {
            if (!ready)
            {
                System.IO.File.Delete(path);
            }
        }
    }

    /// <summary>再読み込み・応答喪失時に本人の確認待ちまたは確定結果を返す</summary>
    [HttpGet("/api/data/restore/status")]
    public async Task<IActionResult> Status(string? id)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var transfer = id is null
            ? await db.RestoreTransfers.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == Owner && !x.Completed && x.ExpiresAt > now)
            : await db.RestoreTransfers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == Owner);
        if (transfer is null)
        {
            return Ok(new
            {
                state = "none"
            });
        }

        if (transfer.Completed)
        {
            return Ok(new
            {
                transfer.Id,
                state = "completed"
            });
        }

        if (transfer.ExpiresAt <= now || !transfer.Ready)
        {
            return Ok(new
            {
                transfer.Id,
                state = transfer.ExpiresAt <= now ? "expired" : "upload"
            });
        }

        try
        {
            var inspection = await backups.InspectAsync(transfer.Id, false, HttpContext.RequestAborted);
            return Ok(await PreviewAsync(transfer.Id, inspection));
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or FileNotFoundException)
        {
            return Conflict(new
            {
                error = "確認用ファイルがありません。取り消して、もう一度アップロードしてください。"
            });
        }
    }

    /// <summary>反映直前に空アカウント・名前・ファイルを再確認し、一度だけ保存する</summary>
    [HttpPost("/api/data/restore/{id}/confirm")]
    [ServiceFilter(typeof(AuthOperationFilter))]
    public async Task<IActionResult> Confirm(string id, bool confirmed)
    {
        var completed = await db.RestoreTransfers.AnyAsync(x => x.Id == id && x.UserId == Owner && x.Completed);
        if (completed)
        {
            return Ok(new
            {
                state = "completed"
            });
        }

        if (!confirmed || await PendingAsync(id) is not { Ready: true })
        {
            return BadRequest(new
            {
                error = "復元内容の確認が必要、または確認期限が過ぎています。"
            });
        }

        try
        {
            var inspection = await backups.InspectAsync(id, false, HttpContext.RequestAborted);
            var problem = await backups.TargetProblemAsync(Owner, inspection);
            if (problem is not null)
            {
                return Conflict(new
                {
                    error = problem
                });
            }

            await restore.ApplyAsync(id, Owner, inspection, HttpContext, HttpContext.RequestAborted);
            return Ok(new
            {
                state = "completed"
            });
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or FileNotFoundException)
        {
            return BadRequest(new
            {
                error = "バックアップを再検証できませんでした。記録は復元されていません。"
            });
        }
    }

    /// <summary>確認待ちの復元だけを取り消す。確定済み結果は二重実行防止のため残す</summary>
    [HttpPost("/api/data/restore/{id}/cancel")]
    [ServiceFilter(typeof(AuthOperationFilter))]
    public async Task<IActionResult> Cancel(string id)
    {
        var transfer = await db.RestoreTransfers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == Owner && !x.Completed);
        if (transfer is not null)
        {
            await db.RestoreTransfers.Where(x => x.Id == id).ExecuteDeleteAsync();
            // 書き込み中ならアップロード側のfinallyで回収する。
            try
            {
                System.IO.File.Delete(backups.PathFor(id));
            }
            catch (IOException)
            {
                // 次の定期回収でも再試行する。
            }
        }

        return Ok(new
        {
            state = "none"
        });
    }

    /// <summary>期限内の本人の操作だけを選ぶ</summary>
    private Task<RestoreTransfer?> PendingAsync(string id)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        return db.RestoreTransfers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == Owner && x.ExpiresAt > now);
    }

    /// <summary>本文を返さず、対象名と件数・復元可否を確認できるようにする</summary>
    private async Task<object> PreviewAsync(string id, BackupInspection inspection) => new
    {
        id,
        state = "ready",
        books = inspection.Books.Values.OrderBy(x => x.Position).Select(x => new { name = x.Name, archived = x.Archived, paused = x.TasksPausedOn is not null }),
        entries = inspection.Entries.Count,
        revisions = inspection.Revisions,
        images = inspection.Images.Count,
        missingImages = inspection.ImageReferences.Except(inspection.Images.Keys).Count(),
        tasks = inspection.Tasks,
        series = inspection.Series.Count,
        anniversaries = inspection.Anniversaries,
        problem = await backups.TargetProblemAsync(Owner, inspection)
    };
}
