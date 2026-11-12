using Microsoft.AspNetCore.Mvc;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>ブラウザ内の表示設定を変更する画面</summary>
public sealed class SettingsController : Controller
{
    /// <summary>テーマとライト配色の選択肢を表示する</summary>
    [HttpGet("/settings/appearance")]
    public IActionResult Appearance()
    {
        return View();
    }
}
