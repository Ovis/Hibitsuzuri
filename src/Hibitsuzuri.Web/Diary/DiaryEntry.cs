namespace Hibitsuzuri.Web.Diary;

/// <summary>日記帳内の日付に対応する現在の本文。ゴミ箱でも日付を占有する</summary>
public sealed class DiaryEntry
{
    /// <summary>履歴の所属先となる不変ID</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>所有者を決める日記帳</summary>
    public string NotebookId { get; set; } = "";

    /// <summary>作成後に変更しない暦日</summary>
    public DateOnly EntryDate
    {
        get; set;
    }

    /// <summary>保存済みのMarkdown原文</summary>
    public string Content { get; set; } = "";

    /// <summary>最初の保存時刻（UTCミリ秒）</summary>
    public long CreatedAt
    {
        get; set;
    }

    /// <summary>本文の最終更新時刻（UTCミリ秒）</summary>
    public long UpdatedAt
    {
        get; set;
    }

    /// <summary>ゴミ箱へ移した時刻。自動で消去しない</summary>
    public long? DeletedAt
    {
        get; set;
    }

    /// <summary>本文変更・削除・復元で更新する排他トークン</summary>
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
}

/// <summary>現在の内容とは別に保持する過去の本文</summary>
public sealed class DiaryRevision
{
    /// <summary>保存当時の排他トークン</summary>
    public string Id { get; set; } = "";

    /// <summary>復元候補を持つ日記</summary>
    public string EntryId { get; set; } = "";

    /// <summary>当時のMarkdown原文</summary>
    public string Content { get; set; } = "";

    /// <summary>当時の本文更新時刻（UTCミリ秒）</summary>
    public long UpdatedAt
    {
        get; set;
    }

    /// <summary>時計の精度に依存せず古い版から間引くための順序</summary>
    public long Sequence
    {
        get; set;
    }
}

/// <summary>応答を失った保存要求を二重適用しないための確定記録</summary>
public sealed class DiarySaveOperation
{
    /// <summary>ブラウザが保存意図ごとに発行するID</summary>
    public string Id { get; set; } = "";

    /// <summary>日記帳削除・ユーザー削除に従って消去する所属先</summary>
    public string NotebookId { get; set; } = "";

    /// <summary>日付・本文・期待版を含む要求のハッシュ</summary>
    public string RequestHash { get; set; } = "";

    /// <summary>確定時に返した版。日記が完全削除されても再作成しない</summary>
    public string ResultVersion { get; set; } = "";
}
