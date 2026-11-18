using System.Net;
using System.Text.RegularExpressions;

namespace Hibitsuzuri.Tests;

/// <summary>実際のCSRFとCookieを使用する認証画面の試験操作</summary>
public static partial class AuthClient
{
    public const string Password = "test-password-for-auth";

    /// <summary>現在の認証状態で取得したCSRFトークンをフォームへ添付する</summary>
    public static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string path, Dictionary<string, string>? fields = null)
    {
        fields ??= [];
        fields["__RequestVerificationToken"] = await browser.GetStringAsync("/fixture/token");

        return await browser.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    /// <summary>登録画面からコードの保存確認まで進め、復旧試験用のコードを返す</summary>
    public static async Task<string[]> RegisterAsync(TestApplication app, HttpClient browser, string loginId = "owner", bool setup = true)
    {
        using var response = await PostAsync(browser, "/auth/register", new()
        {
            ["LoginId"] = loginId,
            ["Password"] = Password,
            ["PasswordConfirmation"] = Password,
            ["SetupCode"] = setup ? app.ReadSetupCode() : ""
        });
        var html = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), html);

        var codes = CodePattern().Matches(html).Select(x => x.Value).Distinct().ToArray();
        Assert.That(codes, Has.Length.EqualTo(10), html);

        using var confirmation = await PostAsync(browser, "/auth/codes/confirm", new()
        {
            ["saved"] = "true"
        });

        Assert.That(confirmation.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        return codes;
    }

    /// <summary>フォームを使い、固定期限でログインする</summary>
    public static Task<HttpResponseMessage> LoginAsync(HttpClient browser, string loginId = "owner", bool remember = false, string password = Password)
    {
        return PostAsync(browser, "/auth/login", new()
        {
            ["LoginId"] = loginId,
            ["Password"] = password,
            ["RememberMe"] = remember.ToString()
        });
    }

    /// <summary>復旧コードの表示から128bitのコードだけを抽出する</summary>
    [GeneratedRegex(@"\b[A-F0-9]{32}\b")]
    public static partial Regex CodePattern();
}

/// <summary>待機せずに有効期限を進める試験時計</summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    /// <summary>試験上の現在時刻</summary>
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>時間境界まで進める</summary>
    public void Advance(TimeSpan duration)
    {
        _now += duration;
    }
}
