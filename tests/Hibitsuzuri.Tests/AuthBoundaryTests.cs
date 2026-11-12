using System.Net;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>ログアウト・入力上限・監査保持・公開経路の回帰試験</summary>
public sealed class AuthBoundaryTests
{
    /// <summary>コード保存前のログアウトでも、残った専用Cookieからログインできない</summary>
    [Test]
    public async Task LogoutCancelsPendingCodeConfirmation()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        using var registered = await AuthClient.PostAsync(client, "/auth/register", new()
        {
            ["LoginId"] = "owner",
            ["Password"] = AuthClient.Password,
            ["PasswordConfirmation"] = AuthClient.Password,
            ["SetupCode"] = "test-only-initial-setup-code"
        });
        Assert.That(registered.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var logout = await AuthClient.PostAsync(client, "/auth/logout");
        using var replay = await AuthClient.PostAsync(client, "/auth/codes/confirm", new()
        {
            ["saved"] = "true"
        });
        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using var page = await client.GetAsync("/account");
        Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
    }

    /// <summary>初回コードを失った表示確認は、新しいコードの発行で無効になる</summary>
    [Test]
    public async Task ResumingRegistrationInvalidatesOlderConfirmation()
    {
        await using var app = new TestApplication();
        using var first = app.CreateBrowser();
        using var registered = await AuthClient.PostAsync(first, "/auth/register", new()
        {
            ["LoginId"] = "owner",
            ["Password"] = AuthClient.Password,
            ["PasswordConfirmation"] = AuthClient.Password,
            ["SetupCode"] = "test-only-initial-setup-code"
        });
        using var second = app.CreateBrowser();
        using var resume = await AuthClient.LoginAsync(second);
        Assert.That(AuthClient.CodePattern().Matches(await resume.Content.ReadAsStringAsync()).Count, Is.EqualTo(10));
        using var stale = await AuthClient.PostAsync(first, "/auth/codes/confirm", new()
        {
            ["saved"] = "true"
        });
        Assert.That(stale.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using var fresh = await AuthClient.PostAsync(second, "/auth/codes/confirm", new()
        {
            ["saved"] = "true"
        });
        Assert.That(fresh.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
    }

    /// <summary>期限切れは記録を維持し、その後の明示ログアウトは失効時刻を記録する</summary>
    [Test]
    public async Task ExplicitLogoutAfterNaturalExpiryRecordsRevocation()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        app.Clock.Advance(TimeSpan.FromHours(8));
        using var logout = await AuthClient.PostAsync(client, "/auth/logout");
        Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(await db.AuthSessions.AllAsync(x => x.RevokedAt != null), Is.True);
    }

    /// <summary>重要イベントだけを保持し、90日ちょうどの境界で回収する</summary>
    [Test]
    public async Task AuditRetentionUsesEventTimeAndOmitsOrdinaryLogins()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        using var other = app.CreateBrowser();
        using var failed = await AuthClient.LoginAsync(other, password: "wrong-password");
        using var success = await AuthClient.LoginAsync(other);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(await db.SecurityAuditLogs.Select(x => x.Event).ToArrayAsync(), Is.EqualTo(new[] { "setup" }));
        var cutoff = app.Clock.GetUtcNow().AddDays(-90).ToUnixTimeMilliseconds();
        db.SecurityAuditLogs.AddRange(
            new SecurityAuditLog { Event = "expired", OccurredAt = cutoff },
            new SecurityAuditLog { Event = "retained", OccurredAt = cutoff + 1 });
        await db.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<AuthAudit>().PruneAsync();
        Assert.That(await db.SecurityAuditLogs.Select(x => x.Event).ToArrayAsync(), Is.EquivalentTo(new[] { "setup", "retained" }));
    }

    /// <summary>エラーのHTMLへパスワードを再出力せず、過大な入力を拒否する</summary>
    [Test]
    public async Task InvalidPasswordIsNeverEchoedAndLargeFormsAreRejected()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        using var anonymous = app.CreateBrowser();
        const string secret = "secret-that-must-not-be-echoed";
        using var failure = await AuthClient.LoginAsync(anonymous, password: secret);
        Assert.That(await failure.Content.ReadAsStringAsync(), Does.Not.Contain(secret));
        using var excessive = await AuthClient.LoginAsync(anonymous, password: new string('x', 2000));
        Assert.That(excessive.StatusCode, Is.AnyOf(HttpStatusCode.BadRequest, HttpStatusCode.RequestEntityTooLarge));
    }

    /// <summary>認証更新にはCSRFが必要で、IP単位の大量要求を制限する</summary>
    [Test]
    public async Task PasskeyOptionsRequireCsrfAndTrafficIsBounded()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        using var noToken = await client.PostAsync("/api/passkeys/login/options", new FormUrlEncodedContent([]));
        Assert.That(noToken.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 31; i++)
        {
            using var response = await client.GetAsync("/auth/register");
            last = response.StatusCode;
        }

        Assert.That(last, Is.EqualTo(HttpStatusCode.TooManyRequests));
    }
}
