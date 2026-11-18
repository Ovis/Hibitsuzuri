using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Diary;

namespace Hibitsuzuri.Web.Backups;

/// <summary>編集可能なMarkdown ZIPを検証し、既存の復元検証が扱う内部ZIPへ変換する</summary>
public sealed class MarkdownImport(DiaryMarkdown markdown, TimeProvider clock)
{
    /// <summary>元ZIPの内容をOS上へ展開せず、変換完了後にだけ一時ZIPを置き換える</summary>
    public async Task NormalizeAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > BackupArchive.CompressedLimit)
        {
            throw new InvalidDataException("ZIPは圧縮後2GBまでです。");
        }

        var converted = path + ".normalized";
        try
        {
            using (var source = ZipFile.OpenRead(path))
            {
                if (source.Entries.Count > BackupArchive.RecordLimit
                    || source.Entries.Select(x => x.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != source.Entries.Count)
                {
                    throw new InvalidDataException("ZIPの項目数または重複が不正です。");
                }

                var metadata = source.GetEntry("markdown.json");
                if (metadata is null)
                {
                    // 内部変換形式をアップロードできるようにすると、Markdown用の検証を迂回できてしまう。
                    var manifest = source.GetEntry("manifest.json") ?? throw new InvalidDataException("構成情報がありません。");
                    var saved = JsonSerializer.Deserialize<BackupManifest>(await ReadAsync(manifest, 16000000, token), BackupArchive.Json);
                    if (saved?.Format != "hibitsuzuri-backup")
                    {
                        throw new InvalidDataException("対応していないZIPです。");
                    }

                    return;
                }

                var package = JsonSerializer.Deserialize<MarkdownPackage>(await ReadAsync(metadata, 16000000, token), BackupArchive.Json)
                    ?? throw new InvalidDataException("Markdownの構成情報がありません。");
                ValidatePackage(package);
                using var output = new BackupArchive(converted, true);
                await ConvertAsync(source, output, package, token);
                await output.FinishAsync(clock, token, package.IncludesImages, markdown: true);
            }

            if (new FileInfo(converted).Length > BackupArchive.CompressedLimit)
            {
                throw new InvalidDataException("取り込みデータが圧縮後2GBを超えています。");
            }

            File.Move(converted, path, overwrite: true);
        }
        finally
        {
            File.Delete(converted);
        }
    }

    /// <summary>元の日記帳名と安全な直下フォルダーを一対一に対応させる</summary>
    private static void ValidatePackage(MarkdownPackage package)
    {
        if (package.Format != "hibitsuzuri-markdown" || package.Version != 1 || package.Books is null || package.Images is null
            || package.Books.Length > BackupArchive.RecordLimit || package.Images.Count > BackupArchive.RecordLimit
            || package.Books.Any(x => x is null || !BackupInspection.ValidId(x.Id) || !AuthRules.ValidText(x.Name, 1, 100)
                || x.Name != x.Name.Trim() || string.IsNullOrEmpty(x.Folder) || x.Folder.Length > 200
                || !x.Folder.EndsWith("-" + x.Id, StringComparison.Ordinal)
                || x.Folder.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-') && c is not (>= '\u3040' and <= '\u9fff')))
            || package.Books.Select(x => x.Id).Distinct().Count() != package.Books.Length
            || package.Books.Select(x => x.Name).Distinct().Count() != package.Books.Length
            || package.Books.Select(x => x.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() != package.Books.Length
            || package.Images.Any(x => x is null || !BackupInspection.ValidId(x.Id))
            || package.Images.Select(x => x.Id).Distinct().Count() != package.Images.Count
            || (!package.IncludesImages && package.Images.Count != 0))
        {
            throw new InvalidDataException("Markdownの構成情報が不正です。");
        }
    }

    /// <summary>本文は一件ずつ変換し、画像は参照されたものだけを引き継ぐ</summary>
    private async Task ConvertAsync(ZipArchive source, BackupArchive output, MarkdownPackage package, CancellationToken token)
    {
        long expanded = 0;
        var folders = package.Books.ToDictionary(x => x.Folder, StringComparer.Ordinal);
        var images = package.Images.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var assets = new HashSet<string>(StringComparer.Ordinal);
        var needed = new Dictionary<string, (string Book, DateOnly Date)>();
        var position = 0;
        foreach (var book in package.Books)
        {
            await output.AddRecordAsync("books", new BackupNotebook(book.Id, book.Name, "", position++, false, null), token);
        }

        foreach (var item in source.Entries)
        {
            var parts = item.FullName.Split('/');
            var isDirectory = parts.Length == 2 && parts[1] == "" && (parts[0] == "assets" || folders.ContainsKey(parts[0]));
            var isAsset = parts.Length == 2 && parts[0] == "assets" && images.ContainsKey(parts[1]);
            var isDiary = parts.Length == 2 && folders.ContainsKey(parts[0]) && parts[1].EndsWith(".md", StringComparison.Ordinal);
            if (item.Length < 0 || (expanded += item.Length) > BackupArchive.ExpandedLimit
                || (item.ExternalAttributes >> 16 & 0xf000) == 0xa000
                || (item.FullName != "markdown.json" && !isDirectory && !isAsset && !isDiary))
            {
                throw new InvalidDataException("Markdown ZIPのパスまたは展開後容量が不正です。");
            }

            if (item.FullName == "markdown.json")
            {
                continue;
            }

            if (isDirectory)
            {
                if (item.Length != 0)
                {
                    throw new InvalidDataException("フォルダー項目が不正です。");
                }

                continue;
            }

            if (isAsset)
            {
                if (item.Length != images[parts[1]].Length || item.Length > 160000000)
                {
                    throw new InvalidDataException("画像の容量が不正です。");
                }

                assets.Add(parts[1]);
                continue;
            }

            var bytes = await ReadAsync(item, BackupArchive.JsonLimit, token);
            string text;
            try
            {
                text = DiaryMarkdown.NormalizeLineEndings(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
            }
            catch (DecoderFallbackException error)
            {
                throw new InvalidDataException("MarkdownはUTF-8で保存してください。", error);
            }

            var entry = ParseEntry(text, parts[1], folders[parts[0]].Id);
            foreach (var image in markdown.ImageIds(entry.Content))
            {
                needed.TryAdd(image, (entry.NotebookId, entry.EntryDate));
            }

            await output.AddRecordAsync("entries", entry, token);
        }

        if (!assets.SetEquals(images.Keys))
        {
            throw new InvalidDataException("画像ファイルが不足しています。");
        }

        foreach (var image in package.Images)
        {
            var item = source.GetEntry("assets/" + image.Id) ?? throw new InvalidDataException("画像がありません。");
            await using var input = item.Open();
            if (needed.TryGetValue(image.Id, out var reference))
            {
                await output.AddRecordAsync("images", image with
                {
                    NotebookId = reference.Book,
                    EntryDate = reference.Date
                }, token);
                await output.AddAsync("assets/" + image.Id, input, token);
            }
            else
            {
                // 本文から外した画像も読み切り、宣言されたサイズだけで検証を終えない。
                var length = await BackupArchive.CopyAsync(input, Stream.Null, item.Length, token);
                if (length != item.Length)
                {
                    throw new InvalidDataException("画像が破損しています。");
                }
            }
        }
    }

    /// <summary>書き出し時のヘッダーとファイル名が指す日付を照合し、本文を取り出す</summary>
    private BackupEntry ParseEntry(string text, string name, string book)
    {
        var end = text.IndexOf("\n---\n", StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal) || end < 0
            || !DateOnly.TryParseExact(name[..^3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new InvalidDataException("日付付きファイル名またはMarkdownのヘッダーが不正です。");
        }

        var header = text[4..end].Split('\n');
        if (header.Length != 3 || header[0] != "date: " + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            || !header[1].StartsWith("created: ", StringComparison.Ordinal) || !header[2].StartsWith("updated: ", StringComparison.Ordinal)
            || !DateTimeOffset.TryParseExact(header[1][9..], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var created)
            || !DateTimeOffset.TryParseExact(header[2][9..], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated))
        {
            throw new InvalidDataException("書き出したヘッダーを保持し、ファイル名と日付を一致させてください。");
        }

        var start = end + 5;
        if (start < text.Length && text[start] == '\n')
        {
            start++;
        }

        var content = markdown.ImportImages(text[start..]);
        if (!AuthRules.ValidText(content, 1, 100000) || string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidDataException("Markdownの本文は空白だけにせず、100,000文字以内にしてください。");
        }

        return new BackupEntry(Guid.NewGuid().ToString("N"), book, date, content, created.ToUnixTimeMilliseconds(), updated.ToUnixTimeMilliseconds());
    }

    /// <summary>宣言値と実読み取り量を制限し、メタデータと本文のメモリ使用量を抑える</summary>
    private static async Task<byte[]> ReadAsync(ZipArchiveEntry entry, long limit, CancellationToken token)
    {
        if (entry.Length > limit || (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000)
        {
            throw new InvalidDataException("ZIPの項目が大きすぎるか、リンクになっています。");
        }

        await using var input = entry.Open();
        using var bytes = new MemoryStream();
        var actual = await BackupArchive.CopyAsync(input, bytes, limit, token);
        if (actual != entry.Length)
        {
            throw new InvalidDataException("ZIPの項目が破損しています。");
        }

        return bytes.ToArray();
    }
}
