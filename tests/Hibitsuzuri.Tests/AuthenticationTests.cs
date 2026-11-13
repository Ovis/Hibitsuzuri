using System.Net;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>初回登録の排他性・コード保存・通常認証の境界</summary>
public sealed class AuthenticationTests
{
    /// <summary>同時の初回登録でも管理者は一人だけで、保存確認前には通常利用を許可しない</summary>
    [Test]
    public async Task SetupIsExclusiveAndRequiresCodeConfirmation()
    {
        await using var app = new TestApplication();
        using var first = app.CreateBrowser();
        using var second = app.CreateBrowser();

        var responses = await Task.WhenAll(new[] { first, second }.Select((client, i) => AuthClient.PostAsync(client, "/auth/register", new()
        {
            ["LoginId"] = "owner" + i,
            ["Password"] = AuthClient.Password,
            ["PasswordConfirmation"] = AuthClient.Password,
            ["SetupCode"] = "test-only-initial-setup-code"
        })));

        Assert.That(responses.Select(x => x.StatusCode), Is.EquivalentTo(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));

        using var page = await first.GetAsync("/account");
        Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(await db.Users.CountAsync(x => x.IsAdmin), Is.EqualTo(1));
        Assert.That(await db.AuthSessions.CountAsync(), Is.Zero);

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>コードはハッシュだけを永続化し、通常登録では管理者権限を付けない</summary>
    [Test]
    public async Task RegistrationSeparatesOwnersAndStoresNoPlainCodes()
    {
        await using var app = new TestApplication();
        using var owner = app.CreateBrowser();
        using var member = app.CreateBrowser();
        var codes = await AuthClient.RegisterAsync(owner);
        await AuthClient.RegisterAsync(member, "member", false);

        var html = await member.GetStringAsync("/account");

        Assert.That(html, Does.Contain("member").And.Not.Contain(">owner<"));

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(await db.Users.CountAsync(x => x.IsAdmin), Is.EqualTo(1));
        Assert.That(await db.RecoveryCodes.CountAsync(), Is.EqualTo(20));
        Assert.That(await db.RecoveryCodes.Select(x => x.Hash).ToListAsync(), Has.None.Matches<string>(codes.Contains));
    }

    /// <summary>保持指定による絶対期限とCookieの寿命を区別し、利用中も延長しない</summary>
    [TestCase(false, 8)]
    [TestCase(true, 720)]
    public async Task SessionExpiresAtFixedBoundary(bool remember, int hours)
    {
        await using var app = new TestApplication();
        using var setup = app.CreateBrowser();
        await AuthClient.RegisterAsync(setup);
        using var client = app.CreateBrowser();

        using var login = await AuthClient.LoginAsync(client, remember: remember);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-Hibitsuzuri.Session=", StringComparison.Ordinal));

        Assert.That(cookie.Contains("expires=", StringComparison.Ordinal), Is.EqualTo(remember));
        Assert.That(cookie, Does.Contain("secure").And.Contain("httponly"));

        app.Clock.Advance(TimeSpan.FromHours(hours) - TimeSpan.FromSeconds(1));
        using var valid = await client.GetAsync("/account");

        Assert.That(valid.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        using var expired = await client.GetAsync("/account");

        Assert.That(expired.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(await db.AuthSessions.AnyAsync(x => x.RevokedAt != null), Is.False, "自然期限切れを通知停止と混同しない");
    }

    /// <summary>5回の失敗でパスワードだけを5分停止し、既存の認証を維持する</summary>
    [Test]
    public async Task PasswordLockoutDoesNotRevokeExistingSession()
    {
        await using var app = new TestApplication();
        using var existing = app.CreateBrowser();
        await AuthClient.RegisterAsync(existing);
        using var attacker = app.CreateBrowser();

        for (var i = 0; i < 5; i++)
        {
            using var failed = await AuthClient.LoginAsync(attacker, password: "wrong-password");
            Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        using var blocked = await AuthClient.LoginAsync(attacker);

        Assert.That(blocked.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var stillValid = await existing.GetAsync("/account");
        Assert.That(stillValid.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        app.Clock.Advance(TimeSpan.FromMinutes(5));
        using var accepted = await AuthClient.LoginAsync(attacker);

        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
    }

    /// <summary>Unicodeスカラー値の上下限を使い、サロゲート不正やNULを拒否する</summary>
    [Test]
    public void PasswordCountsUnicodeScalars()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AuthRules.ValidText(string.Concat(Enumerable.Repeat("😀", 12)), 12, 128), Is.True);
            Assert.That(AuthRules.ValidText(new string('a', 11), 12, 128), Is.False);
            Assert.That(AuthRules.ValidText(new string('a', 128), 12, 128), Is.True);
            Assert.That(AuthRules.ValidText(new string('a', 129), 12, 128), Is.False);
            Assert.That(AuthRules.ValidText("abcdefghijkl\ud800", 12, 128), Is.False);
            Assert.That(AuthRules.ValidText("abcdefghijkl\0", 12, 128), Is.False);
        });
    }
}
