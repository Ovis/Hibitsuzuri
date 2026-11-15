using System.Globalization;
using System.Net;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>初回コードの永続化・失効・保存先異常と初回登録完了判定の回帰試験</summary>
public sealed class SetupTokenTests
{
    /// <summary>生成した256ビットのコードと期限は、24時間以内の再起動で変わらない</summary>
    [Test]
    public async Task RestartPreservesUnexpiredCode()
    {
        var first = new TestApplication { PreserveData = true };
        string original;
        await using (first)
        {
            using var browser = first.CreateBrowser();
            original = File.ReadAllText(TokenPath(first));
            var lines = File.ReadAllLines(TokenPath(first));
            Assert.That(lines, Has.Length.EqualTo(2));
            Assert.That(lines[0], Does.Match("^[0-9a-f]{64}$"));
            Assert.That(DateTimeOffset.Parse(lines[1], CultureInfo.InvariantCulture), Is.EqualTo(first.Clock.GetUtcNow().AddHours(24)));
            Assert.That(first.Services.GetRequiredService<SetupToken>().Verify(lines[0]), Is.True);
            Assert.That(await browser.GetStringAsync("/auth/register"), Does.Not.Contain(lines[0]));
        }

        await using var restarted = new TestApplication(first.DataPath);
        using var second = restarted.CreateBrowser();
        Assert.That(File.ReadAllText(TokenPath(restarted)), Is.EqualTo(original));
    }

    /// <summary>期限切れを実行中に延長せず、次の起動時にだけ新しいコードへ切り替える</summary>
    [Test]
    public async Task ExpiredCodeIsReissuedOnRestart()
    {
        var first = new TestApplication { PreserveData = true };
        string previous;
        await using (first)
        {
            using var browser = first.CreateBrowser();
            previous = first.ReadSetupCode();
            first.Clock.Advance(TimeSpan.FromHours(24));
            Assert.That(first.Services.GetRequiredService<SetupToken>().Verify(previous), Is.False);
            Assert.That(first.ReadSetupCode(), Is.EqualTo(previous));
        }

        await using var restarted = new TestApplication(first.DataPath);
        restarted.Clock.Advance(TimeSpan.FromHours(25));
        using var second = restarted.CreateBrowser();
        Assert.That(restarted.ReadSetupCode(), Is.Not.EqualTo(previous));
        Assert.That(restarted.Services.GetRequiredService<SetupToken>().Verify(restarted.ReadSetupCode()), Is.True);
        Assert.That(restarted.Services.GetRequiredService<SetupToken>().Verify(previous), Is.False);
    }

    /// <summary>入力失敗では消費もHTMLへの再出力もせず、登録確定後はファイルとメモリの両方で失効する</summary>
    [Test]
    public async Task FailedRegistrationPreservesCodeAndCommittedRegistrationInvalidatesIt()
    {
        await using var app = new TestApplication();
        using var browser = app.CreateBrowser();
        var code = app.ReadSetupCode();
        using var failed = await AuthClient.PostAsync(browser, "/auth/register", new()
        {
            ["LoginId"] = "owner",
            ["Password"] = "short",
            ["PasswordConfirmation"] = "short",
            ["SetupCode"] = code
        });

        Assert.That(await failed.Content.ReadAsStringAsync(), Does.Not.Contain(code));
        Assert.That(app.Services.GetRequiredService<SetupToken>().Verify(code), Is.True);
        Assert.That(app.ReadSetupCode(), Is.EqualTo(code));

        await AuthClient.RegisterAsync(app, browser);
        Assert.That(File.Exists(TokenPath(app)), Is.False);
        Assert.That(app.Services.GetRequiredService<SetupToken>().Verify(code), Is.False);
    }

