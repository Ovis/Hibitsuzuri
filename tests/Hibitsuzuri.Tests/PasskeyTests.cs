using System.Net;
using System.Text.Json;

namespace Hibitsuzuri.Tests;

/// <summary>Identity標準の暗号検証とアプリの単回使用・所有者境界</summary>
public sealed class PasskeyTests
{
    /// <summary>実署名で登録・ログイン・再認証し、リプレイを拒否する</summary>
    [Test]
    public async Task SignedPasskeyWorksAndCannotBeReplayed()
    {
        await using var app = new TestApplication();
        using var owner = app.CreateBrowser();
        await AuthClient.RegisterAsync(owner);
        using var authenticator = new TestAuthenticator();
        await RegisterKeyAsync(owner, authenticator);

        using var login = app.CreateBrowser();
        var options = await OptionsAsync(login, "login");
        var credential = authenticator.Get(options.GetProperty("publicKeyOptions"));

        using var success = await VerifyAsync(login, "login", options, credential);

        Assert.That(success.StatusCode, Is.EqualTo(HttpStatusCode.OK), await success.Content.ReadAsStringAsync());

        using var page = await login.GetAsync("/account");
        Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var replay = await VerifyAsync(login, "login", options, credential);
        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        app.Clock.Advance(TimeSpan.FromMinutes(10));
        var reauth = await OptionsAsync(login, "reauthenticate");
        using var verified = await VerifyAsync(login, "reauthenticate", reauth, authenticator.Get(reauth.GetProperty("publicKeyOptions")));

        Assert.That(verified.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var passwordPage = await login.GetAsync("/account/password");
        Assert.That(passwordPage.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    /// <summary>異なるOriginと本人確認なしの登録を拒否する</summary>
    [TestCase("https://attacker.example", true)]
    [TestCase("https://diary.example.test", false)]
    public async Task RegistrationRejectsWrongOriginOrMissingVerification(string origin, bool verified)
    {
        await using var app = new TestApplication();
        using var owner = app.CreateBrowser();
        await AuthClient.RegisterAsync(owner);
        using var authenticator = new TestAuthenticator();
        var options = await OptionsAsync(owner, "register");

        using var rejected = await VerifyAsync(owner, "register", options, authenticator.Create(options.GetProperty("publicKeyOptions"), origin, verified));

        Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>改変署名・別Origin・期限切れ・不正JSONを拒否する</summary>
    [TestCase("signature")]
    [TestCase("origin")]
    [TestCase("expired")]
    [TestCase("json")]
    public async Task InvalidAssertionsAreRejected(string failure)
    {
        await using var app = new TestApplication();
        using var owner = app.CreateBrowser();
        await AuthClient.RegisterAsync(owner);
        using var authenticator = new TestAuthenticator();
        await RegisterKeyAsync(owner, authenticator);
        using var client = app.CreateBrowser();
        var options = await OptionsAsync(client, "login");
        var credential = failure == "json" ? "{" : authenticator.Get(options.GetProperty("publicKeyOptions"),
            origin: failure == "origin" ? "https://attacker.example" : "https://diary.example.test", badSignature: failure == "signature");
        if (failure == "expired")
        {
            app.Clock.Advance(TimeSpan.FromMinutes(5));
        }

        using var rejected = await VerifyAsync(client, "login", options, credential);

        Assert.That(rejected.StatusCode, Is.AnyOf(HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized));
    }

    /// <summary>パスワード停止中でもPasskeyでログインでき、他者は名前変更・削除できない</summary>
    [Test]
    public async Task PasswordLockoutDoesNotBlockPasskeyAndOwnersAreIsolated()
    {
        await using var app = new TestApplication();
        using var owner = app.CreateBrowser();
        await AuthClient.RegisterAsync(owner);
        using var authenticator = new TestAuthenticator();
        await RegisterKeyAsync(owner, authenticator);
        using var other = app.CreateBrowser();
        await AuthClient.RegisterAsync(other, "another", false);

        using var rename = await AuthClient.PostAsync(other, "/account/passkeys/rename", new()
        {
            ["id"] = authenticator.Id,
            ["name"] = "stolen"
        });
        using var delete = await AuthClient.PostAsync(other, "/account/passkeys/delete", new()
        {
            ["id"] = authenticator.Id,
            ["confirmed"] = "true"
        });

        Assert.That(rename.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(delete.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        using var client = app.CreateBrowser();
        for (var i = 0; i < 5; i++)
        {
            using var failed = await AuthClient.LoginAsync(client, password: "wrong-password");
        }

        var options = await OptionsAsync(client, "login");
        using var success = await VerifyAsync(client, "login", options, authenticator.Get(options.GetProperty("publicKeyOptions")));

        Assert.That(success.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    /// <summary>復旧前に発行済みのチャレンジを、復旧後に使用できない</summary>
    [Test]
    public async Task RecoveryInvalidatesPasskeysAndPendingAssertions()
    {
        await using var app = new TestApplication();
        using var owner = app.CreateBrowser();
        var codes = await AuthClient.RegisterAsync(owner);
        using var authenticator = new TestAuthenticator();
        await RegisterKeyAsync(owner, authenticator);
        using var login = app.CreateBrowser();
        var options = await OptionsAsync(login, "login");

        using var recovery = app.CreateBrowser();
        using var start = await AuthClient.PostAsync(recovery, "/auth/recovery", new()
        {
            ["LoginId"] = "owner",
            ["Code"] = codes[0]
        });
        using var complete = await AuthClient.PostAsync(recovery, "/auth/recovery/password", new()
        {
            ["Password"] = "recovered-password",
            ["PasswordConfirmation"] = "recovered-password"
        });
        Assert.That(complete.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var rejected = await VerifyAsync(login, "login", options, authenticator.Get(options.GetProperty("publicKeyOptions")));

        Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    /// <summary>Identity標準の登録処理を通して試験鍵を保存する</summary>
    private static async Task RegisterKeyAsync(HttpClient client, TestAuthenticator authenticator)
    {
        var options = await OptionsAsync(client, "register");
        using var registered = await VerifyAsync(client, "register", options, authenticator.Create(options.GetProperty("publicKeyOptions")));

        Assert.That(registered.StatusCode, Is.EqualTo(HttpStatusCode.OK), await registered.Content.ReadAsStringAsync());
    }

    /// <summary>CSRF付きでチャレンジと認証器用オプションを取得する</summary>
    private static async Task<JsonElement> OptionsAsync(HttpClient client, string purpose)
    {
        using var response = await AuthClient.PostAsync(client, $"/api/passkeys/{purpose}/options");
        var json = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), json);

        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    /// <summary>試験認証器が生成した応答をサーバーへ渡す</summary>
    private static Task<HttpResponseMessage> VerifyAsync(HttpClient client, string purpose, JsonElement options, string credential)
    {
        return AuthClient.PostAsync(client, $"/api/passkeys/{purpose}/verify", new()
        {
            ["ChallengeId"] = options.GetProperty("challengeId").GetString() ?? "",
            ["Credential"] = credential,
            ["Name"] = "試験用Passkey"
        });
    }
}
