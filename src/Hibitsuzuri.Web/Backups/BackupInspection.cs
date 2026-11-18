using System.Globalization;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Tasks;

namespace Hibitsuzuri.Web.Backups;

/// <summary>本文を保持せず、復元前に所属関係と値の範囲を検証する</summary>
public sealed class BackupInspection
{
    /// <summary>表示順と復元時の名前衝突を確認する日記帳</summary>
    public Dictionary<string, BackupNotebook> Books { get; } = [];

    /// <summary>履歴と画像参照の所属先</summary>
    public Dictionary<string, string> Entries { get; } = [];

    /// <summary>新しいIDを割り当てる系列</summary>
    public Dictionary<string, string> Series { get; } = [];

    /// <summary>各回のIDと系列の整合を確認する区間</summary>
    public Dictionary<string, BackupTaskSegment> Segments { get; } = [];

    /// <summary>実体検証と保存先の割り当てに使う画像情報</summary>
    public Dictionary<string, BackupImage> Images { get; } = [];

    /// <summary>欠落画像も既存アカウントのIDへ結び付けないための参照集合</summary>
    public HashSet<string> ImageReferences { get; } = [];

    /// <summary>確認画面へ表示する履歴数</summary>
    public int Revisions
    {
        get; private set;
    }

    /// <summary>確認画面へ表示する通常タスクと操作済みの回の数</summary>
    public int Tasks
    {
        get; private set;
    }

    /// <summary>確認画面へ表示する記念日数</summary>
    public int Anniversaries
    {
        get; private set;
    }

