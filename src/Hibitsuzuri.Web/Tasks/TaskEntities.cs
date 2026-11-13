namespace Hibitsuzuri.Web.Tasks;

/// <summary>通常タスクと、繰り返しの各回に対する利用者の操作記録</summary>
public sealed class TaskRecord
{
    /// <summary>通常タスクはGUID、繰り返しは適用区間と元の予定日で識別する</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>所有者を決める日記帳</summary>
    public string NotebookId { get; set; } = "";

    /// <summary>繰り返しのまとまり。通常タスクではnull</summary>
    public string? SeriesId
    {
        get; set;
    }

    /// <summary>操作した回が属する元のルール区間</summary>
    public string? SegmentId
    {
        get; set;
    }

    /// <summary>休日調整や個別変更前の予定日</summary>
    public DateOnly? ScheduledDate
    {
        get; set;
    }

    /// <summary>その回で表示する名前</summary>
    public string Title { get; set; } = "";

    /// <summary>HTMLとして扱わない任意のメモ</summary>
    public string Memo { get; set; } = "";

    /// <summary>英数字の幅と大小文字を揃えた検索用本文</summary>
    public string SearchText { get; set; } = "";

    /// <summary>個別変更後の期限。nullは期限なし</summary>
    public DateOnly? DueDate
    {
        get; set;
    }

    /// <summary>現在の完了時刻。未完了へ戻すと解除する</summary>
    public long? CompletedAt
    {
        get; set;
    }

    /// <summary>今回だけの削除を保持し、生成回数からは差し引かない</summary>
    public bool Skipped
    {
        get; set;
    }

    /// <summary>空欄への変更を含む個別編集を保持する</summary>
    public bool Edited
    {
        get; set;
    }

    /// <summary>更新の競合を検出する版</summary>
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
}

/// <summary>区間を分割しても一覧で一つにまとめる繰り返し</summary>
public sealed class TaskSeries
{
    /// <summary>系列の不変ID</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>系列を所有する日記帳</summary>
    public string NotebookId { get; set; } = "";

    /// <summary>今回以降の変更と各回操作の競合を検出する版</summary>
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
}

/// <summary>変更せず残すルールの適用区間</summary>
public sealed class TaskSegment
{
    /// <summary>各回の識別に用いる区間ID</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>分割前後をまとめる系列</summary>
    public string SeriesId { get; set; } = "";

    /// <summary>未操作の回に適用する名前</summary>
    public string Title { get; set; } = "";

    /// <summary>未操作の回に適用するメモ</summary>
    public string Memo { get; set; } = "";

    /// <summary>共通設定の検索用テキスト</summary>
    public string SearchText { get; set; } = "";

    /// <summary>周期の基準日</summary>
    public DateOnly StartDate
    {
        get; set;
    }

    /// <summary>今回以降の変更で打ち切った日。この日以降は生成しない</summary>
    public DateOnly? Cutoff
    {
        get; set;
    }

    /// <summary>検証済みの周期・終了・休日調整</summary>
    public string RuleJson { get; set; } = "";

    /// <summary>区間の作成時に発行する版</summary>
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
}

/// <summary>必要な範囲だけ計算する予定の索引。利用者の操作記録とは分離する</summary>
public sealed class TaskSlot
{
    /// <summary>祝日更新による期限の変更も検出する表示版</summary>
    public string Version { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>区間IDと調整前の予定日の組</summary>
    public string Id { get; set; } = "";

    /// <summary>計算元の不変ルール</summary>
    public string SegmentId { get; set; } = "";

    /// <summary>調整前の予定日</summary>
    public DateOnly ScheduledDate
    {
        get; set;
    }

    /// <summary>調整後の期限。自動スキップ・データ待ちはnull</summary>
    public DateOnly? DueDate
    {
        get; set;
    }

    /// <summary>祝日未収録のため確定できない予定</summary>
    public bool Waiting
    {
        get; set;
    }

    /// <summary>到来済み・操作済みの期限や自動スキップを祝日訂正で動かさない</summary>
    public bool Frozen
    {
        get; set;
    }
}

/// <summary>再読込時に過去の全予定を再計算しないための月別計算記録</summary>
public sealed class TaskWindow
{
    /// <summary>計算したルール区間</summary>
    public string SegmentId { get; set; } = "";

    /// <summary>調整前の予定日の月初</summary>
    public DateOnly Month
    {
        get; set;
    }

    /// <summary>計算時の祝日データ更新時刻</summary>
    public long? HolidayRevision
    {
        get; set;
    }
}

/// <summary>応答喪失時も追加・完了・分割を二重適用しない操作結果</summary>
public sealed class TaskMutation
{
    /// <summary>再送の所有者境界</summary>
    public string UserId { get; set; } = "";

    /// <summary>ブラウザが操作意図ごとに発行するID</summary>
    public string Id { get; set; } = "";

    /// <summary>同じIDで別の入力を受け入れないためのハッシュ</summary>
    public string Hash { get; set; } = "";

    /// <summary>確定時の返却値</summary>
    public string ResultJson { get; set; } = "";
}

/// <summary>再開後も停止中に到来した未操作回を生成し直さない期間</summary>
public sealed class TaskPause
{
    /// <summary>停止期間の識別子</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>停止した日記帳</summary>
    public string NotebookId { get; set; } = "";

    /// <summary>停止開始日</summary>
    public DateOnly From
    {
        get; set;
    }

    /// <summary>再開日。この日の予定は再開する</summary>
    public DateOnly Until
    {
        get; set;
    }
}
