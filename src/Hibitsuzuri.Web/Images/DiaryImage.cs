namespace Hibitsuzuri.Web.Images;

/// <summary>非公開領域に保存した検証済み画像</summary>
public sealed class DiaryImage
{
    /// <summary>ファイル名にも使うサーバー発行ID</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>配信を許可する本人</summary>
    public string UserId { get; set; } = "";

    /// <summary>アップロード時の日記帳</summary>
    public string NotebookId { get; set; } = "";

    /// <summary>添付一覧を表示する元の日付</summary>
    public DateOnly EntryDate
    {
        get; set;
    }

    /// <summary>表示だけに使い、パスへは使用しない元の名前</summary>
    public string Name { get; set; } = "";

    /// <summary>内容を検証して決めた配信形式</summary>
    public string MediaType { get; set; } = "";

    /// <summary>保存後のファイルサイズ</summary>
    public long Length
    {
        get; set;
    }

    /// <summary>表示方向を適用した横幅</summary>
    public int Width
    {
        get; set;
    }

    /// <summary>表示方向を適用した高さ</summary>
    public int Height
    {
        get; set;
    }

    /// <summary>取り込み時刻（UTCミリ秒）</summary>
    public long CreatedAt
    {
        get; set;
    }
}

/// <summary>本文をサーバーへ保存せず、下書きごとに画像の期限だけを保持する</summary>
public sealed class ImageDraftLease
{
    /// <summary>保持対象の画像</summary>
    public string ImageId { get; set; } = "";

    /// <summary>ブラウザのタブごとに分離した下書き</summary>
    public string DraftId { get; set; } = "";

    /// <summary>最後の下書き保存から30日後（UTCミリ秒）</summary>
    public long ExpiresAt
    {
        get; set;
    }
}

/// <summary>日記の現行版または過去5版が使用する画像の集合</summary>
public sealed class DiaryImageReference
{
    /// <summary>保持対象の画像</summary>
    public string ImageId { get; set; } = "";

    /// <summary>ゴミ箱でも保持し、完全削除で参照を解放する日記</summary>
    public string EntryId { get; set; } = "";
}

/// <summary>子プロセスで検証・変換した画像の配信情報</summary>
/// <param name="MediaType">検証後のMIME形式</param>
/// <param name="Width">表示方向の横幅</param>
/// <param name="Height">表示方向の高さ</param>
public sealed record ImageResult(string MediaType, int Width, int Height);
