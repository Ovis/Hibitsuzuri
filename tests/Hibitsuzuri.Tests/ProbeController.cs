using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Hibitsuzuri.Tests;

/// <summary>本番のミドルウェアとMVCフィルターを通してHTTP境界を検証する。配布アプリには登録しない</summary>
[Microsoft.AspNetCore.Authorization.AllowAnonymous]
public sealed class ProbeController : Controller
{
    /// <summary>サーバーが採用した接続元とHTTPS判定を返す</summary>
    [HttpGet("/fixture/request")]
    public IActionResult RequestInfo()
    {
        return Content($"{Request.Scheme}|{Request.Host.Host}|{HttpContext.Connection.RemoteIpAddress}");
    }

    /// <summary>CSRF保護の正常系に使用するトークンとCookieを発行する</summary>
    [HttpGet("/fixture/token")]
    public IActionResult Token([FromServices] IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Content(tokens.RequestToken ?? throw new InvalidOperationException("トークンを生成できませんでした。"));
    }

    /// <summary>CSRFフィルターを通過した更新要求だけを成功させる</summary>
    [HttpPost("/fixture/write")]
    public IActionResult Write()
    {
        return Ok();
    }

    /// <summary>例外の詳細が画面やAPIへ漏れないことを検証する</summary>
    [HttpGet("/fixture/failure")]
    [HttpGet("/api/fixture/failure")]
    public IActionResult Failure()
    {
        throw new InvalidOperationException("private-failure-detail");
    }
}
