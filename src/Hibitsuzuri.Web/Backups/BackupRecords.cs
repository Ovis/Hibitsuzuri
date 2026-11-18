namespace Hibitsuzuri.Web.Backups;

/// <summary>日記帳の表示と新規日記テンプレートを持ち運ぶ</summary>
/// <param name="Id">バックアップ内の関連付けに使う日記帳ID</param>
/// <param name="Name">日記帳名</param>
/// <param name="Template">新規日記のMarkdownテンプレート</param>
/// <param name="Position">表示順</param>
/// <param name="Archived">普段の選択肢から外した状態</param>
/// <param name="TasksPausedOn">タスクの停止開始日</param>
public sealed record BackupNotebook(
    string Id,
    string Name,
    string Template,
    int Position,
    bool Archived,
    DateOnly? TasksPausedOn);

/// <summary>日付に対応する日記の現行本文と作成・更新時刻</summary>
/// <param name="Id">バックアップ内の日記ID</param>
/// <param name="NotebookId">所属する日記帳ID</param>
/// <param name="EntryDate">日記の日付</param>
/// <param name="Content">Markdown原文</param>
/// <param name="CreatedAt">最初の保存時刻（UTCミリ秒）</param>
/// <param name="UpdatedAt">本文の更新時刻（UTCミリ秒）</param>
public sealed record BackupEntry(
    string Id,
    string NotebookId,
    DateOnly EntryDate,
    string Content,
    long CreatedAt,
    long UpdatedAt);

/// <summary>復元後も読み返せる日記の過去版</summary>
/// <param name="Id">バックアップ内の履歴ID</param>
/// <param name="EntryId">所属する日記ID</param>
/// <param name="Content">過去版のMarkdown原文</param>
/// <param name="UpdatedAt">当時の本文更新時刻（UTCミリ秒）</param>
/// <param name="Sequence">過去版を古い順に並べる番号</param>
public sealed record BackupRevision(
    string Id,
    string EntryId,
    string Content,
    long UpdatedAt,
    long Sequence);

/// <summary>画像実体の照合と添付一覧に必要な情報</summary>
/// <param name="Id">画像実体と本文参照を結ぶID</param>
/// <param name="NotebookId">添付一覧の所属日記帳ID</param>
/// <param name="EntryDate">添付一覧の所属日付</param>
/// <param name="Name">元のファイル名</param>
/// <param name="MediaType">検証済みのMIME形式</param>
/// <param name="Length">画像実体のバイト数</param>
/// <param name="Width">画像の横幅</param>
/// <param name="Height">画像の高さ</param>
/// <param name="CreatedAt">取り込み時刻（UTCミリ秒）</param>
public sealed record BackupImage(
    string Id,
    string NotebookId,
    DateOnly EntryDate,
    string Name,
    string MediaType,
    long Length,
    int Width,
    int Height,
    long CreatedAt);

/// <summary>ルール区間を一つの繰り返しへまとめる関連付け</summary>
/// <param name="Id">系列のID</param>
/// <param name="NotebookId">所属する日記帳ID</param>
public sealed record BackupTaskSeries(
    string Id,
    string NotebookId);

/// <summary>繰り返しルールの適用区間と共通設定</summary>
/// <param name="Id">区間のID</param>
/// <param name="SeriesId">所属する系列ID</param>
/// <param name="Title">未操作の回に使う件名</param>
/// <param name="Memo">未操作の回に使うメモ</param>
/// <param name="StartDate">周期の基準日</param>
/// <param name="Cutoff">この日以降の回を生成しない境界</param>
/// <param name="Rule">繰り返し周期・終了・休日調整</param>
/// <param name="Reminders">期限を基準にした通知設定</param>
public sealed record BackupTaskSegment(
    string Id,
    string SeriesId,
    string Title,
    string Memo,
    DateOnly StartDate,
    DateOnly? Cutoff,
    BackupRepeatRule Rule,
    BackupReminder[] Reminders);

