namespace Hibitsuzuri.Web.Diary;

/// <summary>所有者ごとに日記と新規作成時のひな形をまとめる</summary>
public sealed class Notebook
{
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
