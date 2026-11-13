using System.Net;
using System.Text.Json;

namespace Hibitsuzuri.Tests;

/// <summary>日記機能を実際の認証・CSRF付きHTTP操作で検証する補助</summary>
public static class DiaryClient
{
    /// <summary>日記帳作成後の編集URLから識別子を得る</summary>
    public static async Task<string> CreateBookAsync(HttpClient client, string name = "日記")
    {
        using var response = await AuthClient.PostAsync(client, "/books", new()
        {
            ["name"] = name
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect), await response.Content.ReadAsStringAsync());

        return response.Headers.Location?.ToString().Split('/').Last() ?? throw new InvalidOperationException();
    }

    /// <summary>操作IDを指定して、競合・再送・正常保存を同じ入口から試す</summary>
    public static Task<HttpResponseMessage> SaveAsync(HttpClient client, string book, string content,
        string version = "", string? operation = null, string date = "2020-05-10")
    {
        return AuthClient.PostAsync(client, $"/api/diary/{book}/{date}/save", new()
        {
            ["content"] = content,
            ["version"] = version,
            ["operationId"] = operation ?? Guid.NewGuid().ToString()
        });
    }

    /// <summary>正常保存を確認し、次の編集で必要な版を返す</summary>
    public static async Task<string> SaveVersionAsync(HttpClient client, string book, string content, string version = "", string date = "2020-05-10")
    {
        using var response = await SaveAsync(client, book, content, version, date: date);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("version").GetString() ?? throw new InvalidOperationException();
    }
}
