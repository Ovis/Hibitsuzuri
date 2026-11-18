using Hibitsuzuri.Web.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Hibitsuzuri.Tests;

/// <summary>誤った配置設定を起動前に拒否する条件を検証する</summary>
public sealed class ConfigurationTests
{
    /// <summary>省略時はプロキシIPの制限を維持し、明示した場合だけ配置側管理へ切り替える</summary>
    [TestCase(null, false)]
    [TestCase("KnownProxies", false)]
    [TestCase("DeploymentManaged", true)]
    public void ProxyModeRequiresExplicitDeploymentManagement(string? mode, bool deploymentManaged)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Site:PublicOrigin"] = "https://diary.example.test",
            ["ReverseProxy:Mode"] = mode
        }).Build();

        var settings = new SiteSettings(configuration, new TestEnvironment());

        Assert.That(settings.DeploymentManagedProxy, Is.EqualTo(deploymentManaged));
    }

    /// <summary>空欄や誤記を別の信頼方式へ読み替えず、起動時に拒否する</summary>
    [TestCase("")]
    [TestCase("0")]
    [TestCase("DeploymentManage")]
    public void InvalidProxyModeIsRejected(string mode)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Site:PublicOrigin"] = "https://diary.example.test",
            ["ReverseProxy:Mode"] = mode
        }).Build();

        Assert.Throws<InvalidOperationException>(() => new SiteSettings(configuration, new TestEnvironment()));
    }

    /// <summary>本番の平文URL、認証情報・パス・クエリを含むURLを拒否する</summary>
    [TestCase("")]
    [TestCase("http://diary.example.test")]
    [TestCase("https://diary.example.test/nested")]
    [TestCase("https://user:password@diary.example.test")]
    [TestCase("https://diary.example.test/?token=value")]
    [TestCase("https://diary.example.test/#fragment")]
    public void InvalidProductionOriginIsRejected(string origin)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Site:PublicOrigin"] = origin
        }).Build();

        Assert.Throws<InvalidOperationException>(() => new SiteSettings(configuration, new TestEnvironment()));
    }

    /// <summary>HTTPによる開発はループバックに限定する</summary>
    [TestCase("http://localhost:5180", true)]
    [TestCase("http://127.0.0.1:5180", true)]
    [TestCase("http://192.168.1.10:5180", false)]
    public void DevelopmentHttpMustBeLoopback(string origin, bool accepted)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Site:PublicOrigin"] = origin
        }).Build();
        var environment = new TestEnvironment { EnvironmentName = Environments.Development };

        if (accepted)
        {
            Assert.DoesNotThrow(() => new SiteSettings(configuration, environment));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => new SiteSettings(configuration, environment));
        }
    }

    /// <summary>構成検証に必要なホスト環境</summary>
    private sealed class TestEnvironment : IHostEnvironment
    {
        /// <summary>本番設定を検証する既定の環境名</summary>
        public string EnvironmentName { get; set; } = Environments.Production;

        /// <summary>検証対象のアプリ名</summary>
        public string ApplicationName { get; set; } = "Hibitsuzuri";

        /// <summary>構成検証で使用する基準ディレクトリ</summary>
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        /// <summary>ファイルを読み出さない環境プロバイダー</summary>
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
