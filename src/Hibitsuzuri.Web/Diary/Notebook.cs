namespace Hibitsuzuri.Web.Diary;

/// <summary>所有者ごとに日記と新規作成時のひな形をまとめる</summary>
public sealed class Notebook
{
    /// <summary>普段の選択肢から外す。明示的に開けば編集できる</summary>
    public bool Archived
    {
        get; set;
    }

    /// <summary>配下の記録を保持したまま通常の閲覧・通知から外した時刻</summary>
    public long? DeletedAt
    {
        get; set;
    }

    /// <summary>停止から再開した時点より過去の通知を送らない境界</summary>
    public long NotificationAfter
    {
        get; set;
    }

    /// <summary>停止開始日。停止中はタスクを閲覧のみにする</summary>
    public DateOnly? TasksPausedOn
    {
        get; set;
    }

    /// <summary>日記帳の不変の識別子</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>閲覧・編集を許可する所有者</summary>
    public string UserId { get; set; } = "";

    /// <summary>同じ所有者の中で重複しない表示名</summary>
    public string Name { get; set; } = "";

    /// <summary>新規日記だけに適用するMarkdown原文</summary>
    public string Template { get; set; } = "";

    /// <summary>端末間で共有する手動の表示順</summary>
    public int Position
    {
        get; set;
    }

    /// <summary>設定の同時編集を検出する版</summary>
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
}
