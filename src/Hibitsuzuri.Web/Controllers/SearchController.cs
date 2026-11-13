using System.Globalization;
using System.Security.Claims;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>本人の日記の表示本文と同月同日の記録を探す</summary>
public sealed class SearchController(AppDbContext database) : Controller
{
    /// <summary>検索対象を制限する本人の識別子</summary>
    private string Owner => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException();

    /// <summary>日本語の短い部分一致と日付範囲で現在の日記を検索する</summary>
    [HttpGet("/search")]
    public async Task<IActionResult> Index(string? keyword, string? bookId, string? start, string? end, int page = 1)
    {
        var books = await database.Notebooks.Where(x => x.UserId == Owner).OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync();
        if (!Request.Query.ContainsKey("bookId"))
        {
            bookId = books.FirstOrDefault(x => x.Id == Request.Cookies[$"Hibitsuzuri.Book.{Owner}"])?.Id ?? books.FirstOrDefault()?.Id;
        }

        if (!string.IsNullOrEmpty(bookId) && books.All(x => x.Id != bookId))
        {
            return NotFound();
        }

        keyword = keyword?.Trim() ?? "";
        var validStart = ParseDate(start, out var first);
        var validEnd = ParseDate(end, out var last);
        var valid = validStart && validEnd && (first is null || last is null || first <= last)
            && AuthRules.ValidText(keyword, 0, 200) && page is >= 1 and <= 100000;
        var entries = new List<DiaryEntry>();
        if (valid && keyword.Length > 0)
        {
            var ids = books.Where(x => string.IsNullOrEmpty(bookId) || x.Id == bookId).Select(x => x.Id).ToArray();
            var query = DiarySearch.Normalize(keyword);
            entries = await database.DiaryEntries.AsNoTracking().Where(x => ids.Contains(x.NotebookId) && x.DeletedAt == null
                && (first == null || x.EntryDate >= first) && (last == null || x.EntryDate <= last)
                && x.SearchText != null && x.SearchText.Contains(query))
                .OrderByDescending(x => x.EntryDate).ThenBy(x => x.NotebookId).Skip((page - 1) * 50).Take(51).ToListAsync();
        }

        return View(new SearchPage
        {
            Keyword = keyword,
            BookId = bookId,
            Start = first,
            End = last,
            Page = page,
            Error = valid ? null : "検索語は200文字以内、日付は開始日から終了日の順で指定してください。",
            Books = books,
            Entries = entries.Take(50).ToList(),
            HasNext = entries.Count > 50
        });
    }

    /// <summary>年数を制限せず、2月29日は同じ月日の記録だけを表示する</summary>
    [HttpGet("/diary/{bookId}/{date}/years")]
    public async Task<IActionResult> Years(string bookId, string date)
    {
        var book = await database.Notebooks.SingleOrDefaultAsync(x => x.UserId == Owner && x.Id == bookId);
        if (book is null || !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return NotFound();
        }

        var entries = await database.DiaryEntries.AsNoTracking().Where(x => x.NotebookId == book.Id && x.DeletedAt == null
            && x.EntryDate.Month == day.Month && x.EntryDate.Day == day.Day).OrderByDescending(x => x.EntryDate).ToListAsync();

        return View(new DiaryYears { Book = book, Date = day, Entries = entries });
    }

    /// <summary>未指定と不正な日付を区別する</summary>
    private static bool ParseDate(string? value, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        date = parsed;
        return true;
    }
}
