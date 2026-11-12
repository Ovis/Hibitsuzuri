using Hibitsuzuri.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Hibitsuzuri.Tests;

/// <summary>試験ごとにDB・鍵と配置設定を分離した実アプリのホスト</summary>
public sealed class TestApplication : WebApplicationFactory<Program>
{
    /// <summary>本番の保存先と共有しない一時領域</summary>
    public string DataPath
    {
        get;
    }

    /// <summary>起動前に適用する公開先とプロキシの設定</summary>
    public Dictionary<string, string?> Settings
    {
        get;
    } = new()
    {
        ["Site:PublicOrigin"] = "https://diary.example.test",
        ["Authentication:SetupCode"] = "test-only-initial-setup-code",
        ["ReverseProxy:KnownProxies:0"] = "10.20.0.2"
    };

    /// <summary>再起動試験で同じ永続領域を次のホストへ引き継ぐ</summary>
    public bool PreserveData
    {
        get; set;
    }

    /// <summary>有効期限の境界試験に使う時計</summary>
    public TestClock Clock { get; } = new();

    /// <summary>新規または再起動試験用の永続領域を選ぶ</summary>
    public TestApplication(string? dataPath = null)
    {
        DataPath = dataPath ?? Path.Combine(Path.GetTempPath(), "Hibitsuzuri.Tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>Programが構成を読む前に試験用の公開先を渡す</summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(Settings));
        return base.CreateHost(builder);
    }

    /// <summary>本番モードで起動し、試験アセンブリ内の検証用エンドポイントだけ追加する</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Directory"] = DataPath
        }));
        builder.ConfigureServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(ProbeController).Assembly);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    /// <summary>自動転送しないHTTPSクライアントを作成する</summary>
    public HttpClient CreateBrowser()
    {
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://diary.example.test"),
            AllowAutoRedirect = false
        });
    }

    /// <summary>ホストを停止してから、試験が所有する一時領域だけを削除する</summary>
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Hibitsuzuri.Tests")) + Path.DirectorySeparatorChar;
        if (!PreserveData && Directory.Exists(DataPath)
            && Path.GetFullPath(DataPath).StartsWith(testRoot, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(DataPath, recursive: true);
        }
    }
}
