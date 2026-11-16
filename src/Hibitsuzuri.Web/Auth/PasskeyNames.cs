using System.Formats.Cbor;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;

namespace Hibitsuzuri.Web.Auth;

/// <summary>登録済み応答のAAGUIDから、利用者が変更できるPasskeyの初期名を付ける</summary>
public static class PasskeyNames
{
    private static readonly IReadOnlyDictionary<Guid, string> Providers = LoadProviders();

    /// <summary>Identityの登録検証に成功した応答だけを受け取り、提供元名または登録日付きの名前を返す</summary>
    /// <remarks>AAGUIDは作成元の表示用情報であり、認証の許可や信頼性の判定には使用しない。</remarks>
    public static string Create(UserPasskeyInfo passkey)
    {
        var aaguid = ReadAaguid(passkey.AttestationObject);
        if (aaguid != Guid.Empty && Providers.TryGetValue(aaguid, out var provider))
        {
            return provider;
        }

        return "Passkey（" + passkey.CreatedAt.ToLocalTime().ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) + "登録）";
    }

    /// <summary>検証済みのCBOR応答から認証器データを読み、ネットワーク順のAAGUIDを取り出す</summary>
    private static Guid ReadAaguid(byte[] attestationObject)
    {
        var reader = new CborReader(attestationObject);
        reader.ReadStartMap();
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            if (reader.ReadTextString() == "authData")
            {
                var data = reader.ReadByteString();
                // WebAuthnの固定ヘッダーはRPハッシュ32・フラグ1・カウンター4バイト。
                // 検証済みの登録応答にはATフラグと16バイトのAAGUIDが必ず含まれる。
                if (data.Length < 53 || (data[32] & 0x40) == 0)
                {
                    throw new InvalidDataException("検証済みの登録応答に認証器の情報がありません。");
                }

                return new Guid(data.AsSpan(37, 16), bigEndian: true);
            }

            reader.SkipValue();
        }

        throw new InvalidDataException("検証済みの登録応答に認証器データがありません。");
    }

    /// <summary>同梱した名前対応表を読む。外部サービスの障害や一覧の廃止を登録処理へ持ち込まない</summary>
    private static IReadOnlyDictionary<Guid, string> LoadProviders()
    {
        using var stream = typeof(PasskeyNames).Assembly.GetManifestResourceStream("Hibitsuzuri.Web.Auth.passkey-providers.json")
            ?? throw new InvalidOperationException("Passkeyの名前対応表がありません。");
        using var document = JsonDocument.Parse(stream);
        var providers = new Dictionary<Guid, string>();
        foreach (var entry in document.RootElement.GetProperty("providers").EnumerateObject())
        {
            var name = entry.Value.GetString();
            if (!Guid.TryParseExact(entry.Name, "D", out var id) || id == Guid.Empty || string.IsNullOrWhiteSpace(name)
                || !AuthRules.ValidText(name, 1, 100))
            {
                throw new InvalidDataException("Passkeyの名前対応表に不正な項目があります。");
            }

            providers.Add(id, name);
        }

        return providers;
    }
}