    /// <summary>例外を利用者向けの入力不正として統一する</summary>
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid)
    {
        if (!valid)
        {
            throw new InvalidDataException("データの値・重複・所属関係が不正です。");
        }
    }

    /// <summary>パスや復元先のキーへ持ち込めるIDを限定する</summary>
    public static bool ValidId(string? id) => Guid.TryParseExact(id, "N", out var guid) && guid.ToString("N") == id;

    /// <summary>画面の日時変換が扱えるUTCミリ秒だけを許可する</summary>
    private static bool Timestamp(long value) => value is >= 0 and <= 253402300799999;

    /// <summary>順に読み取り、本文のサイズに比例したメモリを確保しない</summary>
    public static BackupInspection Read(BackupArchive archive, DiaryMarkdown markdown)
    {
        var result = new BackupInspection();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, book) in archive.Read<BackupNotebook>("books"))
        {
            Require(ValidId(book.Id) && AuthRules.ValidText(book.Name, 1, 100)
                && book.Name == book.Name.Trim() && AuthRules.ValidText(book.Template, 0, 100000) && book.Position >= 0
                && names.Add(book.Name));
            Require(result.Books.TryAdd(book.Id, book with
            {
                Template = ""
            }));

            result.ImageReferences.UnionWith(markdown.ImageIds(book.Template));
        }

        var dates = new HashSet<(string, DateOnly)>();
        foreach (var (_, entry) in archive.Read<BackupEntry>("entries"))
        {
            Require(ValidId(entry.Id) && result.Books.ContainsKey(entry.NotebookId)
                && AuthRules.ValidText(entry.Content, 1, 100000) && !string.IsNullOrWhiteSpace(entry.Content)
                && Timestamp(entry.CreatedAt) && Timestamp(entry.UpdatedAt)
                && dates.Add((entry.NotebookId, entry.EntryDate)) && result.Entries.TryAdd(entry.Id, entry.NotebookId));
            result.ImageReferences.UnionWith(markdown.ImageIds(entry.Content));
        }

        var revisions = new HashSet<string>();
        var sequences = new HashSet<(string, long)>();
        var historyCount = new Dictionary<string, int>();
        foreach (var (_, revision) in archive.Read<BackupRevision>("revisions"))
        {
            Require(ValidId(revision.Id) && revisions.Add(revision.Id) && result.Entries.ContainsKey(revision.EntryId)
                && AuthRules.ValidText(revision.Content, 1, 100000) && Timestamp(revision.UpdatedAt) && revision.Sequence >= 0
                && sequences.Add((revision.EntryId, revision.Sequence)));
            historyCount[revision.EntryId] = historyCount.GetValueOrDefault(revision.EntryId) + 1;
            Require(historyCount[revision.EntryId] <= 5);
            result.ImageReferences.UnionWith(markdown.ImageIds(revision.Content));
            result.Revisions++;
        }

        foreach (var (_, image) in archive.Read<BackupImage>("images"))
        {
            Require(ValidId(image.Id) && result.Books.ContainsKey(image.NotebookId)
                && AuthRules.ValidText(image.Name, 0, 200) && image.MediaType is "image/png" or "image/jpeg" or "image/webp" or "image/gif"
                && image.Width is >= 1 and <= 16384 && image.Height is >= 1 and <= 16384 && (long)image.Width * image.Height <= 40000000
                && image.Length is >= 1 and <= 160000000 && Timestamp(image.CreatedAt)
                && result.ImageReferences.Contains(image.Id) && result.Images.TryAdd(image.Id, image));
        }

        Require(result.Images.Keys.ToHashSet().SetEquals(archive.AssetIds));
        result.ReadTasks(archive);
        result.ReadAnniversaries(archive);
        return result;
    }

    /// <summary>停止期間・不変区間・操作した各回の参照を検証する</summary>
    private void ReadTasks(BackupArchive archive)
    {
        var ids = new HashSet<string>();
        foreach (var (_, pause) in archive.Read<BackupTaskPause>("pauses"))
        {
            Require(ValidId(pause.Id) && ids.Add(pause.Id) && Books.ContainsKey(pause.NotebookId) && pause.From < pause.Until);
        }

        foreach (var (_, series) in archive.Read<BackupTaskSeries>("series"))
        {
            Require(ValidId(series.Id) && Books.ContainsKey(series.NotebookId) && Series.TryAdd(series.Id, series.NotebookId));
        }

        foreach (var (_, segment) in archive.Read<BackupTaskSegment>("segments"))
        {
            Require(ValidId(segment.Id) && Series.ContainsKey(segment.SeriesId)
                && AuthRules.ValidText(segment.Title, 1, 200) && AuthRules.ValidText(segment.Memo, 0, 10000)
                && (segment.Cutoff is null || segment.Cutoff >= segment.StartDate)
                && segment.Rule is not null && BackupMapping.Restore(segment.Rule).Valid(segment.StartDate));
            ValidateReminders(segment.Reminders, true, segment.StartDate);
            Require(Segments.TryAdd(segment.Id, segment with
            {
                Memo = ""
            }));
        }

        Require(Segments.Values.Select(x => x.SeriesId).ToHashSet().SetEquals(Series.Keys));
        ids.Clear();
        foreach (var (_, task) in archive.Read<BackupTask>("tasks"))
        {
            Require(task.Id is not null && ids.Add(task.Id) && Books.ContainsKey(task.NotebookId)
                && AuthRules.ValidText(task.Title, 1, 200) && AuthRules.ValidText(task.Memo, 0, 10000)
                && (task.CompletedAt is null || Timestamp(task.CompletedAt.Value)));
            if (task.SeriesId is null)
            {
                Require(ValidId(task.Id) && task.SegmentId is null && task.ScheduledDate is null);
            }
            else
            {
                Require(task.SegmentId is not null && Segments.TryGetValue(task.SegmentId, out var segment)
                    && segment.SeriesId == task.SeriesId && Series[task.SeriesId] == task.NotebookId && task.ScheduledDate is { } date
                    && task.Id == TaskSchedule.Key(task.SegmentId, date) && date >= segment.StartDate);
            }

            ValidateReminders(task.Reminders, false, task.DueDate);
            Tasks++;
        }

        ids.Clear();
        foreach (var (_, slot) in archive.Read<BackupTaskSlot>("slots"))
        {
            Require(slot.Id is not null && ids.Add(slot.Id) && Segments.TryGetValue(slot.SegmentId, out var segment)
                && slot.Id == TaskSchedule.Key(slot.SegmentId, slot.ScheduledDate) && slot.ScheduledDate >= segment.StartDate);
        }
    }

    /// <summary>過去の通知設定は保持し、日時の形と範囲だけを検証する</summary>
    private static void ValidateReminders(IReadOnlyList<BackupReminder> reminders, bool recurring, DateOnly? due)
    {
        Require(reminders is not null && reminders.Count <= 3);
        foreach (var reminder in reminders)
        {
            Require(reminder is not null
                && reminder.Kind is "relative" or "absolute" && (!recurring || reminder.Kind == "relative")
                && reminder.DaysBefore is >= 0 and <= 365
                && reminder.Time is not null && reminder.Time.Length <= 5
                && reminder.At is not null && reminder.At.Length <= 16);
            Require(reminder.Kind == "relative"
                ? due is not null && TimeOnly.TryParseExact(reminder.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                : DateTime.TryParseExact(reminder.At, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
        }
    }

    /// <summary>記念日の有効日と本人の日記帳への表示先を確認する</summary>
    private void ReadAnniversaries(BackupArchive archive)
    {
        var ids = new HashSet<string>();
        foreach (var (_, anniversary) in archive.Read<BackupAnniversary>("anniversaries"))
        {
            Require(ValidId(anniversary.Id) && ids.Add(anniversary.Id)
                && AuthRules.ValidText(anniversary.Title, 1, 100) && anniversary.Month is >= 1 and <= 12
                && anniversary.Day >= 1 && anniversary.Day <= DateTime.DaysInMonth(2000, anniversary.Month)
                && anniversary.LeapDayPolicy is "skip" or "feb28" or "mar1" && anniversary.NotebookIds is not null);
            Require(anniversary.NotebookIds.All(x => x is not null && Books.ContainsKey(x))
                && anniversary.NotebookIds.Distinct().Count() == anniversary.NotebookIds.Length
                && (anniversary.AllNotebooks ? anniversary.NotebookIds.Length == 0 : anniversary.NotebookIds.Length > 0));
            Anniversaries++;
        }
    }
}
