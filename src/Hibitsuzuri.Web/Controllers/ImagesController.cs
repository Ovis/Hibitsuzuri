using System.Globalization;
using System.Security.Claims;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Images;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>画像の取り込み・非公開配信と下書きの保持期限を扱う</summary>
public sealed class ImagesController(AppDbContext database, ImageStore images, ImageWorker worker, DataDirectory data,
    TimeProvider clock, DiaryMarkdown markdown) : Controller
{
    /// <summary>配信と変更を許す所有者</summary>
    private string Owner => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException();

    /// <summary>実体を検証した画像だけを配信し、ブラウザに永続キャッシュさせない</summary>
    [HttpGet("/images/{id}")]
    public async Task<IActionResult> Read(string id)
    {
        var image = (await images.AvailableAsync(Owner, [id])).SingleOrDefault();
        if (image is null)
        {
            return NotFound();
        }

        // 回収と読み出しが重なっても、開いたストリームは配信終了まで維持する。
        var stream = new FileStream(images.PathFor(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        Response.Headers.CacheControl = "no-store";

        return File(stream, image.MediaType);
    }

    /// <summary>一枚ずつ検証・変換し、下書きの保持期限付きで確定する</summary>
    [HttpPost("/api/images/{bookId}/{date}")]
    [RequestSizeLimit(ImageConverter.MaxBytes + 100000)]
    [RequestFormLimits(MultipartBodyLengthLimit = ImageConverter.MaxBytes + 100000)]
    [ServiceFilter(typeof(AuthOperationFilter))]
    public async Task<IActionResult> Upload(string bookId, string date, string? draftId, IFormFile? file, CancellationToken cancellationToken)
    {
        if (!await database.Notebooks.AnyAsync(x => x.Id == bookId && x.UserId == Owner))
        {
            return NotFound();
        }

        if (!Guid.TryParseExact(draftId, "D", out _) || !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            || day > DateOnly.FromDateTime(clock.GetLocalNow().DateTime) || file is null || file.Length is < 1 or > ImageConverter.MaxBytes)
        {
            return BadRequest(new
            {
                message = "画像は1枚20MiB以内で指定してください。未来の日付へは添付できません。"
            });
        }

        if (await database.DiaryEntries.AnyAsync(x => x.NotebookId == bookId && x.EntryDate == day && x.DeletedAt != null))
        {
            return Conflict(new
            {
                message = "日記がゴミ箱にあります。復元してから添付してください。"
            });
        }

        var original = await database.Users.Where(x => x.Id == Owner).Select(x => x.KeepOriginalImages).SingleAsync(cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var input = Path.Combine(data.ImagesPath, $"work-{id}.input");
        var output = Path.Combine(data.ImagesPath, $"work-{id}.output");
        try
        {
            await using (var stream = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.CopyToAsync(stream, cancellationToken);
                stream.Flush(true);
            }

            var result = await worker.ConvertAsync(input, output, original, cancellationToken);
            var name = Path.GetFileName(file.FileName.Replace('\\', '/'));
            var image = new DiaryImage
            {
                Id = id,
                UserId = Owner,
                NotebookId = bookId,
                EntryDate = day,
                Name = name.Length > 200 ? name[..200] : name,
                MediaType = result.MediaType,
                Width = result.Width,
                Height = result.Height,
                Length = new FileInfo(output).Length,
                CreatedAt = clock.GetUtcNow().ToUnixTimeMilliseconds()
            };
            // ファイルを先に確定し、DB保存前に中断した場合は次の巡回で孤立ファイルを回収する。
            System.IO.File.Move(output, images.PathFor(id));
            database.DiaryImages.Add(image);
            database.ImageDraftLeases.Add(new ImageDraftLease
            {
                ImageId = id,
                DraftId = draftId ?? "",
                ExpiresAt = clock.GetUtcNow().AddDays(30).ToUnixTimeMilliseconds()
            });
            await database.SaveChangesAsync(cancellationToken);

            return Json(new
            {
                image.Id,
                image.Name,
                image.MediaType,
                image.Width,
                image.Height,
                image.Length
            });
        }
        catch (InvalidDataException error)
        {
            return BadRequest(new
            {
                message = error.Message + " JPEG・PNG・WebP・GIFに対応しています。HEIC/HEIFはJPEG等へ変換してください。展開後は4,000万画素・各辺16,384px以内です。"
            });
        }
        finally
        {
            System.IO.File.Delete(input);
            System.IO.File.Delete(output);
        }
    }

    /// <summary>下書き保存で参照中の画像を延長し、欠落した画像は本文を消さず通知する</summary>
    [HttpPost("/api/images/draft")]
    [RequestSizeLimit(1500000)]
    [RequestFormLimits(ValueLengthLimit = 1200000)]
    [ServiceFilter(typeof(AuthOperationFilter))]
    public async Task<IActionResult> Draft(string? draftId, string? content)
    {
        if (!Guid.TryParseExact(draftId, "D", out _) || !AuthRules.ValidText(content ?? "", 0, 100000))
        {
            return BadRequest();
        }

        var missing = await images.RenewAsync(Owner, draftId ?? "", content ?? "");

        return Json(new
        {
            missing,
            expiresAt = clock.GetUtcNow().AddDays(30).ToUnixTimeMilliseconds()
        });
    }

    /// <summary>保存完了・キャンセル・下書きの破棄により、その下書きの保持だけを解除する</summary>
    [HttpPost("/api/images/draft/discard")]
    [ServiceFilter(typeof(AuthOperationFilter))]
    public async Task<IActionResult> DiscardDraft(string? draftId)
    {
        if (!Guid.TryParseExact(draftId, "D", out _))
        {
            return BadRequest();
        }

        await images.DiscardAsync(Owner, draftId ?? "");

        return Json(new
        {
            missing = Array.Empty<string>()
        });
    }

    /// <summary>本人の添付候補と下書き画像の期限を返す</summary>
    [HttpGet("/api/images/{bookId}/{date}")]
    public async Task<IActionResult> List(string bookId, string date)
    {
        if (!await database.Notebooks.AnyAsync(x => x.Id == bookId && x.UserId == Owner)
            || !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return NotFound();
        }

        var available = await images.AvailableAsync(Owner);
        var saved = await images.SavedReferencesAsync(Owner);
        var references = await database.DiaryImageReferences.Where(x => database.DiaryEntries.Any(e => e.Id == x.EntryId
            && e.NotebookId == bookId && e.EntryDate == day)).Select(x => x.ImageId).ToListAsync();
        var leases = await database.ImageDraftLeases.Where(x => database.DiaryImages.Any(i => i.Id == x.ImageId && i.UserId == Owner)).ToListAsync();

        return Json(available.Where(x => (x.NotebookId == bookId && x.EntryDate == day) || references.Contains(x.Id)).Select(x => new
        {
            x.Id,
            x.Name,
            x.Width,
            x.Height,
            x.Length,
            expiresAt = saved.Contains(x.Id) ? (long?)null : leases.Where(l => l.ImageId == x.Id).Max(l => l.ExpiresAt)
        }));
    }

    /// <summary>本文から外した画像を削除する。参照中の画像は影響を確認した完全削除だけで扱う</summary>
    [HttpPost("/api/images/{id}/delete")]
    [RequestSizeLimit(1500000)]
    [RequestFormLimits(ValueLengthLimit = 1200000)]
    [ServiceFilter(typeof(AuthOperationFilter))]
    public async Task<IActionResult> Delete(string id, string? draftId, string? content, bool confirmed,
        bool permanently = false, bool referencesConfirmed = false)
    {
        if (!confirmed || (permanently && !referencesConfirmed)
            || !Guid.TryParseExact(id, "N", out _) || !Guid.TryParseExact(draftId, "D", out _)
            || !AuthRules.ValidText(content ?? "", 0, 100000))
        {
            return BadRequest(new
            {
                message = "本文と削除対象を確認してください。"
            });
        }

        var image = await database.DiaryImages.SingleOrDefaultAsync(x => x.Id == id && x.UserId == Owner);
        if (image is null)
        {
            return NotFound(new
            {
                message = "画像が見つかりません。添付一覧を確認してください。"
            });
        }

        if (markdown.ImageIds(content ?? "").Contains(id))
        {
            return Conflict(new
            {
                message = "先に「本文から外す」で、この画像を本文から外してください。"
            });
        }

        if (!permanently && (await images.SavedReferencesAsync(Owner)).Contains(id))
        {
            return Conflict(new
            {
                message = "保存済みの日記・編集履歴・テンプレートで使用している画像は削除できません。"
            });
        }

        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (!permanently && await database.ImageDraftLeases.AnyAsync(x => x.ImageId == id && x.DraftId != draftId && x.ExpiresAt > now))
        {
            return Conflict(new
            {
                message = "別の下書きで保持している画像です。不要な下書きを破棄してから削除してください。"
            });
        }

        // 完全削除でも本文・履歴・テンプレートは書き換えず、残った参照は欠落画像として表示する。
        // 画像の保持行は外部キーで同時に削除する。ファイル削除失敗時は定期回収へ引き継ぐ。
        database.DiaryImages.Remove(image);
        await database.SaveChangesAsync();
        System.IO.File.Delete(images.PathFor(id));

        return Ok(new
        {
            deleted = true
        });
    }

    /// <summary>アカウント共通の次回以降の画像保存方式を表示する</summary>
    [HttpGet("/settings/images")]
    public async Task<IActionResult> Settings()
    {
        return View(await database.Users.Where(x => x.Id == Owner).Select(x => x.KeepOriginalImages).SingleAsync());
    }

    /// <summary>既存の画像を変換し直さず、次回の取り込み方針を変更する</summary>
    [HttpPost("/settings/images")]
    [ServiceFilter(typeof(AuthOperationFilter))]
    public async Task<IActionResult> Settings(bool keepOriginal)
    {
        var user = await database.Users.SingleAsync(x => x.Id == Owner);
        user.KeepOriginalImages = keepOriginal;
        await database.SaveChangesAsync();

        return Redirect("/settings/images");
    }
}
