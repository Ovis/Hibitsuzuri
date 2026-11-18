using System.Security.Cryptography;
using System.Text.Json;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Storage;
using Lib.Net.Http.WebPush;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Notifications;

/// <summary>購読情報を保護し、ブラウザと所有者の対応を一意にする</summary>
public sealed class PushSubscriptions(AppDbContext db, IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector _protector = protection.CreateProtector("Hibitsuzuri.Push.v1");

    /// <summary>対応ブラウザの公開Pushサービスだけを許可し、任意URLへのサーバー送信を防ぐ</summary>
    public static bool Valid(PushSubscription? subscription)
    {
        if (subscription is null || subscription.Endpoint is null || subscription.Endpoint.Length > 4096
            || !Uri.TryCreate(subscription.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || !(uri.IdnHost == "fcm.googleapis.com" || uri.IdnHost.EndsWith(".notify.windows.com", StringComparison.Ordinal))
            || subscription.Keys is null || !subscription.Keys.TryGetValue("p256dh", out var publicKey)
            || !subscription.Keys.TryGetValue("auth", out var auth) || publicKey is null || auth is null || publicKey.Length > 100 || auth.Length > 30)
        {
            return false;
        }

        try
        {
            var key = WebEncoders.Base64UrlDecode(publicKey);
            if (key.Length != 65 || key[0] != 4 || WebEncoders.Base64UrlDecode(auth).Length != 16)
            {
                return false;
            }

            using var curve = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = key[1..33], Y = key[33..65] }
            });
            return true;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>明示的に有効化した時刻以降だけを対象にし、同じブラウザの旧所有者を停止する</summary>
    public async Task<PushDevice> EnableAsync(string user, string browser, PushSubscription subscription, bool showName)
    {
        var hash = AuthRules.Hash(subscription.Endpoint);
        await DisableAsync(db.PushDevices.Where(x => (x.BrowserId == browser || x.EndpointHash == hash)
            && !(x.UserId == user && x.BrowserId == browser)));
        var device = await db.PushDevices.SingleOrDefaultAsync(x => x.UserId == user && x.BrowserId == browser);
        if (device is null)
        {
            device = new PushDevice { UserId = user, BrowserId = browser };
            db.PushDevices.Add(device);
        }

        if (!device.Enabled || device.EndpointHash != hash)
        {
            device.Generation = Guid.NewGuid().ToString("N");
            device.EnabledSince = clock.GetUtcNow().ToUnixTimeMilliseconds();
        }

        device.EndpointHash = hash;
        device.ProtectedSubscription = _protector.Protect(JsonSerializer.Serialize(subscription));
        device.Enabled = true;
        device.ShowName = showName;
        device.Error = null;
        await db.SaveChangesAsync();
        return device;
    }

    /// <summary>自然失効とは独立した明示停止。世代変更で待機中の配信も無効にする</summary>
    public static Task<int> DisableAsync(IQueryable<PushDevice> devices) => devices.Where(x => x.Enabled)
        .ExecuteUpdateAsync(set => set.SetProperty(x => x.Enabled, false).SetProperty(x => x.Generation, Guid.NewGuid().ToString("N")));

    /// <summary>送信の直前だけ購読秘密を復号する</summary>
    public PushSubscription Read(PushDevice device) => JsonSerializer.Deserialize<PushSubscription>(_protector.Unprotect(device.ProtectedSubscription))
        ?? throw new InvalidDataException("Push購読を読み取れません。");

    /// <summary>再送時も同じ受信確認トークンを使う</summary>
    public string Token(PushDelivery delivery) => _protector.Unprotect(delivery.ProtectedToken);

    /// <summary>Cookieの自然失効後も受信確認できる短命な能力トークンを発行する</summary>
    public PushDelivery Create(PushDevice device, string task, string reminder, long at, bool test = false)
    {
        var token = AuthRules.Token();
        return new PushDelivery
        {
            DeviceId = device.Id,
            Generation = device.Generation,
            TaskId = task,
            ReminderId = reminder,
            ScheduledAt = at,
            NextAttemptAt = at,
            Test = test,
            ProtectedToken = _protector.Protect(token),
            TokenHash = AuthRules.Hash(token)
        };
    }
}
