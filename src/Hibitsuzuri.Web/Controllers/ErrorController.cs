using Hibitsuzuri.Web.Models;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>HTTPエラーを失敗のステータスのまま案内する</summary>
[Microsoft.AspNetCore.Authorization.AllowAnonymous]
public sealed class ErrorController : Controller
{
    /// <summary>APIには問題詳細、画面には再操作できる案内を返す</summary>
    [Route("/error/{statusCode:int?}")]
    [IgnoreAntiforgeryToken]
    public IActionResult Index()
    {
        var exception = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        var reExecution = HttpContext.Features.Get<IStatusCodeReExecuteFeature>();

        // 不正・過大なフォームを再バインドせず、再実行されたルートから元のエラーを読む。
        var code = exception is null && int.TryParse(RouteData.Values["statusCode"]?.ToString(), out var statusCode)
            && statusCode is >= 400 and <= 599 ? statusCode : StatusCodes.Status500InternalServerError;
        var message = code switch
        {
            StatusCodes.Status403Forbidden => "このページを表示する権限がありません。",
            StatusCodes.Status404NotFound => "ページが見つかりません。",
            _ => "処理を完了できませんでした。"
        };
        var originalPath = exception?.Path ?? reExecution?.OriginalPath ?? Request.Path.Value;

        if (originalPath?.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) == true
            || string.Equals(originalPath, "/api", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(statusCode: code, title: message);
        }

        Response.StatusCode = code;
        return View(new ErrorViewModel
        {
            StatusCode = code,
            Message = message,
            RequestId = HttpContext.TraceIdentifier
        });
    }
}
