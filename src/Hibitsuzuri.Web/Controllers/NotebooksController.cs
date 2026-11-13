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
public sealed class NotebooksController(AppDbContext database) : Controller
{
    /// <summary>認証済みの所有者ID</summary>
    private string Owner => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException();

    /// <summary>手動順の一覧と作成フォーム</summary>
    [HttpGet("/books")]
    public async Task<IActionResult> Index() => View(await Books().ToListAsync());

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

        var book = new Notebook { UserId = Owner, Name = normalized ?? "", Position = await Books().CountAsync() };
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
            return View(new Notebook { Id = id, Name = name ?? "", Template = template, Version = version ?? "" });
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
            (books[index], books[target]) = (books[target], books[index]);
            for (var i = 0; i < books.Count; i++)
            {
                books[i].Position = i;
            }

            await database.SaveChangesAsync();
        }

        return Redirect("/books");
    }

    /// <summary>管理者も本人の所有範囲に限定する</summary>
    private IOrderedQueryable<Notebook> Books() => database.Notebooks.Where(x => x.UserId == Owner)
        .OrderBy(x => x.Position).ThenBy(x => x.Id);
}
