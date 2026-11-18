using System.Security.Claims;
using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Navigation;

/// <summary>本人の日記帳と表示中の画面に合わせた共通ナビゲーション</summary>
public sealed class NavigationViewComponent(AppDbContext database) : ViewComponent
{
    /// <summary>所有者の範囲で日記帳の切替先を組み立てる</summary>
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var owner = UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException();
        var books = await database.Notebooks.AsNoTracking().Where(x => x.UserId == owner)
            .OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync();
        var requested = RouteData.Values["bookId"] as string ?? Request.Query["bookId"].FirstOrDefault()
            ?? Request.Cookies[$"Hibitsuzuri.Book.{owner}"];
        var selected = books.FirstOrDefault(x => x.Id == requested) ?? books.FirstOrDefault(x => !x.Archived);
        var date = RouteData.Values["date"] as string;
        var path = Request.Path.Value ?? "/";
        var links = new Dictionary<string, string>();

        foreach (var book in books.Where(x => !x.Archived || x.Id == selected?.Id))
        {
            if (path is "/calendar" or "/search" or "/tasks")
            {
                var query = QueryString.Create(Request.Query.Where(x => x.Key is not "bookId" and not "page"))
                    .Add("bookId", book.Id);
                links.Add(book.Id, $"/diary/{book.Id}?returnUrl={Uri.EscapeDataString(path + query)}");
            }
            else
            {
                links.Add(book.Id, $"/diary/{book.Id}" + (date is null ? "" : $"/{date}"));
            }
        }

        return View(new NavigationPage
        {
            Books = books.Where(x => links.ContainsKey(x.Id)).ToList(),
            Selected = selected,
            Links = links,
            Section = ViewContext.ViewData["Navigation"] as string ?? "settings"
        });
    }
}

/// <summary>デスクトップとモバイルで共有するナビゲーションの表示材料</summary>
public sealed class NavigationPage
{
    /// <summary>通常の切替候補と、明示的に開いているアーカイブ日記帳</summary>
    public required IReadOnlyList<Notebook> Books
    {
        get; init;
    }

    /// <summary>現在の日記帳。未作成の場合は未選択</summary>
    public Notebook? Selected
    {
        get; init;
    }

    /// <summary>現在の検索条件や日付を引き継いだ切替先</summary>
    public required IReadOnlyDictionary<string, string> Links
    {
        get; init;
    }

    /// <summary>選択状態を示すメニュー項目</summary>
    public required string Section
    {
        get; init;
    }
}
