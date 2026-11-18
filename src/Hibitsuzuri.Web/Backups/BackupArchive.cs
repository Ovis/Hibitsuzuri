using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Hibitsuzuri.Web.Backups;

/// <summary>バージョン付きZIPの容量・構造・チェックサムを共通で検証する</summary>
public sealed partial class BackupArchive : IDisposable
{
    public const long CompressedLimit = 2000000000;
    public const long ExpandedLimit = 4000000000;
    public const int RecordLimit = 100000;
    public const int JsonLimit = 2000000;
    private readonly ZipArchive _zip;
    private readonly List<BackupFile> _files = [];
    private long _expanded;

    /// <summary>未知の項目・深すぎるJSONを受け入れず、形式変更を版で区別する</summary>
    public static JsonSerializerOptions Json
    {
        get;
    } = new()
    {
        MaxDepth = 16,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>画像メタデータとの過不足検査に使う実体ID</summary>
    public IEnumerable<string> AssetIds => _zip.Entries.Where(x => x.FullName.StartsWith("assets/", StringComparison.Ordinal)).Select(x => x.FullName[7..]);

    /// <summary>公開パスを使わず、呼び出し元が管理する一時ファイルを開く</summary>
    public BackupArchive(string path, bool create = false)
    {
        if (!create && new FileInfo(path).Length > CompressedLimit)
        {
            throw new InvalidDataException("ZIPは圧縮後2GBまでです。");
        }

        _zip = ZipFile.Open(path, create ? ZipArchiveMode.Create : ZipArchiveMode.Read);
    }

    /// <summary>ZIPからOS上のパスへ直接展開せず、既知の名前だけを受け付ける</summary>
    [GeneratedRegex("^(books|entries|revisions|series|segments|tasks|slots|pauses|anniversaries|images)/[0-9a-f]{32}\\.json$|^assets/[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedPath();

    /// <summary>全エントリーを実際に読み、宣言値だけを信用しない</summary>
    public async Task VerifyAsync(CancellationToken cancellationToken)
    {
        if (_zip.Entries.Count > RecordLimit || _zip.Entries.Select(x => x.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _zip.Entries.Count)
        {
            throw new InvalidDataException("ZIPの項目数または重複が不正です。");
        }

        var manifestEntry = _zip.GetEntry("manifest.json") ?? throw new InvalidDataException("バックアップの目録がありません。");
        if (manifestEntry.Length > 16000000)
        {
            throw new InvalidDataException("目録が大きすぎます。");
        }

        await using var manifestBytes = new MemoryStream();
        await using (var input = manifestEntry.Open())
        {
            await CopyAsync(input, manifestBytes, 16000000, cancellationToken);
        }

        var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestBytes.ToArray(), Json)
            ?? throw new InvalidDataException("目録を読み込めません。");
        if (manifest.Format != "hibitsuzuri-backup" || manifest.Version != 1 || manifest.Files is null
            || manifest.Files.Count != _zip.Entries.Count - 1 || manifest.Files.Any(x => x is null)
            || manifest.Files.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != manifest.Files.Count)
        {
            throw new InvalidDataException("対応していない形式・バージョン、または不正な目録です。");
        }

        long total = manifestEntry.Length;
        foreach (var file in manifest.Files)
        {
            if (file.Path is null || !AllowedPath().IsMatch(file.Path) || file.Length < 0)
            {
                throw new InvalidDataException("バックアップのパスが不正です。");
            }

            var entry = _zip.GetEntry(file.Path) ?? throw new InvalidDataException("ファイルが不足しています。");
            var limit = file.Path.StartsWith("assets/", StringComparison.Ordinal) ? 160000000 : JsonLimit;
            if (entry.Length != file.Length || entry.Length > limit || (total += entry.Length) > ExpandedLimit
                || (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000)
            {
                throw new InvalidDataException("展開後4GBまたは各項目の上限を超えています。");
            }

            await using var source = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var actual = await CopyAsync(source, Stream.Null, entry.Length, cancellationToken, hash);
            if (actual != entry.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
            {
                throw new InvalidDataException("バックアップの内容が破損しています。");
            }
        }
    }

    /// <summary>一件ずつ読み、日記本文全体をメモリへ展開しない</summary>
    public IEnumerable<(string Path, T Value)> Read<T>(string group)
    {
        foreach (var entry in _zip.Entries.Where(x => x.FullName.StartsWith(group + "/", StringComparison.Ordinal)))
        {
            using var source = entry.Open();
            using var document = JsonDocument.Parse(source, new JsonDocumentOptions { MaxDepth = Json.MaxDepth });
            var record = document.RootElement;
            // モデルの初期値によるID発行を使うと、検証時と反映時で別の参照先になってしまう。
            if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("Id", out var id)
                || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString()))
            {
                throw new InvalidDataException("識別子のないデータがあります。");
            }

            var value = record.Deserialize<T>(Json) ?? throw new InvalidDataException("空のデータがあります。");
            yield return (entry.FullName, value);
        }
    }

    /// <summary>画像の実体を呼び出し元が決めた安全な保存先へ取り出す</summary>
    public async Task ExtractAsync(string id, string destination, CancellationToken cancellationToken)
    {
        var entry = _zip.GetEntry("assets/" + id) ?? throw new InvalidDataException("画像が不足しています。");
        await using var source = entry.Open();
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await CopyAsync(source, output, 160000000, cancellationToken);
    }

    /// <summary>JSONを独立した項目にして、巨大な本文配列を作らない</summary>
    public async Task AddRecordAsync<T>(string group, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > JsonLimit)
        {
            throw new InvalidDataException("一件のデータが上限を超えています。");
        }

        using var input = new MemoryStream(bytes);
        await AddAsync($"{group}/{Guid.NewGuid():N}.json", input, cancellationToken);
    }

    /// <summary>本文・画像を書き込みながら展開後容量とチェックサムを記録する</summary>
    public async Task AddAsync(string path, Stream source, CancellationToken cancellationToken)
    {
        if (_files.Count >= RecordLimit - 1)
        {
            throw new InvalidDataException("ZIPの項目数が上限を超えています。");
        }

        var entry = _zip.CreateEntry(path, CompressionLevel.Fastest);
        await using var target = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var length = await CopyAsync(source, target, ExpandedLimit - _expanded, cancellationToken, hash);
        _expanded += length;
        _files.Add(new BackupFile(path, length, Convert.ToHexStringLower(hash.GetHashAndReset())));
    }

    /// <summary>目録自身も容量へ含め、最後に形式情報を保存する</summary>
    public async Task FinishAsync(TimeProvider clock, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new BackupManifest("hibitsuzuri-backup", 1, clock.GetUtcNow(), clock.LocalTimeZone.Id, _files), Json);
        if (bytes.Length > 16000000 || _expanded + bytes.Length > ExpandedLimit)
        {
            throw new InvalidDataException("バックアップが展開後4GBを超えています。");
        }

        await using var output = _zip.CreateEntry("manifest.json", CompressionLevel.Fastest).Open();
        await output.WriteAsync(bytes, cancellationToken);
    }

    /// <summary>宣言サイズが偽装されても、実際の読み取り量で停止する</summary>
    public static async Task<long> CopyAsync(Stream input, Stream output, long limit, CancellationToken cancellationToken, IncrementalHash? hash = null)
    {
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += count;
            if (total > limit)
            {
                throw new InvalidDataException("ファイルの容量上限を超えています。");
            }

            hash?.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }

        return total;
    }

    /// <summary>ZIPの中央ディレクトリを確定し、ファイルを閉じる</summary>
    public void Dispose() => _zip.Dispose();
}

/// <summary>形式の識別情報と、ZIP内の全ファイルの目録</summary>
/// <param name="Format">Markdown出力と区別する形式名</param>
/// <param name="Version">互換性を判断する形式の版</param>
/// <param name="CreatedAt">バックアップを作成した日時</param>
/// <param name="TimeZone">暦日の解釈に使ったタイムゾーン</param>
/// <param name="Files">改ざん検知ではなく、破損・欠落検出のための目録</param>
public sealed record BackupManifest(string Format, int Version, DateTimeOffset CreatedAt, string TimeZone, List<BackupFile> Files);

/// <summary>単一ファイルのサイズと破損検出用ハッシュ</summary>
/// <param name="Path">ZIP内の固定形式の相対名</param>
/// <param name="Length">展開後のバイト数</param>
/// <param name="Sha256">小文字のSHA-256</param>
public sealed record BackupFile(string Path, long Length, string Sha256);
