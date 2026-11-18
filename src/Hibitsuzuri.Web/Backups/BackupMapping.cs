using System.Text.Json;
using Hibitsuzuri.Web.Calendar;
using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Images;
using Hibitsuzuri.Web.Notifications;
using Hibitsuzuri.Web.Tasks;

namespace Hibitsuzuri.Web.Backups;

/// <summary>DBの所有者・排他制御・検索索引と、持ち運ぶ記録の形式を分離する</summary>
public static class BackupMapping
{
    /// <summary>日記帳名・テンプレート・表示順・停止状態を出力する</summary>
    public static BackupNotebook Export(Notebook value) => new(
        value.Id,
        value.Name,
        value.Template,
        value.Position,
        value.Archived,
        value.TasksPausedOn);

    /// <summary>日記帳設定を新しい排他情報で復元する。所有者とIDは呼び出し元で割り当てる</summary>
    public static Notebook Restore(BackupNotebook value) => new()
    {
        Id = value.Id,
        Name = value.Name,
        Template = value.Template,
        Position = value.Position,
        Archived = value.Archived,
        TasksPausedOn = value.TasksPausedOn
    };

    /// <summary>検索用の派生データを除き、日付・本文・保存時刻を出力する</summary>
    public static BackupEntry Export(DiaryEntry value) => new(
        value.Id,
        value.NotebookId,
        value.EntryDate,
        value.Content,
        value.CreatedAt,
        value.UpdatedAt);

    /// <summary>日記原文を復元する。関連IDの置換と検索文字列の生成は呼び出し元で行う</summary>
    public static DiaryEntry Restore(BackupEntry value) => new()
    {
        Id = value.Id,
        NotebookId = value.NotebookId,
        EntryDate = value.EntryDate,
        Content = value.Content,
        CreatedAt = value.CreatedAt,
        UpdatedAt = value.UpdatedAt
    };

    /// <summary>過去版の本文と、時刻に依存しない保持順序を出力する</summary>
    public static BackupRevision Export(DiaryRevision value) => new(
        value.Id,
        value.EntryId,
        value.Content,
        value.UpdatedAt,
        value.Sequence);

    /// <summary>履歴の本文と順序を復元する。所属日記と履歴IDは呼び出し元で置き換える</summary>
    public static DiaryRevision Restore(BackupRevision value) => new()
    {
        Id = value.Id,
        EntryId = value.EntryId,
        Content = value.Content,
        UpdatedAt = value.UpdatedAt,
        Sequence = value.Sequence
    };

    /// <summary>画像実体を照合する形式・寸法・サイズと添付一覧の所属を出力する</summary>
    public static BackupImage Export(DiaryImage value) => new(
        value.Id,
        value.NotebookId,
        value.EntryDate,
        value.Name,
        value.MediaType,
        value.Length,
        value.Width,
        value.Height,
        value.CreatedAt);

    /// <summary>検証済み画像の情報を復元する。実体の配置と所有者の割当ては呼び出し元で行う</summary>
    public static DiaryImage Restore(BackupImage value) => new()
    {
        Id = value.Id,
        NotebookId = value.NotebookId,
        EntryDate = value.EntryDate,
        Name = value.Name,
        MediaType = value.MediaType,
        Length = value.Length,
        Width = value.Width,
        Height = value.Height,
        CreatedAt = value.CreatedAt
    };

    /// <summary>排他制御用の版を除き、系列と日記帳の関連を出力する</summary>
    public static BackupTaskSeries Export(TaskSeries value) => new(
        value.Id,
        value.NotebookId);

    /// <summary>系列を新しい排他情報で復元する。関連IDは呼び出し元で置き換える</summary>
    public static TaskSeries Restore(BackupTaskSeries value) => new()
    {
        Id = value.Id,
        NotebookId = value.NotebookId
    };

    /// <summary>保存用JSON文字列を展開し、区間のルールと通知設定を出力する</summary>
    public static BackupTaskSegment Export(TaskSegment value) => new(
        value.Id,
        value.SeriesId,
        value.Title,
        value.Memo,
        value.StartDate,
        value.Cutoff,
        Export(RepeatRule.Read(value.RuleJson)),
        Reminders.Read(value.ReminderJson).Select(Export).ToArray());

    /// <summary>区間の共通設定を復元し、通知設定に新しい識別子を発行する</summary>
    public static TaskSegment Restore(BackupTaskSegment value) => new()
    {
        Id = value.Id,
        SeriesId = value.SeriesId,
        Title = value.Title,
        Memo = value.Memo,
        StartDate = value.StartDate,
        Cutoff = value.Cutoff,
        RuleJson = JsonSerializer.Serialize(Restore(value.Rule)),
        ReminderJson = JsonSerializer.Serialize(value.Reminders.Select(Restore))
    };

