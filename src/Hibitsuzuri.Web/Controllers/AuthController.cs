using System.Security.Cryptography;
using System.Text;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Models;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>初回管理者・自己登録・通常ログインのMVC導線</summary>
[AllowAnonymous]
[ServiceFilter<AuthOperationFilter>]
[EnableRateLimiting("authentication")]
[RequestSizeLimit(16384)]
[RequestFormLimits(ValueLengthLimit = 1024)]
public sealed class AuthController(AppDbContext db, UserManager<AppUser> users, SessionService sessions,
    PasswordAuthentication passwords, RecoveryCodes codes, AccountFlows flows, AuthAudit audit,
    PasskeyCeremonies ceremonies, IConfiguration configuration) : Controller
{
    /// <summary>セットアップが必要なら登録へ案内し、通常はログイン画面を表示する</summary>
    [HttpGet("/auth/login")]
    public async Task<IActionResult> Login(string? returnUrl = null)
    {
        if (!await db.AccountPolicies.AnyAsync(x => x.SetupCompleted))
        {
            return RedirectToAction(nameof(Register));
        }

        ViewData["RegistrationEnabled"] = await db.AccountPolicies.AnyAsync(x => x.RegistrationEnabled);
        return View(new LoginModel { ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null });
    }

    /// <summary>パスワードを検証し、未完了のコード保存確認は通常ログインから分離する</summary>
    [HttpPost("/auth/login")]
    public async Task<IActionResult> Login(LoginModel model)
    {
        var user = ModelState.IsValid ? await passwords.VerifyAsync(HttpContext, model.LoginId, model.Password) : null;
        if (user is null)
        {
            ModelState.AddModelError("", "ログインできませんでした。入力内容を確認し、制限中の場合は時間をおいて再試行してください。");
            ViewData["RegistrationEnabled"] = await db.AccountPolicies.AnyAsync(x => x.RegistrationEnabled);
            return View(model);
        }

        if (!user.RecoveryCodesConfirmed)
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var freshCodes = await codes.ReplaceAsync(user.Id);
            await flows.BeginAsync(HttpContext, user, "confirm-login", model.RememberMe);
            await transaction.CommitAsync();
            return View("Codes", new RecoveryCodesModel { Codes = freshCodes });
        }

        await sessions.SignInAsync(HttpContext, user, model.RememberMe);
        return LocalRedirect(Url.IsLocalUrl(model.ReturnUrl) ? model.ReturnUrl : "/");
    }

    /// <summary>初回登録と通常登録で、サーバー上の受付状態を表示する</summary>
    [HttpGet("/auth/register")]
    public async Task<IActionResult> Register()
    {
        var policy = await db.AccountPolicies.SingleAsync();
        if (policy.SetupCompleted && !policy.RegistrationEnabled)
        {
            return StatusCode(403);
        }

        return View(new RegisterModel { IsSetup = !policy.SetupCompleted });
    }

    /// <summary>排他内で受付と初回コードを検証し、アカウント・復旧コードをまとめて確定する</summary>
    [HttpPost("/auth/register")]
    public async Task<IActionResult> Register(RegisterModel model)
    {
        var policy = await db.AccountPolicies.SingleAsync();
        model.IsSetup = !policy.SetupCompleted;
        if (policy.SetupCompleted && !policy.RegistrationEnabled)
        {
            return StatusCode(403);
        }

        // 競合で初回登録から通常登録へ変わった要求を、別ユーザーの登録として通さない。
        if (policy.SetupCompleted && !string.IsNullOrEmpty(model.SetupCode))
        {
            return StatusCode(409);
        }

        if (!AuthRules.ValidLoginId(model.LoginId))
        {
            ModelState.AddModelError(nameof(model.LoginId), "ユーザーIDは3～64文字の半角英数字・ピリオド・ハイフン・アンダースコアで入力してください。");
        }

        if (!AuthRules.ValidText(model.Password, 12, 128))
        {
            ModelState.AddModelError(nameof(model.Password), "パスワードは12～128文字で入力してください。");
        }

        if (model.IsSetup && !MatchesSetupCode(model.SetupCode))
        {
            ModelState.AddModelError("", "初回セットアップ用コードを確認してください。未設定の場合はホストの設定が必要です。");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        var user = new AppUser { UserName = model.LoginId, IsAdmin = model.IsSetup, LockoutEnabled = true };
        var result = await users.CreateAsync(user, model.Password);
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", "このユーザーIDでは登録できません。別のIDを指定してください。");
            return View(model);
        }

        policy.SetupCompleted = true;
        var freshCodes = await codes.ReplaceAsync(user.Id);
        await flows.BeginAsync(HttpContext, user, "confirm-login");
        audit.Add(HttpContext, model.IsSetup ? "setup" : "registration", user.Id);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return View("Codes", new RecoveryCodesModel { Codes = freshCodes });
    }

    /// <summary>コードの保存確認後だけ登録・復旧の手続きを完了する</summary>
    [HttpPost("/auth/codes/confirm")]
    public async Task<IActionResult> ConfirmCodes(bool saved)
    {
        var flow = await flows.GetAsync(HttpContext);
        if (flow is null || !flow.Purpose.StartsWith("confirm-", StringComparison.Ordinal) || !saved)
        {
            return BadRequest("保存の確認が必要です。画面を失った場合はログインから再開してください。");
        }

        var user = await users.FindByIdAsync(flow.UserId);
        if (user is null)
        {
            return Unauthorized();
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        user.RecoveryCodesConfirmed = true;
        await db.SaveChangesAsync();
        await flows.EndAsync(HttpContext, flow);
        if (flow.Purpose == "confirm-login")
        {
            await sessions.SignInAsync(HttpContext, user, flow.RememberMe);
        }

        await transaction.CommitAsync();
        return Redirect(flow.Purpose == "confirm-recovery" ? "/auth/login" : "/account");
    }

    /// <summary>自然期限切れとは区別し、このブラウザを明示的にログアウトする</summary>
    [HttpPost("/auth/logout")]
    public async Task<IActionResult> Logout()
    {
        await flows.CancelAsync(HttpContext);
        ceremonies.Cancel(HttpContext, sessions);
        await sessions.SignOutAsync(HttpContext);
        return RedirectToAction(nameof(Login));
    }

    /// <summary>ホスト設定の初回コードを固定長のハッシュで比較する</summary>
    private bool MatchesSetupCode(string? supplied)
    {
        var configured = configuration["Authentication:SetupCode"];
        return !string.IsNullOrWhiteSpace(configured) && supplied is not null && supplied.Length <= 256
            && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(configured)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
    }
}
