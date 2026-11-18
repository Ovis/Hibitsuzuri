using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Calendar;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>管理者だけが祝日の更新状況と手動更新を操作する</summary>
public sealed class HolidaysController(AppDbContext db, SessionService sessions, HolidayUpdates updates) : Controller
{
    /// <summary>同梱値と外部更新の成功・失敗を区別して表示する</summary>
    [HttpGet("/admin/holidays")]
    public async Task<IActionResult> Index()
    {
        if ((await sessions.CurrentUserAsync(User))?.IsAdmin != true)
        {
            return Forbid();
        }

        return View(await db.HolidayStates.AsNoTracking().SingleAsync());
    }

    /// <summary>自動更新と同じ取得・検証・入れ替え処理を明示実行する</summary>
    [HttpPost("/admin/holidays/update")]
    public async Task<IActionResult> Update()
    {
        if ((await sessions.CurrentUserAsync(User))?.IsAdmin != true)
        {
            return Forbid();
        }

        return await updates.UpdateAsync(User, HttpContext.RequestAborted)
            ? RedirectToAction(nameof(Index)) : Forbid();
    }

    /// <summary>同梱配布物の出典と加工内容を案内する</summary>
    [HttpGet("/about")]
    public IActionResult About() => View();
}
