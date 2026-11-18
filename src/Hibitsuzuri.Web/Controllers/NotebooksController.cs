using System.Security.Claims;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>本人の日記帳とテンプレートを管理する</summary>
[ServiceFilter(typeof(AuthOperationFilter))]
[RequestSizeLimit(1500000)]
// テンプレートの100,000文字は、URLエンコード後の長さとは分けて検証する。
[RequestFormLimits(ValueLengthLimit = 1200000)]
public sealed class NotebooksController(AppDbContext database, TimeProvider clock, DiaryMarkdown markdown, AuthAudit audit) : Controller
{
    /// <summary>認証済みの所有者ID</summary>
    private string Owner => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException();

    /// <summary>手動順の一覧と作成フォーム</summary>
    [HttpGet("/books")]
    public async Task<IActionResult> Index()
    {
        var dedicated = await database.Anniversaries.AsNoTracking().Include(x => x.Notebooks)
            .Where(x => x.UserId == Owner && !x.AllNotebooks && x.Notebooks.Count == 1).ToListAsync();
        ViewData["PurgeAnniversaryCounts"] = dedicated.GroupBy(x => x.Notebooks[0].NotebookId).ToDictionary(x => x.Key, x => x.Count());
        return View(await database.Notebooks.IgnoreQueryFilters().Where(x => x.UserId == Owner)
            .OrderBy(x => x.DeletedAt != null).ThenBy(x => x.Position).ThenBy(x => x.Id).ToListAsync());
    }

    /// <summary>空の日記帳を作成する。日記の空レコードは作らない</summary>
    [HttpPost("/books")]
    public async Task<IActionResult> Create(string? name)
    {
        var normalized = name?.Trim();
        if (!AuthRules.ValidText(normalized, 1, 100) || await Books().AnyAsync(x => x.Name == normalized))
        {
            ModelState.AddModelError("", "日記帳名は1～100文字で、ほかの日記帳と異なる名前にしてください。");
            Response.StatusCode = 400;
            return View("Index", await Books().ToListAsync());
        }

        var lastPosition = await Books().Select(x => (int?)x.Position).MaxAsync() ?? -1;
        var book = new Notebook { UserId = Owner, Name = normalized ?? "", Position = lastPosition + 1 };
        database.Notebooks.Add(book);
        await database.SaveChangesAsync();

        return Redirect($"/books/{book.Id}");
    }

    /// <summary>名前とテンプレートの編集画面</summary>
    [HttpGet("/books/{id}")]
    public async Task<IActionResult> Edit(string id)
    {
        var book = await Books().SingleOrDefaultAsync(x => x.Id == id);
        return book is null ? NotFound() : View(book);
    }

    /// <summary>読み込んだ版が一致するときだけ設定を保存する</summary>
    [HttpPost("/books/{id}")]
    public async Task<IActionResult> Edit(string id, string? name, string? template, string? version)
    {
        var book = await Books().SingleOrDefaultAsync(x => x.Id == id);
        if (book is null)
        {
            return NotFound();
        }

        name = name?.Trim();
        template = DiaryMarkdown.NormalizeLineEndings(template ?? "");
        if (version != book.Version)
        {
            ModelState.AddModelError("", "別の画面で設定が更新されています。入力内容を控え、再読み込みして確認してください。");
            Response.StatusCode = 409;
        }
        else if (!AuthRules.ValidText(name, 1, 100) || !AuthRules.ValidText(template, 0, 100000)
            || await Books().AnyAsync(x => x.Id != id && x.Name == name))
        {
            ModelState.AddModelError("", "名前は重複しない1～100文字、テンプレートは100,000文字以内で入力してください。");
            Response.StatusCode = 400;
        }

        if (!ModelState.IsValid)
        {
            // 競合時も入力を保持し、古い版のまま再送して上書きできないようにする。
            return View(new Notebook { Id = id, Name = name ?? "", Template = template, Version = version ?? "", TasksPausedOn = book.TasksPausedOn, Archived = book.Archived });
        }

        book.Name = name ?? "";
        book.Template = template;
        book.Version = Guid.NewGuid().ToString("N");

        await database.SaveChangesAsync();

        return Redirect("/books");
    }

    /// <summary>最新の並び順で隣り合う日記帳を入れ替える</summary>
    [HttpPost("/books/{id}/move")]
    public async Task<IActionResult> Move(string id, int direction)
    {
        var books = await Books().ToListAsync();
        var selected = books.FirstOrDefault(x => x.Id == id);
        books = books.Where(x => x.Archived == selected?.Archived).ToList();
        var index = books.FindIndex(x => x.Id == id);
        if (index < 0)
        {
            return NotFound();
        }

        if (direction is not (-1 or 1))
        {
            return BadRequest();
        }

        var target = index + direction;
        if (target >= 0 && target < books.Count)
        {
            // 折りたたんだアーカイブ一覧の位置を動かさず、同じ一覧内だけを入れ替える。
            (books[index].Position, books[target].Position) = (books[target].Position, books[index].Position);

            await database.SaveChangesAsync();
        }

        return Redirect("/books");
    }

