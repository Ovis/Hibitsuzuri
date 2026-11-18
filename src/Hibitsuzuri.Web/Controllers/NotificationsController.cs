using System.Security.Claims;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Notifications;
using Hibitsuzuri.Web.Storage;
using Lib.Net.Http.WebPush;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>現在のブラウザだけの通知許可・プライバシー設定</summary>
[ServiceFilter(typeof(AuthOperationFilter))]
public sealed class NotificationsController(AppDbContext db, PushSubscriptions subscriptions, PushKeys keys, TimeProvider clock) : Controller
{
    /// <summary>通知先の本人。管理者にも他人の購読は公開しない</summary>
    private string Owner => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException();

    /// <summary>ログイン時に確定したブラウザ識別子</summary>
    private Task<string> BrowserAsync()
    {
        var session = User.FindFirstValue(SessionService.SessionClaim);
        return db.AuthSessions.Where(x => x.Id == session && x.UserId == Owner).Select(x => x.BrowserId).SingleAsync();
    }

    /// <summary>通知とインストールの設定画面</summary>
    [HttpGet("/settings/notifications")]
    public IActionResult Index() => View();

    /// <summary>自然なログイン期限切れと通知停止を混同しないため、匿名時は認証エラーを返す</summary>
    [HttpGet("/api/push/device")]
    public async Task<IActionResult> Device()
    {
        var browser = await BrowserAsync();
        var device = await db.PushDevices.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == Owner && x.BrowserId == browser);
        return Json(new
        {
            owner = Owner,
            device = device is null ? null : new
            {
                device.Id,
                device.Generation,
                device.Enabled,
                device.ShowName,
                device.Error
            },
            publicKey = keys.Public,
            timeZone = clock.LocalTimeZone.Id
        });
    }

    /// <summary>明示操作で購読を登録する。ブラウザが許可しただけでは登録しない</summary>
    [HttpPost("/api/push/enable")]
    public async Task<IActionResult> Enable([FromBody] PushRegistration request)
    {
        if (!ModelState.IsValid || !PushSubscriptions.Valid(request.Subscription))
        {
            return BadRequest(new
            {
                message = "このブラウザの購読情報を確認できません。対応するChromeまたはEdgeでやり直してください。"
            });
        }

        var device = await subscriptions.EnableAsync(Owner, await BrowserAsync(), request.Subscription, request.ShowName);
        return Json(new
        {
            id = device.Id,
            generation = device.Generation
        });
    }

    /// <summary>このブラウザの購読を即時停止する</summary>
    [HttpPost("/api/push/disable")]
    public async Task<IActionResult> Disable()
    {
        var browser = await BrowserAsync();
        await PushSubscriptions.DisableAsync(db.PushDevices.Where(x => x.UserId == Owner && x.BrowserId == browser));
        return Json(new
        {
            saved = true
        });
    }

    /// <summary>タスク名の表示はブラウザごとの明示選択とする</summary>
    [HttpPost("/api/push/privacy")]
    public async Task<IActionResult> Privacy([FromBody] PushPrivacy request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new
            {
                message = "タスク名の表示設定を確認してください。"
            });
        }

        var browser = await BrowserAsync();
        await db.PushDevices.Where(x => x.UserId == Owner && x.BrowserId == browser)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.ShowName, request.ShowName));
        return Json(new
        {
            saved = true
        });
    }

    /// <summary>固定文のOS通知を現在のブラウザへ一度予約する</summary>
    [HttpPost("/api/push/test")]
    public async Task<IActionResult> Test()
    {
        var browser = await BrowserAsync();
        var device = await db.PushDevices.SingleOrDefaultAsync(x => x.UserId == Owner && x.BrowserId == browser && x.Enabled);
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (device is null)
        {
            return BadRequest(new
            {
                message = "先にこのブラウザの通知を有効にしてください。"
            });
        }

        if (device.LastTestAt > now - 60000)
        {
            return StatusCode(429, new
            {
                message = "テスト通知は1分待ってからお試しください。"
            });
        }

        device.LastTestAt = now;
        db.PushDeliveries.Add(subscriptions.Create(device, "", Guid.NewGuid().ToString("N"), now, true));
        await db.SaveChangesAsync();
        return Json(new
        {
            message = "テスト通知を予約しました。通常は30秒以内に配信を開始します。"
        });
    }
}

/// <summary>ブラウザが発行する購読と、端末単位の表示選択</summary>
public sealed class PushRegistration
{
    /// <summary>Push APIから取得した送信先と暗号鍵</summary>
    public PushSubscription Subscription { get; set; } = new();

    /// <summary>通知にタスク名を含める明示的な選択</summary>
    public bool ShowName
    {
        get; set;
    }
}

/// <summary>購読を作り直さずに変更する通知の表示設定</summary>
public sealed class PushPrivacy
{
    /// <summary>タスク名の表示許可</summary>
    public bool ShowName
    {
        get; set;
    }
}

/// <summary>ログインCookieを使わず、受信した短命トークンで配信の失効を確認する</summary>
[AllowAnonymous]
public sealed class PushReceiptController(AppDbContext db, PushDispatcher dispatcher, TimeProvider clock) : Controller
{
    /// <summary>通知本文は返さず、現在も表示可能かだけを返す</summary>
    [HttpGet("/api/push/receipt/{id}")]
    public async Task<IActionResult> Receipt(string id, string? token, CancellationToken cancellationToken)
    {
        if (token is null || token.Length != 64)
        {
            return NotFound();
        }

        var hash = AuthRules.Hash(token);
        var delivery = await db.PushDeliveries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.TokenHash == hash, cancellationToken);
        if (delivery is null || delivery.ScheduledAt + 10800000 <= clock.GetUtcNow().ToUnixTimeMilliseconds())
        {
            return NotFound();
        }

        var device = await db.PushDevices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == delivery.DeviceId, cancellationToken);
        if (device is null)
        {
            return NotFound();
        }

        return Json(new
        {
            allowed = await dispatcher.EligibleAsync(delivery, device, cancellationToken),
            showName = device.ShowName
        });
    }
}