    /// <summary>操作済みの各回の本文・期限・完了状態と通知設定を出力する</summary>
    public static BackupTask Export(TaskRecord value) => new(
        value.Id,
        value.NotebookId,
        value.SeriesId,
        value.SegmentId,
        value.ScheduledDate,
        value.Title,
        value.Memo,
        value.DueDate,
        value.CompletedAt,
        value.Skipped,
        value.Edited,
        Reminders.Read(value.ReminderJson).Select(Export).ToArray());

    /// <summary>各回の操作結果を復元し、排他情報と通知設定の識別子を新しくする</summary>
    public static TaskRecord Restore(BackupTask value) => new()
    {
        Id = value.Id,
        NotebookId = value.NotebookId,
        SeriesId = value.SeriesId,
        SegmentId = value.SegmentId,
        ScheduledDate = value.ScheduledDate,
        Title = value.Title,
        Memo = value.Memo,
        DueDate = value.DueDate,
        CompletedAt = value.CompletedAt,
        Skipped = value.Skipped,
        Edited = value.Edited,
        ReminderJson = JsonSerializer.Serialize(value.Reminders.Select(Restore))
    };

    /// <summary>呼び出し元が保持対象と判定した予定の日付を出力する</summary>
    public static BackupTaskSlot Export(TaskSlot value) => new(
        value.Id,
        value.SegmentId,
        value.ScheduledDate,
        value.DueDate);

    /// <summary>過去の休日調整結果が変わらないよう、予定を確定済みとして復元する</summary>
    public static TaskSlot Restore(BackupTaskSlot value) => new()
    {
        Id = value.Id,
        SegmentId = value.SegmentId,
        ScheduledDate = value.ScheduledDate,
        DueDate = value.DueDate,
        Frozen = true
    };

    /// <summary>再開後にも生成を抑止する停止期間を出力する</summary>
    public static BackupTaskPause Export(TaskPause value) => new(
        value.Id,
        value.NotebookId,
        value.From,
        value.Until);

    /// <summary>停止期間を復元する。日記帳と期間のIDは呼び出し元で置き換える</summary>
    public static TaskPause Restore(BackupTaskPause value) => new()
    {
        Id = value.Id,
        NotebookId = value.NotebookId,
        From = value.From,
        Until = value.Until
    };

    /// <summary>所有者や中間テーブルの形に依存せず、記念日と表示先を出力する</summary>
    public static BackupAnniversary Export(Anniversary value) => new(
        value.Id,
        value.Title,
        value.Month,
        value.Day,
        value.LeapDayPolicy,
        value.AllNotebooks,
        value.Notebooks.Select(x => x.NotebookId).ToArray());

    /// <summary>記念日と表示先の関連を復元する。所有者と関連IDは呼び出し元で割り当てる</summary>
    public static Anniversary Restore(BackupAnniversary value) => new()
    {
        Id = value.Id,
        Title = value.Title,
        Month = value.Month,
        Day = value.Day,
        LeapDayPolicy = value.LeapDayPolicy,
        AllNotebooks = value.AllNotebooks,
        Notebooks = value.NotebookIds.Select(id => new AnniversaryNotebook { AnniversaryId = value.Id, NotebookId = id }).ToList()
    };

    /// <summary>配信履歴との関連を除き、通知時刻の指定だけを出力する</summary>
    public static BackupReminder Export(Reminder value) => new(
        value.Kind,
        value.DaysBefore,
        value.Time,
        value.At);

    /// <summary>過去の配信履歴と結び付かない新しい通知設定として復元する</summary>
    public static Reminder Restore(BackupReminder value) => new()
    {
        Kind = value.Kind,
        DaysBefore = value.DaysBefore,
        Time = value.Time,
        At = value.At,
        Id = Guid.NewGuid().ToString("N")
    };

    /// <summary>計算結果を含めず、周期・終了・休日調整の入力条件を出力する</summary>
    public static BackupRepeatRule Export(RepeatRule value) => new(
        value.Kind,
        value.Interval,
        value.Weekdays,
        value.MonthMode,
        value.MonthDay,
        value.Ordinal,
        value.Weekday,
        value.Month,
        value.EndOffset,
        value.Missing,
        value.Adjustment,
        value.AvoidWeekends,
        value.AvoidHolidays,
        value.Until,
        value.Count);

    /// <summary>保存された繰り返し条件を、検証・暦計算で扱う型へ変換する</summary>
    public static RepeatRule Restore(BackupRepeatRule value) => new()
    {
        Kind = value.Kind,
        Interval = value.Interval,
        Weekdays = value.Weekdays,
        MonthMode = value.MonthMode,
        MonthDay = value.MonthDay,
        Ordinal = value.Ordinal,
        Weekday = value.Weekday,
        Month = value.Month,
        EndOffset = value.EndOffset,
        Missing = value.Missing,
        Adjustment = value.Adjustment,
        AvoidWeekends = value.AvoidWeekends,
        AvoidHolidays = value.AvoidHolidays,
        Until = value.Until,
        Count = value.Count
    };
}
