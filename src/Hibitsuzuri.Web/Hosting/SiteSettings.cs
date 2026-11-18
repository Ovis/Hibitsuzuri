using System.Net;

namespace Hibitsuzuri.Web.Hosting;

/// <summary>公開URLと、転送ヘッダーを受け入れる直近のプロキシを定義する</summary>
public sealed class SiteSettings
{
    /// <summary>ブラウザから接続するルートURL</summary>
    public Uri PublicOrigin
    {
        get;
    }

    /// <summary>転送元として信頼するプロキシのIPアドレス</summary>
    public IReadOnlyList<IPAddress> KnownProxies
    {
        get;
    }

    /// <summary>配置側の接続制限を前提に、転送元IPを限定せずHTTPS情報だけを受け入れる</summary>
    public bool DeploymentManagedProxy
    {
        get;
    }

    /// <summary>配置設定を検証し、公開先と信頼境界を確定する</summary>
    public SiteSettings(IConfiguration configuration, IHostEnvironment environment)
    {
        var value = configuration["Site:PublicOrigin"];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var origin)
            || origin.HostNameType == UriHostNameType.Unknown
            || origin.AbsolutePath != "/"
            || origin.Query.Length != 0
            || origin.Fragment.Length != 0
            || origin.UserInfo.Length != 0)
        {
            throw new InvalidOperationException("Site:PublicOriginには公開先のルートURLを指定してください。");
        }

        var localDevelopment = environment.IsDevelopment()
            && origin.IsLoopback
            && origin.Scheme == Uri.UriSchemeHttp;
        if (origin.Scheme != Uri.UriSchemeHttps && !localDevelopment)
        {
            throw new InvalidOperationException("公開URLにはHTTPSが必要です。HTTPはDevelopmentのループバック接続だけに使用できます。");
        }

        DeploymentManagedProxy = configuration["ReverseProxy:Mode"] switch
        {
            null or "KnownProxies" => false,
            "DeploymentManaged" => true,
            _ => throw new InvalidOperationException("ReverseProxy:ModeにはKnownProxiesまたはDeploymentManagedを指定してください。")
        };

        var proxies = new List<IPAddress>();
        foreach (var entry in configuration.GetSection("ReverseProxy:KnownProxies").GetChildren())
        {
            if (!IPAddress.TryParse(entry.Value, out var address))
            {
                throw new InvalidOperationException("ReverseProxy:KnownProxiesにはIPアドレスを指定してください。");
            }

            proxies.Add(address);
        }

        PublicOrigin = origin;
        KnownProxies = proxies.AsReadOnly();
    }
}
