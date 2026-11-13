namespace Hibitsuzuri.Web.Diary;

/// <summary>閲覧と編集に共通する、所有者確認済みの表示材料</summary>
public sealed class DiaryPage
{
    /// <summary>表示中の日記帳</summary>
    public required Notebook Book
    {
        get; init;
    }

    /// <summary>切替先の本人の日記帳</summary>
    public required IReadOnlyList<Notebook> Books
    {
        get; init;
    }

    /// <summary>表示対象の暦日</summary>
    public DateOnly Date
    {
        get; init;
    }

    /// <summary>サーバーのタイムゾーンによる今日</summary>
    public DateOnly Today
    {
        get; init;
    }

    /// <summary>未作成ではnull。論理削除済みも区別して扱う</summary>
    public DiaryEntry? Entry
    {
        get; init;
    }

    /// <summary>新しい順に表示する復元候補</summary>
    public IReadOnlyList<DiaryRevision> Revisions { get; init; } = [];

    /// <summary>共通規則でレンダリングした本文</summary>
    public string Html { get; init; } = "";

    /// <summary>URL用の日付表記</summary>
    public string Day => Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>閲覧先。保存とキャンセルの戻り先にも使う</summary>
    public string Path => $"/diary/{Book.Id}/{Day}";
}

/// <summary>選択日記帳だけのゴミ箱と一括削除の確認材料</summary>
public sealed class DiaryTrash
{
    /// <summary>ゴミ箱の所属先</summary>
    public required Notebook Book
    {
        get; init;
    }

    /// <summary>削除済みの日記</summary>
    public required IReadOnlyList<DiaryEntry> Entries
    {
        get; init;
    }

    /// <summary>確認後に対象が変化していないことを検証する値</summary>
    public required string Snapshot
    {
        get; init;
    }
}
