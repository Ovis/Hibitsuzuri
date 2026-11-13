using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Hibitsuzuri.Tests;

/// <summary>実ECDSA署名を生成する試験認証器。実機受け入れの代替にはしない</summary>
public sealed class TestAuthenticator : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _credentialId = RandomNumberGenerator.GetBytes(32);
    private byte[] _userHandle = [];
    private uint _count;

    /// <summary>WebAuthnの資格情報ID</summary>
    public string Id => WebEncoders.Base64UrlEncode(_credentialId);

    /// <summary>none形式の登録応答を生成し、標準Identityの検証に渡す</summary>
    public string Create(JsonElement options, string origin = "https://diary.example.test", bool verified = true)
    {
        _userHandle = WebEncoders.Base64UrlDecode(RequiredString(options.GetProperty("user"), "id"));
        var clientData = ClientData("webauthn.create", RequiredString(options, "challenge"), origin);

        var parameters = _key.ExportParameters(false);
        var x = parameters.Q.X ?? throw new InvalidOperationException("公開鍵Xがありません。");
        var y = parameters.Q.Y ?? throw new InvalidOperationException("公開鍵Yがありません。");
        byte[] cose = [0xa5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20, .. x, 0x22, 0x58, 0x20, .. y];
        byte[] authData = [.. AuthenticatorData((byte)(verified ? 0x45 : 0x41), 0), .. new byte[16], 0, 32, .. _credentialId, .. cose];

        using var stream = new MemoryStream();
        stream.WriteByte(0xa3);
        WriteText(stream, "fmt");
        WriteText(stream, "none");
        WriteText(stream, "attStmt");
        stream.WriteByte(0xa0);
        WriteText(stream, "authData");
        stream.WriteByte(0x58);
        stream.WriteByte((byte)authData.Length);
        stream.Write(authData);

        return JsonSerializer.Serialize(new
        {
            id = Id,
            rawId = Id,
            type = "public-key",
            authenticatorAttachment = "platform",
            clientExtensionResults = new
            {
            },
            response = new
            {
                clientDataJSON = WebEncoders.Base64UrlEncode(clientData),
                attestationObject = WebEncoders.Base64UrlEncode(stream.ToArray()),
                transports = new[] { "internal" }
            }
        });
    }

    /// <summary>チャレンジ・RPハッシュ・カウンターを含む署名を生成する</summary>
    public string Get(JsonElement options, string origin = "https://diary.example.test", bool badSignature = false)
    {
        var data = ClientData("webauthn.get", RequiredString(options, "challenge"), origin);
        var auth = AuthenticatorData(0x05, ++_count);
        var signature = _key.SignData([.. auth, .. SHA256.HashData(data)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        if (badSignature)
        {
            signature[^1] ^= 1;
        }

        return JsonSerializer.Serialize(new
        {
            id = Id,
            rawId = Id,
            type = "public-key",
            authenticatorAttachment = "platform",
            clientExtensionResults = new
            {
            },
            response = new
            {
                clientDataJSON = WebEncoders.Base64UrlEncode(data),
                authenticatorData = WebEncoders.Base64UrlEncode(auth),
                signature = WebEncoders.Base64UrlEncode(signature),
                userHandle = WebEncoders.Base64UrlEncode(_userHandle)
            }
        });
    }

    /// <summary>必須のブラウザ向けフィールドを取得する</summary>
    private static string RequiredString(JsonElement element, string name) => element.GetProperty(name).GetString() ?? throw new InvalidOperationException(name);

    /// <summary>クライアントデータを標準形式で符号化する</summary>
    private static byte[] ClientData(string type, string challenge, string origin) => JsonSerializer.SerializeToUtf8Bytes(new { type, challenge, origin, crossOrigin = false });

    /// <summary>認証器データのRPハッシュ・フラグ・カウンターを構成する</summary>
    private static byte[] AuthenticatorData(byte flags, uint count)
    {
        var data = new byte[37];
        SHA256.HashData(Encoding.UTF8.GetBytes("diary.example.test")).CopyTo(data, 0);
        data[32] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(33), count);

        return data;
    }

    /// <summary>試験で使用する短いCBOR文字列を出力する</summary>
    private static void WriteText(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.WriteByte((byte)(0x60 + bytes.Length));
        stream.Write(bytes);
    }

    /// <summary>試験用秘密鍵を破棄する</summary>
    public void Dispose() => _key.Dispose();
}
