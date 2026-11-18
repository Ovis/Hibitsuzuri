using System.Security.Cryptography;
using System.Text.Json;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace Hibitsuzuri.Web.Notifications;

/// <summary>永続領域に一度だけ生成するVAPID鍵。破損時に黙って鍵を変えない</summary>
public sealed class PushKeys(DataDirectory data, IDataProtectionProvider protection)
{
    private readonly Lazy<KeyPair> _keys = new(() => Load(data, protection));

    /// <summary>ブラウザへ公開するP-256公開鍵</summary>
    public string Public => _keys.Value.Public;

    /// <summary>配信サービスへの署名に使う秘密鍵</summary>
    public string Private => _keys.Value.Private;

    /// <summary>ホスト排他ロック下でのみ新規作成し、既存の秘密鍵は復号して読む</summary>
    private static KeyPair Load(DataDirectory data, IDataProtectionProvider protection)
    {
        var path = Path.Combine(data.RootPath, "vapid.json");
        var protector = protection.CreateProtector("Hibitsuzuri.Vapid.v1");
        if (File.Exists(path))
        {
            var stored = JsonSerializer.Deserialize<KeyPair>(File.ReadAllText(path)) ?? throw new InvalidDataException("VAPID鍵が壊れています。");
            return new KeyPair(stored.Public, protector.Unprotect(stored.Private));
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(true);
        var x = parameters.Q.X ?? throw new CryptographicException();
        var y = parameters.Q.Y ?? throw new CryptographicException();
        var secret = parameters.D ?? throw new CryptographicException();
        var pair = new KeyPair(WebEncoders.Base64UrlEncode([4, .. x, .. y]), WebEncoders.Base64UrlEncode(secret));
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new KeyPair(pair.Public, protector.Protect(pair.Private))));
        File.Move(temporary, path);
        return pair;
    }

    /// <summary>公開鍵と、メモリ上では平文・ファイル上では保護済みの秘密鍵</summary>
    /// <param name="Public">公開鍵</param>
    /// <param name="Private">秘密鍵</param>
    private sealed record KeyPair(string Public, string Private);
}
