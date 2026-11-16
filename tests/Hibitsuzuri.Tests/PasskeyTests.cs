using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hibitsuzuri.Web.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>Identity標準の暗号検証とアプリの単回使用・所有者境界</summary>
public sealed class PasskeyTests
{
    /// <summary>名前未入力時は作成元を補完し、ゼロ・未知のAAGUIDでも登録を妨げない</summary>
    [TestCase("bada5566-a7aa-401f-bd96-45619a55120d", null, "1Password")]
    [TestCase("bada5566-a7aa-401f-bd96-45619a55120d", "", "1Password")]
    [TestCase("bada5566-a7aa-401f-bd96-45619a55120d", "　 ", "1Password")]
    [TestCase("d548826e-79b4-db40-a3d8-11116f7e8349", null, "Bitwarden")]
    [TestCase("ea9b8d66-4d01-1d21-3ce4-b6b48cb575d4", null, "Google Password Manager")]
    [TestCase("00000000-0000-0000-0000-000000000000", null, null)]
    [TestCase("11111111-2222-3333-4444-555555555555", null, null)]
    public async Task RegistrationAssignsNameFromProviderOrDate(string aaguid, string? name, string? expected)
    {
        await using var app = new TestApplication();
        using var owner = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, owner);
        using var authenticator = new TestAuthenticator();
        var options = await OptionsAsync(owner, "register");
        var credential = authenticator.Create(options.GetProperty("publicKeyOptions"), aaguid: Guid.Parse(aaguid));

        using var response = await VerifyAsync(owner, "register", options, credential, name);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        var passkey = (await ReadPasskeysAsync(app)).Single();
        expected ??= "Passkey（" + passkey.CreatedAt.ToLocalTime().ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture) + "登録）";
        Assert.That(passkey.Name, Is.EqualTo(expected));
    }

    /// <summary>入力名と登録後の変更名を優先し、ログインでも自動名へ戻さない</summary>
    [Test]
    public async Task CustomNameAndRenameSurvivePasskeyLogin()
    {
        await using var app = new TestApplication();
        using var owner = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, owner);
        using var authenticator = new TestAuthenticator();
        var options = await OptionsAsync(owner, "register");
        var credential = authenticator.Create(options.GetProperty("publicKeyOptions"), aaguid: Guid.Parse("bada5566-a7aa-401f-bd96-45619a55120d"));
        using var registered = await VerifyAsync(owner, "register", options, credential, "個人用の保管庫");
        Assert.That(registered.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await ReadPasskeysAsync(app)).Single().Name, Is.EqualTo("個人用の保管庫"));

        using var renamed = await AuthClient.PostAsync(owner, "/account/passkeys/rename", new()
        {
            ["id"] = authenticator.Id,
            ["name"] = "変更した表示名"
        });
        Assert.That(renamed.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        using var login = app.CreateBrowser();
        var loginOptions = await OptionsAsync(login, "login");
        using var signedIn = await VerifyAsync(login, "login", loginOptions, authenticator.Get(loginOptions.GetProperty("publicKeyOptions")));
        Assert.That(signedIn.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await ReadPasskeysAsync(app)).Single().Name, Is.EqualTo("変更した表示名"));
    }

    /// <summary>任意入力に変更しても、不正な名前を補完で隠して保存しない</summary>
    [TestCase(101, false)]
    [TestCase(1, true)]
    public async Task RegistrationStillRejectsInvalidName(int length, bool containsNull)
    {
        await using var app = new TestApplication();
        using var owner = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, owner);
        using var authenticator = new TestAuthenticator();
        var options = await OptionsAsync(owner, "register");
        var name = containsNull ? "\0" : new string('a', length);

        using var response = await VerifyAsync(owner, "register", options, authenticator.Create(options.GetProperty("publicKeyOptions")), name);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await ReadPasskeysAsync(app), Is.Empty);
    }

    /// <summary>拡張結果の欠落による400を再現し、空オブジェクトを送れば同じ登録処理が成功する</summary>
    [TestCase(false, HttpStatusCode.BadRequest)]
    [TestCase(true, HttpStatusCode.OK)]
    public async Task RegistrationRequiresClientExtensionResults(bool includeExtensions, HttpStatusCode expected)
    {
        await using var app = new TestApplication();
        using var owner = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, owner);
        using var authenticator = new TestAuthenticator();
        var options = await OptionsAsync(owner, "register");
        var credential = JsonNode.Parse(authenticator.Create(options.GetProperty("publicKeyOptions")))?.AsObject()
            ?? throw new InvalidOperationException("試験用の登録応答がありません。");
        if (!includeExtensions)
        {
            credential.Remove("clientExtensionResults");
        }

        using var response = await VerifyAsync(owner, "register", options, credential.ToJsonString());

        Assert.That(response.StatusCode, Is.EqualTo(expected), await response.Content.ReadAsStringAsync());
    }

    /// <summary>実署名で登録・ログイン・再認証し、リプレイを拒否する</summary>
    [Test]
    public async Task SignedPasskeyWorksAndCannotBeReplayed()
    {
        await using var app = new TestApplication();
        using var owner = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, owner);
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
        await AuthClient.RegisterAsync(app, owner);
        using var authenticator = new TestAuthenticator();
        var options = await OptionsAsync(owner, "register");

        using var rejected = await VerifyAsync(owner, "register", options,
            authenticator.Create(options.GetProperty("publicKeyOptions"), origin, verified, Guid.Parse("bada5566-a7aa-401f-bd96-45619a55120d")), null);

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
        await AuthClient.RegisterAsync(app, owner);
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
        await AuthClient.RegisterAsync(app, owner);
        using var authenticator = new TestAuthenticator();
        await RegisterKeyAsync(owner, authenticator);
        using var other = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, other, "another", false);

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
        var codes = await AuthClient.RegisterAsync(app, owner);
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
    private static Task<HttpResponseMessage> VerifyAsync(HttpClient client, string purpose, JsonElement options, string credential, string? name = "試験用Passkey")
    {
        var fields = new Dictionary<string, string>
        {
            ["ChallengeId"] = options.GetProperty("challengeId").GetString() ?? "",
            ["Credential"] = credential
        };
        if (name is not null)
        {
            fields["Name"] = name;
        }

        return AuthClient.PostAsync(client, $"/api/passkeys/{purpose}/verify", fields);
    }

    /// <summary>画面表示とは別に、アプリが永続化した表示名と登録日を確認する</summary>
    private static async Task<IList<UserPasskeyInfo>> ReadPasskeysAsync(TestApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var owner = await users.FindByNameAsync("owner") ?? throw new InvalidOperationException("試験ユーザーがありません。");

        return await users.GetPasskeysAsync(owner);
    }
}
