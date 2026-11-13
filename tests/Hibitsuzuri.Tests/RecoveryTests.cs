using System.Net;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>認証変更・復旧の失効範囲と専用フローの境界</summary>
public sealed class RecoveryTests
{
    /// <summary>通常のパスワード変更は現在端末を残し、他端末だけ失効する</summary>
    [Test]
    public async Task PasswordChangePreservesCurrentExpiryAndRevokesOthers()
    {
        await using var app = new TestApplication();
        using var current = app.CreateBrowser();
        await AuthClient.RegisterAsync(current);
        using var other = app.CreateBrowser();
        using var login = await AuthClient.LoginAsync(other);

        using var changed = await AuthClient.PostAsync(current, "/account/password", new()
        {
            ["Password"] = "new-password-for-test",
            ["PasswordConfirmation"] = "new-password-for-test"
        });

        Assert.That(changed.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        using var retained = await current.GetAsync("/account");
        using var revoked = await other.GetAsync("/account");

        Assert.That(retained.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(revoked.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        app.Clock.Advance(TimeSpan.FromHours(8));
        using var expired = await current.GetAsync("/account");

        Assert.That(expired.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
    }

    /// <summary>再認証がちょうど10分で失効し、成功後もログイン自体は延長しない</summary>
    [Test]
    public async Task SensitiveOperationRequiresRecentAuthentication()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        app.Clock.Advance(TimeSpan.FromMinutes(10));

        using var rejected = await AuthClient.PostAsync(client, "/account/codes", new()
        {
            ["confirmed"] = "true"
        });

        Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        using var verified = await AuthClient.PostAsync(client, "/account/reauthenticate", new()
        {
            ["password"] = AuthClient.Password,
            ["returnUrl"] = "https://attacker.example/"
        });

        Assert.That(verified.Headers.Location?.OriginalString, Is.EqualTo("/account"));

        using var accepted = await AuthClient.PostAsync(client, "/account/codes", new()
        {
            ["confirmed"] = "true"
        });

        Assert.That(AuthClient.CodePattern().Matches(await accepted.Content.ReadAsStringAsync()).Count, Is.EqualTo(10));
    }

    /// <summary>復旧は旧コード・全通常セッションを失効し、保存確認後もログインへ戻す</summary>
    [Test]
    public async Task RecoveryAtomicallyReplacesCredentialsAndNeverLogsIn()
    {
        await using var app = new TestApplication();
        using var original = app.CreateBrowser();
        var oldCodes = await AuthClient.RegisterAsync(original);
        using var recovery = app.CreateBrowser();

        using var started = await StartAsync(recovery, oldCodes[0]);
        Assert.That(started.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        using var privatePage = await recovery.GetAsync("/account");
        Assert.That(privatePage.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        using var completed = await AuthClient.PostAsync(recovery, "/auth/recovery/password", new()
        {
            ["Password"] = "recovered-password",
            ["PasswordConfirmation"] = "recovered-password"
        });
        var freshCodes = AuthClient.CodePattern().Matches(await completed.Content.ReadAsStringAsync()).Select(x => x.Value).ToArray();

        Assert.That(freshCodes, Has.Length.EqualTo(10));
        Assert.That(freshCodes.Intersect(oldCodes), Is.Empty);

        using var oldSession = await original.GetAsync("/account");
        Assert.That(oldSession.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        using var confirmed = await AuthClient.PostAsync(recovery, "/auth/codes/confirm", new()
        {
            ["saved"] = "true"
        });

        Assert.That(confirmed.Headers.Location?.OriginalString, Is.EqualTo("/auth/login"));

        using var notLoggedIn = await recovery.GetAsync("/account");
        Assert.That(notLoggedIn.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        using var oldCode = await StartAsync(recovery, oldCodes[1]);
        Assert.That(oldCode.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var login = await AuthClient.LoginAsync(recovery, password: "recovered-password");
        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(await db.SecurityAuditLogs.CountAsync(x => x.Event == "account-recovery"), Is.EqualTo(1));
    }

    /// <summary>復旧コードの5回制限は15分で解除し、通常パスワードを止めない</summary>
    [Test]
    public async Task RecoveryLockoutIsIndependentAndDoesNotConsumeValidCode()
    {
        await using var app = new TestApplication();
        using var existing = app.CreateBrowser();
        var codes = await AuthClient.RegisterAsync(existing);
        using var recovery = app.CreateBrowser();

        for (var i = 0; i < 5; i++)
        {
            using var failed = await StartAsync(recovery, "wrong-code");
        }

        using var blocked = await StartAsync(recovery, codes[0]);

        Assert.That(blocked.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var normal = app.CreateBrowser();
        using var login = await AuthClient.LoginAsync(normal);
        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        app.Clock.Advance(TimeSpan.FromMinutes(15));
        using var accepted = await StartAsync(recovery, codes[0]);

        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
    }

    /// <summary>中断した復旧は10分で失効し、旧パスワードは変更されない</summary>
    [Test]
    public async Task AbandonedRecoveryExpiresWithoutChangingCredentials()
    {
        await using var app = new TestApplication();
        using var original = app.CreateBrowser();
        var codes = await AuthClient.RegisterAsync(original);
        using var recovery = app.CreateBrowser();
        using var started = await StartAsync(recovery, codes[0]);
        app.Clock.Advance(TimeSpan.FromMinutes(10));

        using var expired = await AuthClient.PostAsync(recovery, "/auth/recovery/password", new()
        {
            ["Password"] = "recovered-password",
            ["PasswordConfirmation"] = "recovered-password"
        });

        Assert.That(expired.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        using var login = await AuthClient.LoginAsync(recovery);
        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
    }

    /// <summary>復旧の最終保存が失敗しても、パスワード・コード・セッションを部分更新しない</summary>
    [Test]
    public async Task RecoveryFailureRollsBackCredentialChanges()
    {
        await using var app = new TestApplication();
        using var original = app.CreateBrowser();
        var codes = await AuthClient.RegisterAsync(original);
        using var recovery = app.CreateBrowser();
        using var started = await StartAsync(recovery, codes[0]);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectRecovery BEFORE INSERT ON SecurityAuditLogs WHEN NEW.Event = 'account-recovery' BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        }

        using var failed = await AuthClient.PostAsync(recovery, "/auth/recovery/password", new()
        {
            ["Password"] = "recovered-password",
            ["PasswordConfirmation"] = "recovered-password"
        });

        Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));

        using var retained = await original.GetAsync("/account");
        Assert.That(retained.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var oldPassword = await AuthClient.LoginAsync(recovery);
        Assert.That(oldPassword.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        await using var check = app.Services.CreateAsyncScope();
        var database = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(await database.RecoveryCodes.CountAsync(), Is.EqualTo(9));
    }

    /// <summary>同じコードの同時消費は一方だけを復旧フローへ進める</summary>
    [Test]
    public async Task RecoveryCodeCannotBeConsumedConcurrently()
    {
        await using var app = new TestApplication();
        using var original = app.CreateBrowser();
        var codes = await AuthClient.RegisterAsync(original);
        using var first = app.CreateBrowser();
        using var second = app.CreateBrowser();

        var results = await Task.WhenAll(StartAsync(first, codes[0]), StartAsync(second, codes[0]));

        Assert.That(results.Select(x => x.StatusCode), Is.EquivalentTo(new[] { HttpStatusCode.OK, HttpStatusCode.Redirect }));

        foreach (var response in results)
        {
            response.Dispose();
        }
    }

    /// <summary>復旧開始用フォームを送る</summary>
    private static Task<HttpResponseMessage> StartAsync(HttpClient client, string code)
    {
        return AuthClient.PostAsync(client, "/auth/recovery", new()
        {
            ["LoginId"] = "owner",
            ["Code"] = code
        });
    }
}
