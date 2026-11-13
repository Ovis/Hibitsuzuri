using System.Globalization;
using System.Security.Claims;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Calendar;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>所有者の月表示と年ごとの記念日を扱う</summary>
public sealed class CalendarController(AppDbContext database, CalendarReader calendar, TimeProvider clock) : Controller
{
    /// <summary>日記帳と記念日の所有者</summary>
    private string Owner => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException();

    /// <summary>任意の年月へ移動できるカレンダー</summary>
    [HttpGet("/calendar")]
    public async Task<IActionResult> Index(string? bookId, string? month)
    {
        var date = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        if (month is not null && !DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return BadRequest("年月を確認してください。");
        }

        var model = await calendar.ReadAsync(Owner, bookId ?? Request.Cookies[$"Hibitsuzuri.Book.{Owner}"], date);
        if (model is null && bookId is null)
        {
            model = await calendar.ReadAsync(Owner, null, date);
        }

        return model is null ? (bookId is null ? Redirect("/books") : NotFound()) : View(model);
    }

    /// <summary>本人の記念日と表示先の選択肢を表示する</summary>
    [HttpGet("/anniversaries")]
    public async Task<IActionResult> Anniversaries(string? id)
    {
        var items = await database.Anniversaries.Include(x => x.Notebooks).Where(x => x.UserId == Owner)
            .OrderBy(x => x.Month).ThenBy(x => x.Day).ThenBy(x => x.Title).ToListAsync();
        var editing = id is null ? new Anniversary() : items.FirstOrDefault(x => x.Id == id);
        if (editing is null)
        {
            return NotFound();
        }

        return View(new AnniversaryPage
        {
            Items = items,
            Editing = editing,
            IsNew = id is null,
            Books = await database.Notebooks.Where(x => x.UserId == Owner).OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync()
        });
    }

    /// <summary>複数日記帳の所有者と変更版を確認して記念日を保存する</summary>
    [HttpPost("/anniversaries")]
    [ServiceFilter(typeof(AuthOperationFilter))]
    public async Task<IActionResult> Save(string? id, string? version, string? title, int month, int day,
        string? leapDayPolicy, bool allNotebooks, string[]? bookIds)
    {
        title = title?.Trim() ?? "";
        var ids = (bookIds ?? []).Distinct().ToArray();
        if (!AuthRules.ValidText(title, 1, 100) || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(2000, month)
            || leapDayPolicy is not ("skip" or "feb28" or "mar1") || (!allNotebooks && ids.Length == 0))
        {
            return BadRequest("名前・月日・閏日の扱いと表示先を確認してください。");
        }

        if (await database.Notebooks.CountAsync(x => x.UserId == Owner && ids.Contains(x.Id)) != ids.Length)
        {
            return NotFound();
        }

        var item = string.IsNullOrEmpty(id) ? new Anniversary { UserId = Owner }
            : await database.Anniversaries.Include(x => x.Notebooks).SingleOrDefaultAsync(x => x.Id == id && x.UserId == Owner);
        if (item is null)
        {
            return NotFound();
        }

        if (!string.IsNullOrEmpty(id) && item.Version != version)
        {
            return Conflict("記念日が変更されました。再読み込みして確認してください。");
        }

        item.Title = title;
        item.Month = month;
        item.Day = day;
        item.LeapDayPolicy = leapDayPolicy;
        item.AllNotebooks = allNotebooks;
        item.Version = Guid.NewGuid().ToString("N");
        var selected = allNotebooks ? [] : ids;
        item.Notebooks.RemoveAll(x => !selected.Contains(x.NotebookId));
        item.Notebooks.AddRange(selected.Where(x => item.Notebooks.All(n => n.NotebookId != x))
            .Select(x => new AnniversaryNotebook { AnniversaryId = item.Id, NotebookId = x }));
        if (string.IsNullOrEmpty(id))
        {
            database.Anniversaries.Add(item);
        }

        await database.SaveChangesAsync();

        return Redirect("/anniversaries");
    }

    /// <summary>確認した版の記念日だけを削除する</summary>
    [HttpPost("/anniversaries/{id}/delete")]
    [ServiceFilter(typeof(AuthOperationFilter))]
    public async Task<IActionResult> Delete(string id, string? version)
    {
        var item = await database.Anniversaries.SingleOrDefaultAsync(x => x.Id == id && x.UserId == Owner);
        if (item is null)
        {
            return NotFound();
        }

        if (item.Version != version)
        {
            return Conflict("記念日が変更されました。再読み込みして確認してください。");
        }

        database.Anniversaries.Remove(item);
        await database.SaveChangesAsync();

        return Redirect("/anniversaries");
    }
}
