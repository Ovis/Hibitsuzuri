namespace Hibitsuzuri.Web.Hosting;

/// <summary>追加のHTTPクライアントツールを含めずにコンテナの起動状態を検査する</summary>
public static class HealthCommand
{
    /// <summary>ローカルの準備完了エンドポイントが成功した場合だけ終了コード0を返す</summary>
    public static async Task<int> RunAsync(IConfiguration configuration)
    {
        var port = configuration["ASPNETCORE_HTTP_PORTS"] ?? "8080";
        var origin = configuration["Site:PublicOrigin"];
        if (!int.TryParse(port, out var number) || number is < 1 or > 65535
            || !Uri.TryCreate(origin, UriKind.Absolute, out var publicOrigin))
        {
            return 1;
        }

        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{number}/health/ready");
        request.Headers.Host = publicOrigin.Authority;

        try
        {
            using var response = await client.SendAsync(request);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            return 1;
        }
    }
}
