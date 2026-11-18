using System.Globalization;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Notifications;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Hibitsuzuri.Web.Tasks;

/// <summary>操作記録と分離した予定索引を、必要な月だけ原子的に更新する</summary>
public sealed class TaskSchedule(AppDbContext db, TimeProvider clock, AuthWriteGate gate)
{
    /// <summary>日付境界はサーバーのタイムゾーンで決める</summary>
    public DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    /// <summary>過去全体の集計ではfromを省略し、カレンダーでは表示月だけを指定する</summary>
    public async Task EnsureAsync(string[] bookIds, DateOnly? from, DateOnly through, bool locked = false)
    {
        if (!locked)
        {
            await gate.Semaphore.WaitAsync();
        }

        try
        {
            await EnsureLockedAsync(bookIds, from, through);
        }
        finally
        {
            if (!locked)
            {
                gate.Semaphore.Release();
            }
        }
    }

    /// <summary>祝日の訂正前に到来済みの予定を旧データで確定する</summary>
    /// <remarks>呼び出し元でAuthWriteGateを取得し、祝日の入れ替えと同じトランザクション内で実行する</remarks>
    public async Task FreezePastAsync(CancellationToken cancellationToken)
    {
        var books = await db.TaskSeries.Select(x => x.NotebookId).Distinct().ToArrayAsync(cancellationToken);
        await EnsureLockedAsync(books, null, Offset(Today, 365));
    }

    /// <summary>日付を最大・最小値で止め、暦の端でもオーバーフローさせない</summary>
    public static DateOnly Offset(DateOnly date, int days) => DateOnly.FromDayNumber((int)Math.Clamp((long)date.DayNumber + days, 0, DateOnly.MaxValue.DayNumber));

