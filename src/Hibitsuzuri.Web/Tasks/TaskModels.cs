using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Notifications;

namespace Hibitsuzuri.Web.Tasks;

/// <summary>保存済みの操作と未操作の予定を同じ形で表示する</summary>
public sealed class TaskRow
{
    /// <summary>共通設定または今回だけ変更した通知設定</summary>
    public string ReminderJson { get; set; } = "[]";

    /// <summary>再開・祝日訂正によって送信を許可しない過去の境界</summary>
    public long NotificationAfter
    {
        get; set;
    }

    /// <summary>祝日データが揃うまで期限と完了操作を確定させない</summary>
    public bool Waiting
    {
        get; set;
    }

    /// <summary>通常タスクまたは区間と元予定日で識別する各回</summary>
    public string Id { get; set; } = "";

    /// <summary>所属日記帳</summary>
    public string NotebookId { get; set; } = "";

    /// <summary>全日記帳表示に添える名称</summary>
    public string BookName { get; set; } = "";

    /// <summary>表示する件名</summary>
    public string Title { get; set; } = "";

    /// <summary>プレーンテキストの補足</summary>
    public string Memo { get; set; } = "";

    /// <summary>幅と大文字小文字を揃えた検索用本文</summary>
    public string SearchText { get; set; } = "";

    /// <summary>休日調整・個別変更後の期限</summary>
    public DateOnly? DueDate
    {
        get; set;
    }

    /// <summary>最新の完了操作のUTC時刻</summary>
    public long? CompletedAt
    {
        get; set;
    }

    /// <summary>繰り返しの系列</summary>
    public string? SeriesId
    {
        get; set;
    }

    /// <summary>ルールの適用区間</summary>
    public string? SegmentId
    {
        get; set;
    }

    /// <summary>調整前の予定日</summary>
    public DateOnly? ScheduledDate
    {
        get; set;
    }

    /// <summary>操作対象の版</summary>
    public string Version { get; set; } = "";

    /// <summary>日記帳のタスク停止中は変更を許可しない</summary>
    public bool ReadOnly
    {
        get; set;
    }

}

/// <summary>一覧・履歴・日記の取得条件</summary>
public sealed class TaskFilter
{
    /// <summary>利用者が指定した繰り返し表示の末尾日</summary>
    public DateOnly? Through
    {
        get; set;
    }

    /// <summary>省略時は所有する全日記帳</summary>
    public string? BookId
    {
        get; set;
    }

    /// <summary>pendingまたはcompleted</summary>
    public string Mode { get; set; } = "pending";

    /// <summary>all・dated・undatedの期限絞り込み</summary>
    public string Kind { get; set; } = "all";

    /// <summary>all・overdue・todayの期限絞り込み</summary>
    public string Quick { get; set; } = "all";

    /// <summary>件名とメモの部分一致</summary>
    public string Query { get; set; } = "";

    /// <summary>完了日時の30日・今月・先月・指定・全期間</summary>
    public string Period { get; set; } = "30";

    /// <summary>指定期間の先頭日</summary>
    public DateOnly? Start
    {
        get; set;
    }

    /// <summary>指定期間の末尾日を含む</summary>
    public DateOnly? End
    {
        get; set;
    }

    /// <summary>日記に表示する期限日</summary>
    public DateOnly? Day
    {
        get; set;
    }

    /// <summary>展開する繰り返し系列</summary>
    public string? SeriesId
    {
        get; set;
    }

    /// <summary>50件ずつ取得するページ</summary>
    public int Page { get; set; } = 1;

}

/// <summary>版と操作IDを含むタスク変更要求</summary>
public sealed class TaskChange
{
    /// <summary>nullは現在の設定を保持し、空の配列は通知なしへ変更する</summary>
    public List<Reminder>? Reminders
    {
        get; set;
    }

    /// <summary>入力検証後だけに設定する永続化用の値</summary>
    internal string PreparedReminders { get; set; } = "[]";

    /// <summary>同じ操作の再送を識別するUUID</summary>
    public string OperationId { get; set; } = "";

    /// <summary>save・complete・deleteの変更種別</summary>
    public string Action { get; set; } = "save";

    /// <summary>新規時だけ省略する操作対象</summary>
    public string? Id
    {
        get; set;
    }

    /// <summary>新規タスクの所属先</summary>
    public string NotebookId { get; set; } = "";

    /// <summary>読み込み時の各回の版</summary>
    public string Version { get; set; } = "";

    /// <summary>今回以降の変更前に確認した系列の版</summary>
    public string SeriesVersion { get; set; } = "";

    /// <summary>onceまたはfollowing</summary>
    public string Scope { get; set; } = "once";

    /// <summary>空白以外を必要とする件名</summary>
    public string Title { get; set; } = "";

    /// <summary>リンクを含められるプレーンテキスト</summary>
    public string Memo { get; set; } = "";

    /// <summary>通常タスク・個別編集の期限</summary>
    public DateOnly? DueDate
    {
        get; set;
    }

    /// <summary>完了後の希望状態</summary>
    public bool Completed
    {
        get; set;
    }

    /// <summary>新規または今回以降に適用する繰り返し条件</summary>
    public RepeatRule? Rule
    {
        get; set;
    }

    /// <summary>繰り返しの起点</summary>
    public DateOnly? StartDate
    {
        get; set;
    }

    /// <summary>日記からの操作時に過去日の変更を禁止する</summary>
    public DateOnly? ContextDay
    {
        get; set;
    }

    /// <summary>日記から別日記帳のタスクを操作させない</summary>
    public string? ContextBook
    {
        get; set;
    }

}

/// <summary>一覧と日記の共通操作画面へ渡す日記帳と日付</summary>
/// <param name="Books">本人の日記帳</param>
/// <param name="BookId">初期選択先</param>
/// <param name="Today">サーバーの今日</param>
/// <param name="Day">日記に埋め込む場合の表示日</param>
public sealed record TaskPage(IReadOnlyList<Notebook> Books, string? BookId, DateOnly Today, DateOnly? Day = null);
