using Hibitsuzuri.Web.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>認証済みユーザー本人のアカウント設定</summary>
[ServiceFilter<AuthOperationFilter>]
public sealed class AccountController(UserManager<AppUser> users) : Controller
{
    /// <summary>本人の情報だけを表示する</summary>
    [HttpGet("/account")]
    public async Task<IActionResult> Index()
    {
        var user = await users.GetUserAsync(User);
        return user is null ? Unauthorized() : View(user);
    }
}
