using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Controllers;
using Hibitsuzuri.Web.Notifications;
using Hibitsuzuri.Web.Storage;
using Hibitsuzuri.Web.Tasks;
using Lib.Net.Http.WebPush;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Hibitsuzuri.Tests;

/// <summary>実DB・認証・CSRFを通じて通知の時刻境界、失効と送信台帳を検証する</summary>
public sealed class NotificationTests
{
    /// <summary>新しい過去設定を拒否し、メモだけの編集と明示的な通知削除を許可する</summary>
    [Test]
    public async Task ReminderValidationRetainsUnchangedPastAndRejectsMovedPast()
    {
        var sender = new RecordingPush();
        await using var app = Application(sender);
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        var at = app.Clock.GetLocalNow().AddMinutes(2);
        var saved = await TaskTests.SaveAsync(client, new TaskChange
        {
            NotebookId = book,
            Title = "通知設定",
            DueDate = DateOnly.FromDateTime(at.DateTime),
            Reminders = [new Reminder { Kind = "absolute", At = at.ToString("yyyy-MM-ddTHH:mm") }],
            OperationId = Guid.NewGuid().ToString()
        });

        app.Clock.Advance(TimeSpan.FromMinutes(3));
        var edited = await TaskTests.SaveAsync(client, new TaskChange
        {
            Id = saved.GetProperty("id").GetString(),
            Version = saved.GetProperty("version").GetString() ?? "",
            Title = "メモのみ変更",
            Memo = "保持",
            DueDate = DateOnly.FromDateTime(at.DateTime),
            OperationId = Guid.NewGuid().ToString()
        });
        using var rejected = await TaskTests.SendAsync(client, new TaskChange
        {
            NotebookId = book,
            Title = "新しい過去通知",
            Reminders = [new Reminder { Kind = "absolute", At = at.ToString("yyyy-MM-ddTHH:mm") }],
            OperationId = Guid.NewGuid().ToString()
        });
        Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        await TaskTests.SaveAsync(client, new TaskChange
        {
            Id = edited.GetProperty("id").GetString(),
            Version = edited.GetProperty("version").GetString() ?? "",
            Title = "通知を外す",
            Reminders = [],
            OperationId = Guid.NewGuid().ToString()
        });
        var detail = await TaskTests.ReadAsync(client, "/api/tasks/item?id=" + edited.GetProperty("id").GetString());
        Assert.That(detail.GetProperty("reminders").GetArrayLength(), Is.Zero);

        var due = DateOnly.FromDateTime(app.Clock.GetLocalNow().AddDays(2).DateTime);
        var relative = await TaskTests.SaveAsync(client, new TaskChange
        {
            NotebookId = book,
            Title = "期限相対",
            DueDate = due,
            Reminders = [new Reminder()],
            OperationId = Guid.NewGuid().ToString()
        });
        using var moved = await TaskTests.SendAsync(client, new TaskChange
        {
            Id = relative.GetProperty("id").GetString(),
            Version = relative.GetProperty("version").GetString() ?? "",
            Title = "過去に移動",
            DueDate = due.AddDays(-3),
            OperationId = Guid.NewGuid().ToString()
        });
        Assert.That(moved.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>送信遅延の境界と、予定時刻を基準にした3時間TTLを確認する</summary>
    [TestCase(59, true)]
    [TestCase(60, true)]
    [TestCase(61, false)]
    public async Task LateDeliveryUsesScheduledTimeAndNeverSendsTwice(int minutes, bool expected)
    {
        var sender = new RecordingPush();
        await using var app = Application(sender);
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        await EnableAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        var at = await AddTaskAsync(app, book, TimeSpan.FromMinutes(2));
        app.Clock.Advance(TimeSpan.FromMinutes(2 + minutes));

        await RunAsync(app);
        await RunAsync(app);
        Assert.That(sender.Messages.Count, Is.EqualTo(expected ? 1 : 0));
        if (expected)
        {
            var sent = sender.Messages.Single();
            Assert.That(sent.Ttl, Is.EqualTo((int)((at + 10800000 - app.Clock.GetUtcNow().ToUnixTimeMilliseconds()) / 1000)));
            Assert.That(sent.Payload, Does.Not.Contain("秘密の件名").And.Not.Contain("秘密のメモ"));
        }
    }

    /// <summary>名前は端末単位の許可時だけ含め、リトライでも通知識別子は変えない</summary>
    [Test]
    public async Task RetryUsesSameTopicAndCompletionCancelsQueuedMessages()
    {
        var sender = new RecordingPush { Status = 503 };
        await using var app = Application(sender);
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        await EnableAsync(client, true);
        var book = await DiaryClient.CreateBookAsync(client);
        await AddTaskAsync(app, book, TimeSpan.FromMinutes(2));
        app.Clock.Advance(TimeSpan.FromMinutes(2));
        await RunAsync(app);
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        sender.Status = 201;
        await RunAsync(app);
        Assert.That(sender.Messages.Count, Is.EqualTo(2));
        Assert.That(sender.Messages.Select(x => x.Topic).Distinct().Count(), Is.EqualTo(1));
        Assert.That(JsonDocument.Parse(sender.Messages[0].Payload).RootElement.GetProperty("body").GetString(), Is.EqualTo("秘密の件名"));
        Assert.That(sender.Messages[0].Payload, Does.Not.Contain("秘密のメモ"));

        await AddTaskAsync(app, book, TimeSpan.FromMinutes(2));
        sender.Status = 503;
        app.Clock.Advance(TimeSpan.FromMinutes(2));
        await RunAsync(app);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.TaskRecords.ExecuteUpdateAsync(set => set.SetProperty(x => x.CompletedAt, app.Clock.GetUtcNow().ToUnixTimeMilliseconds()));
        }

        app.Clock.Advance(TimeSpan.FromMinutes(1));
        await RunAsync(app);
        Assert.That(sender.Messages.Count, Is.EqualTo(3));
    }

    /// <summary>ログイン期限切れは通知を止めず、明示ログアウト後は再ログインだけで再開しない</summary>
    [Test]
    public async Task ExpiryContinuesButLogoutRevokesAndRequiresExplicitEnable()
    {
        var sender = new RecordingPush();
        await using var app = Application(sender);
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        await EnableAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        await AddTaskAsync(app, book, TimeSpan.FromHours(9));
        app.Clock.Advance(TimeSpan.FromHours(9));
        await RunAsync(app);
        Assert.That(sender.Messages.Count, Is.EqualTo(1));

        using var logout = await AuthClient.PostAsync(client, "/auth/logout");
        Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        using var login = await AuthClient.LoginAsync(client);
        var state = await TaskTests.ReadAsync(client, "/api/push/device");
        Assert.That(state.GetProperty("device").GetProperty("enabled").GetBoolean(), Is.False);
        var receipt = JsonDocument.Parse(sender.Messages[0].Payload).RootElement.GetProperty("receipt").GetString();
        using var anonymous = app.CreateBrowser();
        var denied = await TaskTests.ReadAsync(anonymous, receipt ?? "");
        Assert.That(denied.GetProperty("allowed").GetBoolean(), Is.False);
    }

    /// <summary>パスワード変更は現在のブラウザだけを残し、全失効は全通知を止める</summary>
    [Test]
    public async Task PasswordChangeAndForcedRevocationUseBrowserBoundary()
    {
        await using var app = Application(new RecordingPush());
        using var current = app.CreateBrowser();
        await AuthClient.RegisterAsync(current);
        await EnableAsync(current);
        using var other = app.CreateBrowser();
        using var login = await AuthClient.LoginAsync(other);
        await EnableAsync(other);

        using var changed = await AuthClient.PostAsync(current, "/account/password", new()
        {
            ["Password"] = "changed-password-for-notifications",
            ["PasswordConfirmation"] = "changed-password-for-notifications"
        });
        Assert.That(changed.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(await db.PushDevices.CountAsync(x => x.Enabled), Is.EqualTo(1));
        var state = await TaskTests.ReadAsync(current, "/api/push/device");
        Assert.That(state.GetProperty("device").GetProperty("enabled").GetBoolean(), Is.True);

        await scope.ServiceProvider.GetRequiredService<SessionService>().RevokeAsync(await db.Users.Select(x => x.Id).SingleAsync());
        Assert.That(await db.PushDevices.AnyAsync(x => x.Enabled), Is.False);
    }

    /// <summary>購読失効を画面に返し、再開時に以前の未配信分をまとめて送らない</summary>
    [Test]
    public async Task GoneSubscriptionAndManualResumeSkipPast()
    {
        var sender = new RecordingPush { Status = 410 };
        await using var app = Application(sender);
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        await EnableAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        await AddTaskAsync(app, book, TimeSpan.FromMinutes(2));
        app.Clock.Advance(TimeSpan.FromMinutes(2));
        await RunAsync(app);
        var status = await TaskTests.ReadAsync(client, "/api/push/device");
        Assert.That(status.GetProperty("device").GetProperty("enabled").GetBoolean(), Is.False);
        Assert.That(status.GetProperty("device").GetProperty("error").GetString(), Does.Contain("失効"));

        sender.Status = 201;
        await EnableAsync(client);
        await RunAsync(app);
        Assert.That(sender.Messages.Count, Is.EqualTo(1));
    }

    /// <summary>テスト通知も同じ配信経路を通り、他ブラウザへは送らない</summary>
    [Test]
    public async Task TestNotificationTargetsOnlyCurrentBrowserAndProtectsSecrets()
    {
        var sender = new RecordingPush();
        await using var app = Application(sender);
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        await EnableAsync(client);
        using var other = app.CreateBrowser();
        using var login = await AuthClient.LoginAsync(other);
        await EnableAsync(other);
        using var response = await PostAsync(client, "/api/push/test", new
        {
        });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var limited = await PostAsync(client, "/api/push/test", new
        {
        });
        Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));

        await RunAsync(app);
        Assert.That(sender.Messages.Count, Is.EqualTo(1));
        Assert.That(JsonDocument.Parse(sender.Messages[0].Payload).RootElement.GetProperty("title").GetString(), Does.Contain("テスト通知"));
        var state = await client.GetStringAsync("/api/push/device");
        Assert.That(state, Does.Not.Contain("protectedSubscription").And.Not.Contain("browserId").And.Not.Contain("Endpoint"));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(await db.PushDevices.Select(x => x.ProtectedSubscription).FirstAsync(), Does.Not.Contain("https://"));
    }

