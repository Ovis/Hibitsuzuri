using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Models;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>外観・通知と本人のアカウント管理をまとめる設定画面</summary>
public sealed class SettingsController(UserManager<AppUser> users, AppDbContext database) : Controller
{
    /// <summary>表示・通知・アカウントとデータ管理への入口をまとめる</summary>
    [HttpGet("/settings")]
    public async Task<IActionResult> Index()
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        var selection = Request.Cookies[$"Hibitsuzuri.Book.{user.Id}"];
        var book = await database.Notebooks.Where(x => x.UserId == user.Id && (!x.Archived || x.Id == selection))
            .OrderByDescending(x => x.Id == selection).ThenBy(x => x.Position).ThenBy(x => x.Id)
            .Select(x => x.Id).FirstOrDefaultAsync();

        return View(new SettingsPage
        {
            User = user,
            Passkeys = await users.GetPasskeysAsync(user),
            TrashPath = book is null ? "/books" : $"/diary/{book}/trash"
        });
    }

    /// <summary>テーマとライト配色の選択肢を表示する</summary>
    [HttpGet("/settings/appearance")]
    public IActionResult Appearance()
    {
        return View();
    }
}
