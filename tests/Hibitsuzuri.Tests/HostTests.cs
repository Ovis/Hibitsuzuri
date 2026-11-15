using System.Net;
using Microsoft.AspNetCore.Http;

namespace Hibitsuzuri.Tests;

/// <summary>公開URL・プロキシ・エラー応答・CSRFのHTTP境界を検証する</summary>
public sealed class HostTests
{
    /// <summary>画面から直接読み込むスクリプトとimport先の両方で更新確認を要求する</summary>
    [TestCase("/js/diary-editor.js")]
    [TestCase("/js/diary-images.js")]
    [TestCase("/js/diary-drafts.js")]
    [TestCase("/sw.js")]
    public async Task ScriptsRequireCacheRevalidation(string path)
    {
        await using var app = new TestApplication();
        using var browser = app.CreateBrowser();

        using var response = await browser.GetAsync(path);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.CacheControl?.NoCache, Is.True);
            Assert.That(response.Headers.ETag, Is.Not.Null);
        });
    }

    /// <summary>MVC画面は日本語で表示し、個人データ向けのキャッシュ禁止を既定にする</summary>
    [Test]
    public async Task HomeUsesJapaneseMvcViewAndNoStore()
    {
        await using var app = new TestApplication();
        using var browser = app.CreateBrowser();

        using var response = await browser.GetAsync("/auth/register");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain("lang=\"ja\"").And.Contain("Hibitsuzuri"));
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
            Assert.That(response.Headers.GetValues("Content-Security-Policy").Single(), Does.Contain("frame-ancestors 'none'"));
        });
    }

    /// <summary>未知のHostと転送先Hostの偽装を拒否する</summary>
    [TestCase("KnownProxies")]
    [TestCase("DeploymentManaged")]
    public async Task UnknownHostIsRejectedEvenWithForwardedHost(string mode)
    {
        await using var app = new TestApplication();
        app.Settings["ReverseProxy:Mode"] = mode;
        using var browser = app.CreateBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "attacker.example";
        request.Headers.Add("X-Forwarded-Host", "diary.example.test");

        using var response = await browser.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>HTTP閲覧は公開先へ転送し、HTTP更新は実行せず失敗させる</summary>
    [TestCase("KnownProxies")]
    [TestCase("DeploymentManaged")]
    public async Task PlainHttpRedirectsReadsButRejectsWrites(string mode)
    {
        await using var app = new TestApplication();
        app.Settings["ReverseProxy:Mode"] = mode;
        using var browser = app.CreateBrowser();

        using var read = await browser.GetAsync("http://diary.example.test/fixture/request?q=1");
        using var write = await browser.PostAsync("http://diary.example.test/fixture/write", new StringContent(""));

        Assert.Multiple(() =>
        {
            Assert.That((int)read.StatusCode, Is.EqualTo(308));
            Assert.That(read.Headers.Location?.AbsoluteUri, Is.EqualTo("https://diary.example.test/fixture/request?q=1"));
            Assert.That(write.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        });
    }

    /// <summary>HTTPS情報と利用者IPは、明示した直近のプロキシからだけ受け入れる</summary>
    [TestCase("10.20.0.2", 200)]
    [TestCase("10.20.0.3", 308)]
    public async Task ForwardedHeadersRequireKnownProxy(string remoteIp, int expectedStatus)
    {
        await using var app = new TestApplication();

        var response = await app.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("diary.example.test");
            context.Request.Method = "GET";
            context.Request.Path = "/fixture/request";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            context.Request.Headers["X-Forwarded-For"] = "203.0.113.7";
            context.Request.Headers["X-Forwarded-Host"] = "attacker.example";
        });

        Assert.That(response.Response.StatusCode, Is.EqualTo(expectedStatus));
        if (expectedStatus == 200)
        {
            using var reader = new StreamReader(response.Response.Body);
            Assert.That(await reader.ReadToEndAsync(), Is.EqualTo("https|diary.example.test|203.0.113.7"));
        }
    }

    /// <summary>プロキシ未設定では転送ヘッダーの処理自体を無効にする</summary>
    [Test]
    public async Task EmptyProxyListDoesNotTrustEverySender()
    {
        await using var app = new TestApplication();
        app.Settings.Remove("ReverseProxy:KnownProxies:0");
        using var browser = app.CreateBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://diary.example.test/");
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await browser.SendAsync(request);

        Assert.That((int)response.StatusCode, Is.EqualTo(308));
    }

    /// <summary>配置側管理では直近1段のHTTPS情報だけを採用し、申告されたIPとHostは無視する</summary>
    [TestCase("https", 200)]
    [TestCase("http, https", 200)]
    [TestCase("https, http", 308)]
    public async Task DeploymentManagedProxyOnlyAcceptsNearestScheme(string forwardedProto, int expectedStatus)
    {
        await using var app = new TestApplication();
        app.Settings["ReverseProxy:Mode"] = "DeploymentManaged";
        app.Settings.Remove("ReverseProxy:KnownProxies:0");

        var response = await app.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("10.20.0.3");
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("diary.example.test");
            context.Request.Method = "GET";
            context.Request.Path = "/fixture/request";
            context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
            context.Request.Headers["X-Forwarded-For"] = "203.0.113.7";
            context.Request.Headers["X-Forwarded-Host"] = "attacker.example";
        });

        Assert.That(response.Response.StatusCode, Is.EqualTo(expectedStatus));
        if (expectedStatus == 200)
        {
            using var reader = new StreamReader(response.Response.Body);
            Assert.That(await reader.ReadToEndAsync(), Is.EqualTo("https|diary.example.test|10.20.0.3"));
        }
    }

    /// <summary>配置側管理の認証要求はプロキシIPで枠を共有し、利用者IPの申告では制限を回避できない</summary>
    [Test]
    public async Task DeploymentManagedAuthenticationSharesProxyLimit()
    {
        await using var app = new TestApplication();
        app.Settings["ReverseProxy:Mode"] = "DeploymentManaged";
        app.Settings.Remove("ReverseProxy:KnownProxies:0");

        for (var attempt = 1; attempt <= 31; attempt++)
        {
            var response = await app.Server.SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("10.20.0.3");
                context.Request.Scheme = "http";
                context.Request.Host = new HostString("diary.example.test");
                context.Request.Method = "GET";
                context.Request.Path = "/auth/login";
                context.Request.Headers["X-Forwarded-Proto"] = "https";
                context.Request.Headers["X-Forwarded-For"] = $"203.0.113.{attempt}";
            });

            Assert.That(response.Response.StatusCode, Is.EqualTo(attempt <= 30 ? 302 : 429), $"認証要求 {attempt}回目");
        }
    }

    /// <summary>エラー応答は元の失敗を維持し、APIへHTMLを返さない</summary>
    [TestCase("/missing", 404, "text/html")]
    [TestCase("/api/missing", 404, "application/problem+json")]
    [TestCase("/fixture/failure", 500, "text/html")]
    [TestCase("/api/fixture/failure", 500, "application/problem+json")]
    public async Task FailuresKeepStatusAndHideExceptionDetails(string path, int statusCode, string mediaType)
    {
        await using var app = new TestApplication();
        using var browser = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, browser);

        using var response = await browser.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That((int)response.StatusCode, Is.EqualTo(statusCode));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(mediaType));
            Assert.That(body, Does.Not.Contain("private-failure-detail"));
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        });
    }

    /// <summary>更新にはCookieと対応するトークンが必要で、保護CookieはHTTPS専用にする</summary>
    [TestCase("KnownProxies")]
    [TestCase("DeploymentManaged")]
    public async Task MvcWritesRequireAntiforgeryToken(string mode)
    {
        await using var app = new TestApplication();
        app.Settings["ReverseProxy:Mode"] = mode;
        using var browser = app.CreateBrowser();

        using var rejected = await browser.PostAsync("/fixture/write", new StringContent(""));
        using var tokenResponse = await browser.GetAsync("/fixture/token");
        var token = await tokenResponse.Content.ReadAsStringAsync();
        using var accepted = await browser.PostAsync("/fixture/write", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        }));

        Assert.Multiple(() =>
        {
            Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(tokenResponse.Headers.GetValues("Set-Cookie").Single(),
                Does.Contain("__Host-Hibitsuzuri.Antiforgery").And.Contain("secure").And.Contain("httponly").And.Contain("samesite=strict"));
        });
    }

    /// <summary>ヘルスチェックは実際のループバック接続だけに応答し、転送ヘッダーでは偽装できない</summary>
    [TestCase("127.0.0.1", "KnownProxies", 200)]
    [TestCase("10.20.0.2", "KnownProxies", 404)]
    [TestCase("127.0.0.1", "DeploymentManaged", 200)]
    [TestCase("10.20.0.2", "DeploymentManaged", 404)]
    public async Task ReadinessIsOnlyAvailableInsideHost(string remoteIp, string mode, int expectedStatus)
    {
        await using var app = new TestApplication();
        app.Settings["ReverseProxy:Mode"] = mode;

        var response = await app.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("diary.example.test");
            context.Request.Method = "GET";
            context.Request.Path = "/health/ready";
            context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        });

        Assert.That(response.Response.StatusCode, Is.EqualTo(expectedStatus));
    }

    /// <summary>DB・鍵・画像の保存領域を静的ファイルとして公開しない</summary>
    [TestCase("/data/hibitsuzuri.db")]
    [TestCase("/data/keys/test.xml")]
    [TestCase("/data/images/test.png")]
    public async Task DataFilesAreNotPublic(string path)
    {
        await using var app = new TestApplication();
        using var browser = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, browser);
        Directory.CreateDirectory(Path.Combine(app.DataPath, "images"));
        await File.WriteAllTextAsync(Path.Combine(app.DataPath, "images", "test.png"), "private-image");
        await File.WriteAllTextAsync(Path.Combine(app.DataPath, "keys", "test.xml"), "private-key");

        using var response = await browser.GetAsync(path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