    /// <summary>任意の内部URLや壊れた鍵を送信先として受理しない</summary>
    [TestCase("https://127.0.0.1/push")]
    [TestCase("https://fcm.googleapis.com.attacker.test/push")]
    [TestCase("https://fcm.googleapis.com:8443/push")]
    [TestCase("http://fcm.googleapis.com/push")]
    public void RejectsUntrustedEndpoints(string endpoint)
    {
        var subscription = Subscription();
        subscription.Endpoint = endpoint;
        Assert.That(PushSubscriptions.Valid(subscription), Is.False);
    }

    /// <summary>自動周期だけを除き、明示実行で通知境界を再現する</summary>
    public static TestApplication Application(RecordingPush sender) => new()
    {
        ServicesOverride = services =>
        {
            foreach (var descriptor in services.Where(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(PushMaintenance)).ToArray())
            {
                services.Remove(descriptor);
            }

            services.RemoveAll<IPushTransport>();
            services.AddSingleton<IPushTransport>(sender);
        }
    };

    /// <summary>有効なP-256公開鍵と試験専用のPush URLを作る</summary>
    public static PushSubscription Subscription()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var point = key.ExportParameters(false).Q;
        return new PushSubscription
        {
            Endpoint = "https://fcm.googleapis.com/push/" + Guid.NewGuid().ToString("N"),
            Keys = new Dictionary<string, string>
            {
                ["p256dh"] = WebEncoders.Base64UrlEncode([4, .. point.X ?? [], .. point.Y ?? []]),
                ["auth"] = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16))
            }
        };
    }

    /// <summary>実際のCSRFヘッダーでJSON設定APIを呼ぶ</summary>
    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("RequestVerificationToken", await client.GetStringAsync("/fixture/token"));
        return await client.SendAsync(request);
    }

    /// <summary>現在のブラウザで通知を明示的に有効にする</summary>
    public static async Task EnableAsync(HttpClient client, bool showName = false)
    {
        using var response = await PostAsync(client, "/api/push/enable", new PushRegistration { Subscription = Subscription(), ShowName = showName });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
    }

    /// <summary>秒単位の境界を正確に検証するため、通知をDBに準備する</summary>
    public static async Task<long> AddTaskAsync(TestApplication app, string book, TimeSpan delay)
    {
        var local = app.Clock.GetLocalNow() + delay;
        // 保存形式の分境界に時計を揃え、遅延試験で余分な秒を含めない。
        app.Clock.Advance(TimeSpan.FromTicks(-(app.Clock.GetUtcNow().Ticks % TimeSpan.TicksPerMinute)));
        local = app.Clock.GetLocalNow() + delay;
        var reminder = new Reminder { Id = Guid.NewGuid().ToString("N"), Kind = "absolute", At = local.ToString("yyyy-MM-ddTHH:mm") };
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TaskRecords.Add(new TaskRecord { NotebookId = book, Title = "秘密の件名", Memo = "秘密のメモ", ReminderJson = JsonSerializer.Serialize(new[] { reminder }) });
        await db.SaveChangesAsync();
        return Reminders.Resolve(reminder, null, app.Clock.LocalTimeZone) ?? throw new InvalidOperationException();
    }

    /// <summary>リクエストとは別のDBスコープで送信ワーカーを一巡する</summary>
    public static async Task RunAsync(TestApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PushDispatcher>().RunAsync(CancellationToken.None);
    }
}

/// <summary>外部サービスを呼ばずにペイロードとTTLを観測する送信境界</summary>
public sealed class RecordingPush : IPushTransport
{
    /// <summary>次の送信で返すHTTP相当の受付結果</summary>
    public int Status { get; set; } = 201;

    /// <summary>試行順の送信記録</summary>
    public List<(string Payload, string Topic, int Ttl)> Messages { get; } = [];

    /// <summary>端末への実配信とは分離してサーバーの判断を記録する</summary>
    public Task<int> SendAsync(PushSubscription subscription, string payload, string topic, int ttl, CancellationToken cancellationToken)
    {
        Messages.Add((payload, topic, ttl));
        return Task.FromResult(Status);
    }
}