    /// <summary>所有者の範囲を呼び出し元で確定したうえで索引を更新する</summary>
    private async Task EnsureLockedAsync(string[] bookIds, DateOnly? from, DateOnly through)
    {
        var segments = await db.TaskSegments.AsNoTracking().Where(x => db.TaskSeries.Any(s => s.Id == x.SeriesId && bookIds.Contains(s.NotebookId))).ToListAsync();
        if (segments.Count == 0)
        {
            return;
        }

        var state = await db.HolidayStates.SingleAsync();
        var dates = await db.Holidays.Select(x => x.Date).ToListAsync();
        var holidays = new HolidayCalendar(state.FirstYear, state.LastYear, dates.ToHashSet());
        var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync() : null;
        try
        {
            foreach (var segment in segments)
            {
                var rule = RepeatRule.Read(segment.RuleJson);
                var begin = from ?? segment.StartDate;
                var end = through;

                // 連休による移動で表示期間へ入る、期間外の元予定も索引へ含める。
                if (rule.Adjustment == "next")
                {
                    while (begin > segment.StartDate && RepeatCalculator.Adjust(Offset(begin, -1), rule, holidays).Due >= begin)
                    {
                        begin = Offset(begin, -1);
                    }
                }
                else if (rule.Adjustment == "previous")
                {
                    while (end < DateOnly.MaxValue && RepeatCalculator.Adjust(Offset(end, 1), rule, holidays).Due <= end)
                    {
                        end = Offset(end, 1);
                    }
                }

                begin = begin < segment.StartDate ? segment.StartDate : begin;
                if (segment.Cutoff is { } cutoff)
                {
                    end = cutoff == DateOnly.MinValue ? DateOnly.MinValue : end < cutoff ? end : cutoff.AddDays(-1);
                }

                var windows = await db.TaskWindows.AsNoTracking().Where(x => x.SegmentId == segment.Id).ToDictionaryAsync(x => x.Month);
                var months = new List<DateOnly>();
                for (var month = new DateOnly(begin.Year, begin.Month, 1); begin <= end && (segment.Cutoff == null || segment.Cutoff > begin) && month <= end;)
                {
                    if (!windows.TryGetValue(month, out var window) || (rule.NeedsHolidays && window.HolidayRevision != state.LastSuccessAt))
                    {
                        months.Add(month);
                    }

                    if (month.Year == 9999 && month.Month == 12)
                    {
                        break;
                    }

                    month = month.AddMonths(1);
                }

                // 以前の表示範囲から移動する予定も更新し、古い期限が現在の検索へ混ざるのを防ぐ。
                if (rule.NeedsHolidays)
                {
                    months = months.Concat(windows.Values.Where(x => x.HolidayRevision != state.LastSuccessAt).Select(x => x.Month))
                        .Distinct().Order().ToList();
                }

                if (months.Count == 0)
                {
                    continue;
                }

                var first = months[0];
                var last = new DateOnly(months[^1].Year, months[^1].Month, DateTime.DaysInMonth(months[^1].Year, months[^1].Month));
                var prior = await db.TaskSlots.AsNoTracking().Where(x => x.SegmentId == segment.Id
                    && (rule.Count != null || (x.ScheduledDate >= first && x.ScheduledDate <= last))).ToListAsync();
                var operations = await db.TaskRecords.AsNoTracking().Where(x => x.SegmentId == segment.Id).ToListAsync();
                var operated = operations.Select(x => x.ScheduledDate).ToHashSet();
                var frozen = prior.Where(x => !x.Waiting && (x.Frozen || operated.Contains(x.ScheduledDate) || (x.DueDate ?? x.ScheduledDate) <= Today
                    || Reminders.HasArrived(segment.ReminderJson, x.DueDate, clock)))
                    .ToDictionary(x => x.ScheduledDate, x => new ScheduledTask(x.ScheduledDate, x.DueDate, false));
                // 通常タスクからの変換では索引より先に初回の完了記録ができる。
                foreach (var operation in operations.Where(x => x.ScheduledDate != null && x.CompletedAt != null))
                {
                    var scheduled = operation.ScheduledDate.GetValueOrDefault();
                    frozen.TryAdd(scheduled, new ScheduledTask(scheduled, operation.DueDate ?? scheduled, false));
                }

                var wanted = months.ToHashSet();
                var generated = RepeatCalculator.Generate(rule, segment.StartDate, first, last, holidays, frozen)
                    .Where(x => x.Waiting || wanted.Contains(new DateOnly(x.Scheduled.Year, x.Scheduled.Month, 1))).ToList();
                months = months.Concat(generated.Where(x => x.Waiting).Select(x => new DateOnly(x.Scheduled.Year, x.Scheduled.Month, 1)))
                    .Distinct().Order().ToList();

                // 回数制限の到達位置が変わった場合、旧計算の未来予定を残さない。
                foreach (var month in months)
                {
                    var monthEnd = new DateOnly(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));
                    await db.TaskSlots.Where(x => x.SegmentId == segment.Id && x.ScheduledDate >= month && x.ScheduledDate <= monthEnd
                        && !x.Frozen && (x.Waiting || (x.DueDate ?? x.ScheduledDate) > Today)).ExecuteDeleteAsync();
                }

                var revisedMonths = windows.Values.Where(x => x.HolidayRevision != state.LastSuccessAt).Select(x => x.Month).ToHashSet();
                await WriteSlotsAsync(segment, generated, frozen, prior.ToDictionary(x => x.ScheduledDate), months, revisedMonths, state.LastSuccessAt);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>過去数十年の初回集計でも大量のEF追跡オブジェクトを作らない</summary>
    private async Task WriteSlotsAsync(TaskSegment segment, IReadOnlyList<ScheduledTask> generated,
        IReadOnlyDictionary<DateOnly, ScheduledTask> frozen, IReadOnlyDictionary<DateOnly, TaskSlot> prior, IReadOnlyList<DateOnly> months,
        IReadOnlySet<DateOnly> revisedMonths, long? revision)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = """
            INSERT INTO TaskSlots (Id,SegmentId,ScheduledDate,DueDate,Waiting,Frozen,Version,NotificationAfter)
            VALUES ($id,$segment,$date,$due,$waiting,$frozen,$version,$after)
            ON CONFLICT(Id) DO UPDATE SET
                DueDate=excluded.DueDate, Waiting=excluded.Waiting, Frozen=excluded.Frozen,
                Version=CASE WHEN DueDate IS excluded.DueDate AND Waiting=excluded.Waiting THEN Version ELSE excluded.Version END,
                NotificationAfter=MAX(NotificationAfter,excluded.NotificationAfter)
            """;
        foreach (var name in new[] { "$id", "$segment", "$date", "$due", "$waiting", "$frozen", "$version", "$after" })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            command.Parameters.Add(parameter);
        }

        await command.PrepareAsync();

        foreach (var item in generated)
        {
            var retained = frozen.GetValueOrDefault(item.Scheduled) ?? item;
            var arrived = Reminders.HasArrived(segment.ReminderJson, retained.Due, clock);
            var previous = prior.GetValueOrDefault(item.Scheduled);
            var revisedIntoPast = !frozen.ContainsKey(item.Scheduled) && arrived
                && (previous is not null ? previous.Waiting || previous.DueDate != retained.Due
                    : revisedMonths.Contains(new DateOnly(item.Scheduled.Year, item.Scheduled.Month, 1)));
            command.Parameters[0].Value = Key(segment.Id, item.Scheduled);
            command.Parameters[1].Value = segment.Id;
            command.Parameters[2].Value = Day(item.Scheduled);
            command.Parameters[3].Value = retained.Due is { } due ? Day(due) : DBNull.Value;
            command.Parameters[4].Value = retained.Waiting;
            command.Parameters[5].Value = frozen.ContainsKey(item.Scheduled) || (!retained.Waiting && ((retained.Due ?? retained.Scheduled) <= Today || arrived));
            command.Parameters[6].Value = Guid.NewGuid().ToString("N");
            command.Parameters[7].Value = revisedIntoPast ? clock.GetUtcNow().ToUnixTimeMilliseconds() : previous?.NotificationAfter ?? 0;
            await command.ExecuteNonQueryAsync();
        }

        foreach (var month in months)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO TaskWindows (SegmentId,Month,HolidayRevision) VALUES ({segment.Id},{Day(month)},{revision}) ON CONFLICT(SegmentId,Month) DO UPDATE SET HolidayRevision=excluded.HolidayRevision");
        }
    }

    /// <summary>調整後の日が重なっても各回のIDを分ける</summary>
    public static string Key(string segment, DateOnly date) => $"{segment}:{Day(date)}";

    /// <summary>SQLiteの日付比較に使う固定書式</summary>
    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