    /// <summary>完了後にユーザーがゼロになっても、残ったファイルの削除に失敗しても初回登録を再開しない</summary>
    [Test]
    public async Task CompletedPolicyPreventsReissueEvenWithoutUsersAndWithCleanupFailure()
    {
        var first = new TestApplication { PreserveData = true };
        await using (first)
        {
            using var browser = first.CreateBrowser();
            await AuthClient.RegisterAsync(first, browser);
            await using var scope = first.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Users.ExecuteDeleteAsync();
            var policy = await db.AccountPolicies.SingleAsync();
            policy.RegistrationEnabled = false;
            await db.SaveChangesAsync();

            // ファイル位置をディレクトリで占有し、削除できない状態をOS共通で作る。
            Directory.CreateDirectory(TokenPath(first));
        }

        await using var restarted = new TestApplication(first.DataPath);
        using var second = restarted.CreateBrowser();
        Assert.That(Directory.Exists(TokenPath(restarted)), Is.True);
        Assert.That(restarted.Services.GetRequiredService<SetupToken>().Verify(new string('a', 64)), Is.False);
        using var registration = await second.GetAsync("/auth/register");
        Assert.That(registration.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        Directory.Delete(TokenPath(restarted));
        restarted.Services.GetRequiredService<SetupToken>().Initialize(true);
        Assert.That(File.Exists(TokenPath(restarted)), Is.False);
    }

    /// <summary>期限や形式が不正な既存ファイルは上書きせず、起動失敗として検知する</summary>
    [TestCase("broken")]
    [TestCase("too-large")]
    [TestCase("future")]
    [TestCase("extra-line")]
    public async Task InvalidFileStopsStartupWithoutReplacement(string kind)
    {
        await using var app = new TestApplication();
        Directory.CreateDirectory(app.DataPath);
        var expiry = app.Clock.GetUtcNow().AddHours(kind == "future" ? 48 : 1).ToString("O", CultureInfo.InvariantCulture);
        var content = new string('a', 64) + "\n" + expiry + "\n";
        content = kind switch
        {
            "broken" => "invalid\n",
            "too-large" => new string('a', 257),
            "extra-line" => content + "unexpected\n",
            _ => content
        };
        WritePrivateFile(TokenPath(app), content);

        Assert.Throws<InvalidDataException>(() => app.CreateBrowser());
        Assert.That(File.ReadAllText(TokenPath(app)), Is.EqualTo(content));
    }

    /// <summary>起動後のファイル欠損・破損は登録を拒否し、自動再生成しない</summary>
    [Test]
    public async Task ChangedOrMissingFileRejectsVerification()
    {
        await using var app = new TestApplication();
        using var browser = app.CreateBrowser();
        var code = app.ReadSetupCode();
        File.WriteAllText(TokenPath(app), "invalid");
        Assert.That(app.Services.GetRequiredService<SetupToken>().Verify(code), Is.False);

        File.Delete(TokenPath(app));
        Assert.That(app.Services.GetRequiredService<SetupToken>().Verify(code), Is.False);
        Assert.That(File.Exists(TokenPath(app)), Is.False);
    }

    /// <summary>排他的に使用されて読めないファイルは上書きせず、照合と初期化を拒否する</summary>
    [Test]
    public async Task UnreadableFileIsNotReplaced()
    {
        await using var app = new TestApplication();
        using var browser = app.CreateBrowser();
        var code = app.ReadSetupCode();
        using (var locked = new FileStream(TokenPath(app), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var token = app.Services.GetRequiredService<SetupToken>();
            Assert.That(token.Verify(code), Is.False);
            Assert.Throws<IOException>(() => token.Initialize(false));
        }

        Assert.That(app.ReadSetupCode(), Is.EqualTo(code));
    }

    /// <summary>Linuxでは生成権限を0600に限定し、広すぎる権限とリンクを拒否する</summary>
    [Test]
    [Platform(Exclude = "Win")]
    public async Task UnixPermissionsAndLinksAreRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var app = new TestApplication();
        using var browser = app.CreateBrowser();
        var token = app.Services.GetRequiredService<SetupToken>();
        var code = app.ReadSetupCode();
        Assert.That(File.GetUnixFileMode(TokenPath(app)), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));

        File.SetUnixFileMode(TokenPath(app), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.That(token.Verify(code), Is.False);
        Assert.Throws<InvalidDataException>(() => token.Initialize(false));

        var target = Path.Combine(app.DataPath, "original-token");
        File.Move(TokenPath(app), target);
        File.CreateSymbolicLink(TokenPath(app), target);
        Assert.Throws<InvalidDataException>(() => token.Initialize(false));
        token.Invalidate();
        Assert.That(File.Exists(target), Is.True);
        File.Delete(TokenPath(app));

        File.CreateSymbolicLink(TokenPath(app), Path.Combine(app.DataPath, "missing"));
        Assert.Throws<InvalidDataException>(() => token.Initialize(false));
        File.Delete(TokenPath(app));
    }

    /// <summary>試験用ホストの非公開コードファイルを特定する</summary>
    private static string TokenPath(TestApplication app) => Path.Combine(app.DataPath, "setup-token.txt");

    /// <summary>起動前に置く異常ファイルにも、検証対象以外の権限エラーを混入させない</summary>
    private static void WritePrivateFile(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
