using System.Net;
using Hibitsuzuri.Web.Hosting;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;

namespace Hibitsuzuri.Web.Notifications;

/// <summary>暗号化したWeb Pushの送信境界</summary>
public interface IPushTransport
{
    /// <summary>サービスの受付結果を返す。端末での表示完了とは区別する</summary>
    Task<int> SendAsync(PushSubscription subscription, string payload, string topic, int ttl, CancellationToken cancellationToken);
}

/// <summary>送信先URLをHTTPログへ残さず、再試行は永続台帳側に集約する</summary>
public sealed class PushTransport(PushKeys keys, SiteSettings site, HttpClient http) : IPushTransport, IDisposable
{
    /// <summary>購読ごとの鍵で暗号化し、予定時刻から残っているTTLを渡す</summary>
    public async Task<int> SendAsync(PushSubscription subscription, string payload, string topic, int ttl, CancellationToken cancellationToken)
    {
        var client = new PushServiceClient(http) { AutoRetryAfter = false };
        var authentication = new VapidAuthentication(keys.Public, keys.Private) { Subject = site.PublicOrigin.AbsoluteUri };
        try
        {
            await client.RequestPushMessageDeliveryAsync(subscription, new PushMessage(payload) { Topic = topic, TimeToLive = ttl }, authentication, cancellationToken);
            return (int)HttpStatusCode.Created;
        }
        catch (PushServiceClientException exception)
        {
            return (int)exception.StatusCode;
        }
        catch (HttpRequestException)
        {
            return 503;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return 503;
        }
    }

    /// <summary>共有HTTP接続をアプリ終了時に破棄する</summary>
    public void Dispose() => http.Dispose();
}
