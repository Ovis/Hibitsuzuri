using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>通常ログインから独立したWebアカウント復旧</summary>
[AllowAnonymous]
[ServiceFilter<AuthOperationFilter>]
[EnableRateLimiting("authentication")]
[RequestSizeLimit(16384)]
[RequestFormLimits(ValueLengthLimit = 1024)]
public sealed class RecoveryController(AccountRecoveryService recovery, AccountFlows flows) : Controller
{
    /// <summary>復旧専用コードによる本人確認を案内する</summary>
    [HttpGet("/auth/recovery")]
    public IActionResult Index() => View(new RecoveryModel());

    /// <summary>不存在・不正・使用済み・制限中を同じエラーとして扱う</summary>
    [HttpPost("/auth/recovery")]
    public async Task<IActionResult> Index(RecoveryModel model)
    {
        if (!ModelState.IsValid || !await recovery.BeginAsync(HttpContext, model.LoginId, model.Code))
        {
            ModelState.Clear();
            ModelState.AddModelError("", "復旧を開始できませんでした。入力内容を確認し、制限中の場合は時間をおいて再試行してください。");
            return View(new RecoveryModel { LoginId = model.LoginId });
        }

        return RedirectToAction(nameof(Password));
    }

    /// <summary>通常利用を許可せず、新パスワードの設定だけを表示する</summary>
    [HttpGet("/auth/recovery/password")]
    public async Task<IActionResult> Password()
    {
        var flow = await flows.GetAsync(HttpContext);
        return flow?.Purpose == "recover" ? View(new NewPasswordModel()) : RedirectToAction(nameof(Index));
    }

    /// <summary>旧認証を失効させ、新しいコードの保存確認へ進む</summary>
    [HttpPost("/auth/recovery/password")]
    public async Task<IActionResult> Password(NewPasswordModel model)
    {
        if (!AuthRules.ValidText(model.Password, 12, 128))
        {
            ModelState.AddModelError(nameof(model.Password), "パスワードは12～128文字で入力してください。");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var codes = await recovery.CompleteAsync(HttpContext, model.Password);
        return codes is null ? BadRequest("復旧手続きの期限が切れました。別の未使用コードで再開してください。")
            : View("~/Views/Auth/Codes.cshtml", new RecoveryCodesModel { Codes = codes });
    }
}