/// <summary>通常タスクと利用者が操作した繰り返しの各回</summary>
/// <param name="Id">通常タスクまたは各回のID</param>
/// <param name="NotebookId">所属する日記帳ID</param>
/// <param name="SeriesId">繰り返しの場合の系列ID</param>
/// <param name="SegmentId">繰り返しの場合の区間ID</param>
/// <param name="ScheduledDate">休日調整・個別編集前の日付</param>
/// <param name="Title">表示する件名</param>
/// <param name="Memo">表示するメモ</param>
/// <param name="DueDate">個別変更を反映した期限</param>
/// <param name="CompletedAt">完了時刻（UTCミリ秒）</param>
/// <param name="Skipped">今回のみ削除した記録</param>
/// <param name="Edited">個別編集を保持する状態</param>
/// <param name="Reminders">この回に適用する通知設定</param>
public sealed record BackupTask(
    string Id,
    string NotebookId,
    string? SeriesId,
    string? SegmentId,
    DateOnly? ScheduledDate,
    string Title,
    string Memo,
    DateOnly? DueDate,
    long? CompletedAt,
    bool Skipped,
    bool Edited,
    BackupReminder[] Reminders);

/// <summary>再計算で変更してはいけない確定済みの予定</summary>
/// <param name="Id">区間と元の予定日からなる各回ID</param>
/// <param name="SegmentId">所属する区間ID</param>
/// <param name="ScheduledDate">休日調整前の日付</param>
/// <param name="DueDate">確定した期限。自動スキップはnull</param>
public sealed record BackupTaskSlot(
    string Id,
    string SegmentId,
    DateOnly ScheduledDate,
    DateOnly? DueDate);

/// <summary>再開後にも保持するタスク停止期間</summary>
/// <param name="Id">停止期間のID</param>
/// <param name="NotebookId">停止した日記帳ID</param>
/// <param name="From">停止開始日</param>
/// <param name="Until">予定を再開する日</param>
public sealed record BackupTaskPause(
    string Id,
    string NotebookId,
    DateOnly From,
    DateOnly Until);

/// <summary>個人の記念日と表示する日記帳の選択</summary>
/// <param name="Id">記念日のID</param>
/// <param name="Title">表示名</param>
/// <param name="Month">元の月</param>
/// <param name="Day">元の日</param>
/// <param name="LeapDayPolicy">非うるう年の2月29日の扱い</param>
/// <param name="AllNotebooks">今後追加するものも含む全日記帳への表示</param>
/// <param name="NotebookIds">個別に選択した日記帳ID</param>
public sealed record BackupAnniversary(
    string Id,
    string Title,
    int Month,
    int Day,
    string LeapDayPolicy,
    bool AllNotebooks,
    string[] NotebookIds);

/// <summary>認証や配信履歴から独立した通知時刻の指定</summary>
/// <param name="Kind">relativeまたはabsolute</param>
/// <param name="DaysBefore">期限からさかのぼる日数</param>
/// <param name="Time">相対指定のHH:mm</param>
/// <param name="At">日時指定のyyyy-MM-ddTHH:mm</param>
public sealed record BackupReminder(
    string Kind,
    int DaysBefore,
    string Time,
    string At);

/// <summary>月末や休日の扱いを含む繰り返し条件</summary>
/// <param name="Kind">daily・weekly・monthly・yearlyの周期</param>
/// <param name="Interval">周期の間隔</param>
/// <param name="Weekdays">日曜をビット0とする対象曜日</param>
/// <param name="MonthMode">日付・曜日・月末の指定方式</param>
/// <param name="MonthDay">月の指定日</param>
/// <param name="Ordinal">第何曜日か。最後は-1</param>
/// <param name="Weekday">日曜を0とする指定曜日</param>
/// <param name="Month">年ごとの対象月</param>
/// <param name="EndOffset">月末からさかのぼる日数</param>
/// <param name="Missing">存在しない日の扱い</param>
/// <param name="Adjustment">休日調整の方式</param>
/// <param name="AvoidWeekends">土日を避ける指定</param>
/// <param name="AvoidHolidays">祝日を避ける指定</param>
/// <param name="Until">調整前の日付で判定する最終日</param>
/// <param name="Count">自動スキップを除く生成回数</param>
public sealed record BackupRepeatRule(
    string Kind,
    int Interval,
    int Weekdays,
    string MonthMode,
    int MonthDay,
    int Ordinal,
    int Weekday,
    int Month,
    int EndOffset,
    string Missing,
    string Adjustment,
    bool AvoidWeekends,
    bool AvoidHolidays,
    DateOnly? Until,
    int? Count);