    /// <summary>管理者も本人の所有範囲に限定する</summary>
    private IOrderedQueryable<Notebook> Books() => database.Notebooks.Where(x => x.UserId == Owner)
        .OrderBy(x => x.Position).ThenBy(x => x.Id);

    /// <summary>削除前の状態を保持し、復元・完全削除を本人の明示確認で行う</summary>
    [HttpPost("/books/{id}/state")]
    public async Task<IActionResult> State(string id, string operation, string version, bool confirmed, bool pauseTasks, string? name, int? anniversaryCount)
    {
        var book = await database.Notebooks.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id && x.UserId == Owner);
        if (book is null)
        {
            return NotFound();
        }

        if (!confirmed || operation is not ("archive" or "unarchive" or "delete" or "restore" or "purge"))
        {
            return BadRequest("対象と操作内容を確認してください。");
        }

        if (book.Version != version || ((operation is "restore" or "purge") != (book.DeletedAt is not null)))
        {
            return Conflict("日記帳の状態が変わりました。一覧へ戻り、確認してください。");
        }

        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        await using var transaction = await database.Database.BeginTransactionAsync();
        switch (operation)
        {
            case "delete":
                book.DeletedAt = now;
                break;
            case "restore":
                name = name?.Trim();
                if (!AuthRules.ValidText(name, 1, 100) || await Books().AnyAsync(x => x.Name == name))
                {
                    return Conflict("使用中・アーカイブ中の日記帳と異なる名前を指定してください。");
                }

                var deletedDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(book.DeletedAt.GetValueOrDefault()), clock.LocalTimeZone).DateTime);
                if (deletedDay < today)
                {
                    database.TaskPauses.Add(new()
                    {
                        NotebookId = book.Id,
                        From = deletedDay,
                        Until = today
                    });
                }

                book.Name = name ?? "";
                book.DeletedAt = null;
                book.NotificationAfter = now;
                break;
            case "purge":
                if (!string.Equals(name, book.Name, StringComparison.Ordinal))
                {
                    return BadRequest("完全削除する日記帳名を正確に入力してください。");
                }

                // 他の日記帳でも参照している画像を、元の日記帳の削除に巻き込まない。
                var shared = await (from image in database.DiaryImages
                                    join reference in database.DiaryImageReferences on image.Id equals reference.ImageId
                                    join entry in database.DiaryEntries on reference.EntryId equals entry.Id
                                    where image.NotebookId == id && entry.NotebookId != id
                                    select new
                                    {
                                        image.Id,
                                        entry.NotebookId
                                    }).ToListAsync();
                var templateBooks = await database.Notebooks.IgnoreQueryFilters().Where(x => x.UserId == Owner && x.Id != id)
                    .Select(x => new { x.Id, x.Template }).ToListAsync();
                var sourceImages = await database.DiaryImages.Where(x => x.NotebookId == id).Select(x => x.Id).ToListAsync();
                foreach (var templateBook in templateBooks)
                {
                    foreach (var imageId in markdown.ImageIds(templateBook.Template).Intersect(sourceImages))
                    {
                        shared.Add(new
                        {
                            Id = imageId,
                            NotebookId = templateBook.Id
                        });
                    }
                }

                foreach (var image in shared.GroupBy(x => x.Id))
                {
                    var destination = image.First().NotebookId;
                    await database.DiaryImages.Where(x => x.Id == image.Key).ExecuteUpdateAsync(set => set.SetProperty(x => x.NotebookId, destination));
                }

                var anniversaries = await database.Anniversaries.Include(x => x.Notebooks)
                    .Where(x => x.UserId == Owner && !x.AllNotebooks && x.Notebooks.Any(n => n.NotebookId == id)).ToListAsync();
                if (anniversaryCount != anniversaries.Count(x => x.Notebooks.Count == 1))
                {
                    return Conflict("同時に削除する記念日の件数が変わりました。一覧で確認し直してください。");
                }

                database.Anniversaries.RemoveRange(anniversaries.Where(x => x.Notebooks.Count == 1));
                database.Notebooks.Remove(book);
                break;
            default:
                book.Archived = operation == "archive";
                var pause = book.Archived && pauseTasks;
                if (pause && book.TasksPausedOn is null)
                {
                    book.TasksPausedOn = today;
                }
                else if (!pause && book.TasksPausedOn is { } started)
                {
                    if (started < today)
                    {
                        database.TaskPauses.Add(new()
                        {
                            NotebookId = book.Id,
                            From = started,
                            Until = today
                        });
                    }

                    book.TasksPausedOn = null;
                    book.NotificationAfter = now;
                }

                break;
        }

        book.Version = Guid.NewGuid().ToString("N");
        audit.Add(HttpContext, "notebook." + operation, Owner);
        await database.SaveChangesAsync();
        await transaction.CommitAsync();
        return Redirect("/books");
    }
}
