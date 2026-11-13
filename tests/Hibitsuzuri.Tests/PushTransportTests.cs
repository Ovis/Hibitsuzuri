using System.Net;
using System.Text;
using Hibitsuzuri.Web.Hosting;
using Hibitsuzuri.Web.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>採用ライブラリを通した暗号化とPushサービスのHTTP契約</summary>
public sealed class PushTransportTests
{
    /// <summary>VAPID署名・暗号化・TTL・Topicを付け、購読失効を通常の結果として返す</summary>
    [Test]
    public async Task SendsEncryptedRequestWithVapidAndScheduledTtl()
    {
        await using var app = NotificationTests.Application(new RecordingPush());
        using var browser = app.CreateBrowser();
        using var handler = new CapturingHandler();
        using var transport = new PushTransport(app.Services.GetRequiredService<PushKeys>(), app.Services.GetRequiredService<SiteSettings>(), new HttpClient(handler));

        var result = await transport.SendAsync(NotificationTests.Subscription(), "秘密の通知本文", "stable-topic", 7200, CancellationToken.None);

        Assert.That(result, Is.EqualTo(410));
        Assert.That(handler.Method, Is.EqualTo("POST"));
        Assert.That(handler.Authorization, Does.Contain("vapid"));
        Assert.That(handler.Ttl, Is.EqualTo("7200"));
        Assert.That(handler.Topic, Is.EqualTo("stable-topic"));
        Assert.That(handler.Encoding, Does.Contain("aes128gcm"));
        Assert.That(handler.Body.Length, Is.GreaterThan(32));
        Assert.That(Encoding.UTF8.GetString(handler.Body), Does.Not.Contain("秘密の通知本文"));
    }

    /// <summary>実ネットワークへ出さずに最終HTTPリクエストを観測する</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        /// <summary>送信メソッド</summary>
        public string Method { get; private set; } = "";

        /// <summary>VAPIDの署名ヘッダー</summary>
        public string Authorization { get; private set; } = "";

        /// <summary>サービスへ渡す残存秒数</summary>
        public string Ttl { get; private set; } = "";

        /// <summary>再送時の置換キー</summary>
        public string Topic { get; private set; } = "";

        /// <summary>暗号化方式のヘッダー</summary>
        public string Encoding { get; private set; } = "";

        /// <summary>暗号化後のペイロード</summary>
        public byte[] Body { get; private set; } = [];

        /// <summary>鍵の生成から送信直前まで実装を通し、サービス失効だけを応答する</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            Authorization = request.Headers.Authorization?.ToString() ?? "";
            Ttl = request.Headers.GetValues("TTL").Single();
            Topic = request.Headers.GetValues("Topic").Single();
            var content = request.Content ?? throw new InvalidOperationException();
            Encoding = string.Join(",", content.Headers.ContentEncoding);
            Body = await content.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Gone) { Content = new StringContent("") };
        }
    }
}
