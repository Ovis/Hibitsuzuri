using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Images;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>日付単位の日記・履歴・ゴミ箱を所有者の範囲で扱う</summary>
[RequestSizeLimit(1500000)]
// URLエンコードでは1文字が最大12バイトになる。本文自体の上限は別途スカラー値で検証する。
[RequestFormLimits(ValueLengthLimit = 1200000)]
public sealed class DiaryController(AppDbContext database, TimeProvider clock, DiaryMarkdown markdown,
    Hibitsuzuri.Web.Calendar.CalendarReader calendar, ImageStore images) : Controller
{
    /// <summary>管理者であっても越えられない所有者境界</summary>
    private string Owner => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException();

    /// <summary>日付判定はブラウザの時計ではなくホストのタイムゾーンに揃える</summary>
    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    /// <summary>ユーザーごと・ブラウザごとの日記帳選択</summary>
    private string SelectionCookie => $"Hibitsuzuri.Book.{Owner}";

    /// <summary>最後に開いた本人の日記帳の今日へ移動する</summary>
    [HttpGet("/")]
    public async Task<IActionResult> Home()
    {
        var books = await Books().ToListAsync();
        var selected = books.FirstOrDefault(x => x.Id == Request.Cookies[SelectionCookie]) ?? books.FirstOrDefault();

        return Redirect(selected is null ? "/books" : $"/diary/{selected.Id}/{Day(Today)}");
    }

    /// <summary>日記帳を開くときは今日を表示する</summary>
    [HttpGet("/diary/{bookId}")]
    public async Task<IActionResult> Open(string bookId)
    {
        return await Books().AnyAsync(x => x.Id == bookId)
            ? Redirect($"/diary/{bookId}/{Day(Today)}") : NotFound();
    }

    /// <summary>未作成・ゴミ箱・未来日を空のレコードを作らず表示する</summary>
    [HttpGet("/diary/{bookId}/{date}")]
    public async Task<IActionResult> Read(string bookId, string date)
    {
        var page = await PageAsync(bookId, date);
        if (page is null)
        {
            return NotFound();
        }

        Response.Cookies.Append(SelectionCookie, bookId, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = TimeSpan.FromDays(365),
            IsEssential = true
        });

        return View(page);
    }

    /// <summary>未来とゴミ箱の編集を拒否し、入力前の版と履歴を渡す</summary>
    [HttpGet("/diary/{bookId}/{date}/edit")]
    public async Task<IActionResult> Edit(string bookId, string date)
    {
        var page = await PageAsync(bookId, date);
        if (page is null)
        {
            return NotFound();
        }

        return page.Date > Today || page.Entry?.DeletedAt is not null ? Redirect(page.Path) : View(page);
    }

    /// <summary>保存せずに共通Markdown規則のプレビューを返す</summary>
    [HttpPost("/api/diary/preview")]
    public async Task<IActionResult> Preview(string? content)
    {
        content = DiaryMarkdown.NormalizeLineEndings(content ?? "");
        if (!AuthRules.ValidText(content ?? "", 0, 100000))
        {
            return BadRequest(new
            {
                message = "本文は100,000文字以内で入力してください。"
            });
        }

        return Json(new
        {
            html = await images.RenderAsync(Owner, content ?? "")
        });
    }

    /// <summary>本文・過去版・再送記録を同じトランザクションで確定する</summary>
    [HttpPost("/api/diary/{bookId}/{date}/save")]
    [ServiceFilter(typeof(AuthOperationFilter))]
    public async Task<IActionResult> Save(string bookId, string date, string? content, string? version, string? operationId)
    {
        if (!await Books().AnyAsync(x => x.Id == bookId))
        {
            return NotFound();
        }

        content = DiaryMarkdown.NormalizeLineEndings(content ?? "");
        if (!ParseDay(date, out var day) || day > Today || string.IsNullOrWhiteSpace(content)
            || !AuthRules.ValidText(content, 1, 100000) || !Guid.TryParseExact(operationId, "D", out _)
            || (!string.IsNullOrEmpty(version) && !Guid.TryParseExact(version, "N", out _)))
        {
            return BadRequest(new
            {
                message = "未来の日付には保存できません。本文は空白以外の1～100,000文字で入力してください。"
            });
        }

        version ??= "";
        var hash = AuthRules.Hash(JsonSerializer.Serialize(new
        {
            date,
            content,
            version
        }));
        var prior = await database.DiarySaveOperations.FindAsync(bookId, operationId);
        if (prior is not null)
        {
            // 日記がその後に更新・完全削除されても、元の保存を二重適用しない。
            return prior.RequestHash == hash ? Json(new
            {
                saved = true,
                version = prior.ResultVersion,
                replayed = true
            })
                : Conflict(new
                {
                    message = "同じ保存IDで異なる内容は送信できません。再読み込みして確認してください。"
                });
        }

        var referencedImages = markdown.ImageIds(content);
        if (referencedImages.Count > 0)
        {
            var available = (await images.AvailableAsync(Owner, referencedImages)).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            if (!referencedImages.IsSubsetOf(available))
            {
                return BadRequest(new
                {
                    message = "利用できない画像が含まれます。期限切れの画像を再選択するか、本文から画像の参照を削除してください。本文は編集欄に残っています。"
                });
            }
        }

        await using var transaction = await database.Database.BeginTransactionAsync();
        var entry = await database.DiaryEntries.SingleOrDefaultAsync(x => x.NotebookId == bookId && x.EntryDate == day);
        if ((entry?.Version ?? "") != version || entry?.DeletedAt is not null)
        {
            return Conflict(new
            {
                message = "別の画面で日記が変更されています。最新版と比較してください。",
                latest = new
                {
                    content = entry?.Content ?? "",
                    version = entry?.Version ?? "",
                    deleted = entry?.DeletedAt is not null
                }
            });
        }

        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (entry is null)
        {
            entry = new DiaryEntry { NotebookId = bookId, EntryDate = day, Content = content, CreatedAt = now, UpdatedAt = now };
            database.DiaryEntries.Add(entry);
        }
        else if (entry.Content != content)
        {
            var revisions = await database.DiaryRevisions.Where(x => x.EntryId == entry.Id).OrderByDescending(x => x.Sequence).ToListAsync();
            database.DiaryRevisions.Add(new DiaryRevision
            {
                Id = entry.Version,
                EntryId = entry.Id,
                Content = entry.Content,
                UpdatedAt = entry.UpdatedAt,
                Sequence = (revisions.FirstOrDefault()?.Sequence ?? 0) + 1
            });
            database.DiaryRevisions.RemoveRange(revisions.Skip(4));

            entry.Content = content;
            entry.UpdatedAt = now;
            entry.Version = Guid.NewGuid().ToString("N");
        }

        entry.PlainText = markdown.PlainText(content);
        entry.SearchText = DiarySearch.Normalize(entry.PlainText);

        database.DiarySaveOperations.Add(new DiarySaveOperation
        {
            NotebookId = bookId,
            Id = operationId ?? "",
            RequestHash = hash,
            ResultVersion = entry.Version
        });
        await database.SaveChangesAsync();
        await images.UpdateReferencesAsync(entry);
        await transaction.CommitAsync();

        return Json(new
        {
            saved = true,
            version = entry.Version,
            replayed = false
        });
    }

    /// <summary>日記帳内のゴミ箱だけを列挙し、確認時の集合を固定する</summary>
    [HttpGet("/diary/{bookId}/trash")]
    public async Task<IActionResult> Trash(string bookId)
    {
        var book = await Books().SingleOrDefaultAsync(x => x.Id == bookId);
        if (book is null)
        {
            return NotFound();
        }

        var entries = await Trashed(bookId).OrderByDescending(x => x.EntryDate).ToListAsync();

        return View(new DiaryTrash { Book = book, Entries = entries, Snapshot = Snapshot(entries) });
    }

    /// <summary>版が一致する日記だけを削除・復元・完全削除する</summary>
    [HttpPost("/diary/{bookId}/{date}/trash")]
    [ServiceFilter(typeof(AuthOperationFilter))]
    public async Task<IActionResult> ChangeTrash(string bookId, string date, string? version, string? action)
    {
        if (!await Books().AnyAsync(x => x.Id == bookId) || !ParseDay(date, out var day))
        {
            return NotFound();
        }

        var entry = await database.DiaryEntries.SingleOrDefaultAsync(x => x.NotebookId == bookId && x.EntryDate == day);
        if (entry is null)
        {
            return NotFound();
        }

        if (entry.Version != version || (action == "delete" ? entry.DeletedAt is not null : entry.DeletedAt is null))
        {
            return Conflict("日記の状態が変わりました。再読み込みして確認してください。");
        }

        switch (action)
        {
            case "delete":
                entry.DeletedAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
                break;
            case "restore":
                entry.DeletedAt = null;
                break;
            case "purge":
                database.DiaryEntries.Remove(entry);
                break;
            default:
                return BadRequest();
        }

        entry.Version = Guid.NewGuid().ToString("N");

        await database.SaveChangesAsync();

        return Redirect(action == "purge" ? $"/diary/{bookId}/trash" : $"/diary/{bookId}/{date}");
    }

    /// <summary>確認した件数・名前・集合が一致する場合だけゴミ箱を空にする</summary>
    [HttpPost("/diary/{bookId}/trash/empty")]
    [ServiceFilter(typeof(AuthOperationFilter))]
    public async Task<IActionResult> EmptyTrash(string bookId, string? name, string? snapshot, int count)
    {
        var book = await Books().SingleOrDefaultAsync(x => x.Id == bookId);
        if (book is null)
        {
            return NotFound();
        }

        var entries = await Trashed(bookId).ToListAsync();
        if (book.Name != name || entries.Count != count || Snapshot(entries) != snapshot)
        {
            return Conflict("名前またはゴミ箱の内容が変わりました。再読み込みして確認してください。");
        }

        database.DiaryEntries.RemoveRange(entries);
        await database.SaveChangesAsync();

        return Redirect($"/diary/{bookId}/trash");
    }

    /// <summary>本人の所有する日記帳を手動順で取得する</summary>
    private IOrderedQueryable<Notebook> Books() => database.Notebooks.Where(x => x.UserId == Owner).OrderBy(x => x.Position).ThenBy(x => x.Id);

    /// <summary>完全削除の対象を選択中の日記帳の論理削除済みに限定する</summary>
    private IQueryable<DiaryEntry> Trashed(string bookId) => database.DiaryEntries.Where(x => x.NotebookId == bookId && x.DeletedAt != null);

    /// <summary>所有者と日付を検証してから本文・履歴を読む</summary>
    private async Task<DiaryPage?> PageAsync(string bookId, string date)
    {
        var books = await Books().ToListAsync();
        var book = books.FirstOrDefault(x => x.Id == bookId);
        if (book is null || !ParseDay(date, out var day))
        {
            return null;
        }

        var entry = await database.DiaryEntries.SingleOrDefaultAsync(x => x.NotebookId == bookId && x.EntryDate == day);
        var revisions = entry is null ? [] : await database.DiaryRevisions.Where(x => x.EntryId == entry.Id)
            .OrderByDescending(x => x.Sequence).ToListAsync();

        return new DiaryPage
        {
            Book = book,
            Books = books,
            Date = day,
            Today = Today,
            Entry = entry,
            Revisions = revisions,
            Calendar = await calendar.ReadAsync(Owner, bookId, day),
            Html = entry is null ? "" : await images.RenderAsync(Owner, entry.Content)
        };
    }

    /// <summary>カルチャーに左右されない日付URL</summary>
    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>年月日以外の曖昧な入力を受け付けない</summary>
    private static bool ParseDay(string date, out DateOnly day) => DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    /// <summary>復元と再削除を挟んだ同じ件数の集合も区別する</summary>
    private static string Snapshot(IEnumerable<DiaryEntry> entries) => AuthRules.Hash(string.Join("|", entries.OrderBy(x => x.Id).Select(x => $"{x.Id}:{x.Version}")));
}
