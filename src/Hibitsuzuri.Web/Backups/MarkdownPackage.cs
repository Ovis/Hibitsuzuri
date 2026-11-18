namespace Hibitsuzuri.Web.Backups;

/// <summary>本文を編集して戻せるMarkdown ZIPの構成情報。本文のチェックサムは持たない</summary>
/// <param name="Format">任意のZIPと区別する識別名</param>
/// <param name="Version">取り込み規則の版</param>
/// <param name="IncludesImages">画像実体を含む出力を選んだか</param>
/// <param name="Books">フォルダーと日記帳の対応</param>
/// <param name="Images">同梱した画像の検証情報</param>
public sealed record MarkdownPackage(string Format, int Version, bool IncludesImages, MarkdownBook[] Books, List<BackupImage> Images);

/// <summary>ファイル名として加工した名前と、元の日記帳名を対応付ける</summary>
/// <param name="Id">ZIP内で日記帳を識別するキー</param>
/// <param name="Name">取り込み先の新規作成に使う表示名</param>
/// <param name="Folder">Markdownを格納する直下フォルダー</param>
public sealed record MarkdownBook(string Id, string Name, string Folder);
