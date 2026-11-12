using System.Security.Claims;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Models;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>認証済みユーザー本人のアカウント設定</summary>
[ServiceFilter<AuthOperationFilter>]
[EnableRateLimiting("authentication")]
public sealed class AccountController(UserManager<AppUser> users, AppDbContext db, SessionService sessions,
    PasswordAuthentication passwords, RecoveryCodes codes, AccountFlows flows, AuthAudit audit) : Controller
{
    /// <summary>本人の情報だけを表示する</summary>
    [HttpGet("/account")]
    public async Task<IActionResult> Index()
    {
        var user = await users.GetUserAsync(User);
        return user is null ? Unauthorized() : View(user);
    }

    /// <summary>重要操作前に使用する本人確認画面</summary>
    [HttpGet("/account/reauthenticate")]
    public IActionResult Reauthenticate(string? returnUrl = null)
    {
        return View(new LoginModel { ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : "/account" });
    }

    /// <summary>ログイン期限を変えず、現在のセッションだけを再認証する</summary>
    [HttpPost("/account/reauthenticate")]
    public async Task<IActionResult> Reauthenticate(string password, string? returnUrl = null)
    {
        var user = await users.GetUserAsync(User);
        if (user?.UserName is null || !AuthRules.ValidText(password, 1, 128)
            || await passwords.VerifyAsync(HttpContext, user.UserName, password) is null)
        {
            ModelState.AddModelError("", "本人確認に失敗しました。制限中の場合は時間をおいて再試行してください。");
            return View(new LoginModel { ReturnUrl = returnUrl });
        }

        await sessions.MarkVerifiedAsync(User);
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/account");
    }

    /// <summary>最近の本人確認を要求してパスワード変更画面を表示する</summary>
    [HttpGet("/account/password")]
    public async Task<IActionResult> Password()
    {
        return await sessions.IsRecentAsync(User) ? View(new NewPasswordModel())
            : Redirect("/account/reauthenticate?returnUrl=/account/password");
    }

    /// <summary>操作中の端末の期限を維持し、他端末だけを失効する</summary>
    [HttpPost("/account/password")]
    public async Task<IActionResult> Password(NewPasswordModel model)
    {
        if (!await sessions.IsRecentAsync(User))
        {
            return StatusCode(403, "本人確認の期限が切れました。別のタブで再認証してから、もう一度保存してください。");
        }

        if (!AuthRules.ValidText(model.Password, 12, 128))
        {
            ModelState.AddModelError(nameof(model.Password), "パスワードは12～128文字で入力してください。");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        var token = await users.GeneratePasswordResetTokenAsync(user);
        AuthRules.RequireSuccess(await users.ResetPasswordAsync(user, token, model.Password));
        var sessionId = User.FindFirstValue(SessionService.SessionClaim);
        await sessions.RevokeAsync(user.Id, sessionId);
        await db.AuthSessions.Where(x => x.Id == sessionId).ExecuteUpdateAsync(set => set.SetProperty(x => x.SecurityStamp, user.SecurityStamp ?? ""));
        await db.AccountFlows.Where(x => x.UserId == user.Id).ExecuteDeleteAsync();
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        audit.Add(HttpContext, "password-change", user.Id);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return RedirectToAction(nameof(Index));
    }

    /// <summary>再認証後の明示操作で旧コードを全失効し、一度限り表示する</summary>
    [HttpPost("/account/codes")]
    public async Task<IActionResult> RegenerateCodes(bool confirmed)
    {
        if (!await sessions.IsRecentAsync(User) || !confirmed)
        {
            return StatusCode(403, "直近10分以内の本人確認と、再生成の確認が必要です。");
        }

        var user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        var freshCodes = await codes.ReplaceAsync(user.Id);
        await flows.BeginAsync(HttpContext, user, "confirm-codes");
        audit.Add(HttpContext, "recovery-codes-replaced", user.Id);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return View("~/Views/Auth/Codes.cshtml", new RecoveryCodesModel { Codes = freshCodes });
    }
}
