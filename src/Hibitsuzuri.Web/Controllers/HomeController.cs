using Microsoft.AspNetCore.Mvc;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>アプリの入口を表示する</summary>
public sealed class HomeController : Controller
{
    /// <summary>共通レイアウトを使う開始画面を返す</summary>
    [HttpGet("/")]
    public IActionResult Index()
    {
        return View();
    }
}
