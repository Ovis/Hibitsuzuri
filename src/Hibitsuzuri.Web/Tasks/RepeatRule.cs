using System.Text.Json;

namespace Hibitsuzuri.Web.Tasks;

/// <summary>日・週・月・年の周期と欠ける日付・休日の扱い</summary>
public sealed class RepeatRule
{
    /// <summary>daily・weekly・monthly・yearlyのいずれか</summary>
    public string Kind { get; set; } = "daily";

    /// <summary>周期の間隔。入力・計算の負荷を揃えて1～52に限定する</summary>
    public int Interval { get; set; } = 1;

    /// <summary>日曜をビット0とする複数曜日の選択</summary>
    public int Weekdays { get; set; } = 62;

    /// <summary>day・weekday・endのいずれか</summary>
    public string MonthMode { get; set; } = "day";

    /// <summary>月の指定日</summary>
    public int MonthDay { get; set; } = 1;

    /// <summary>第1～第5、または最後を表す-1</summary>
    public int Ordinal { get; set; } = 1;

    /// <summary>月内の指定曜日。日曜を0とする</summary>
    public int Weekday { get; set; } = 1;

    /// <summary>年ごとの対象月</summary>
    public int Month { get; set; } = 1;

    /// <summary>月末からさかのぼる日数</summary>
    public int EndOffset
    {
        get; set;
    }

    /// <summary>存在しない指定日をskipまたはlastで扱う</summary>
    public string Missing { get; set; } = "skip";

    /// <summary>none・skip・previous・nextの休日調整</summary>
    public string Adjustment { get; set; } = "none";

    /// <summary>土日を避ける対象にする</summary>
    public bool AvoidWeekends
    {
        get; set;
    }

    /// <summary>公式の祝日・休日を避ける対象にする</summary>
    public bool AvoidHolidays
    {
        get; set;
    }

    /// <summary>調整前の日付で判定する終了日を含む</summary>
    public DateOnly? Until
    {
        get; set;
    }

    /// <summary>自動スキップを除いた生成回数。終了日との併用はしない</summary>
    public int? Count
    {
        get; set;
    }

    /// <summary>祝日の収録状況が計算に影響するか</summary>
    public bool NeedsHolidays => Adjustment != "none" && AvoidHolidays;

    /// <summary>画面とサーバーの両方で使える保存前の条件検証</summary>
    public bool Valid(DateOnly start) => Kind is "daily" or "weekly" or "monthly" or "yearly"
        && Interval is >= 1 and <= 52 && Weekdays is >= 1 and <= 127
        && MonthMode is "day" or "weekday" or "end" && MonthDay is >= 1 and <= 31
        && (Ordinal is >= 1 and <= 5 || Ordinal == -1) && Weekday is >= 0 and <= 6
        && Month is >= 1 and <= 12 && EndOffset is >= 0 and <= 27
        && Missing is "skip" or "last" && Adjustment is "none" or "skip" or "previous" or "next"
        && (Until is null || Until >= start) && (Count is null || Count is >= 1 and <= 10000)
        && (Count is null || Until is null);

    /// <summary>永続化したルールを読み、破損を既定値で隠さない</summary>
    public static RepeatRule Read(string json) => JsonSerializer.Deserialize<RepeatRule>(json) ?? throw new InvalidDataException("繰り返し設定を読み取れません。");
}

/// <summary>計算に用いる公式データの収録範囲と日付集合</summary>
/// <param name="FirstYear">収録先頭年</param>
/// <param name="LastYear">収録末尾年</param>
/// <param name="Dates">祝日・休日の日付</param>
public sealed record HolidayCalendar(int FirstYear, int LastYear, IReadOnlySet<DateOnly> Dates);

/// <summary>元の予定と調整後の期限を分離した計算結果</summary>
/// <param name="Scheduled">調整前の予定日</param>
/// <param name="Due">調整後の期限。スキップ・未確定ではnull</param>
/// <param name="Waiting">祝日データを待っている状態</param>
public sealed record ScheduledTask(DateOnly Scheduled, DateOnly? Due, bool Waiting);

/// <summary>DBや現在時刻に依存しない繰り返し計算</summary>
public static class RepeatCalculator
{
    /// <summary>必要な元予定日の範囲だけを列挙し、終了回数は先頭から数える</summary>
    public static IEnumerable<ScheduledTask> Generate(RepeatRule rule, DateOnly start, DateOnly from, DateOnly through, HolidayCalendar holidays,
        IReadOnlyDictionary<DateOnly, ScheduledTask>? frozen = null)
    {
        var end = rule.Until is { } until && until < through ? until : through;
        var accepted = 0;
        foreach (var date in Candidates(rule, start, rule.Count is null ? from : start, end))
        {
            var item = frozen?.GetValueOrDefault(date) ?? Adjust(date, rule, holidays);
            if (item.Waiting && rule.Count is not null)
            {
                // 未収録範囲で回数を推測すると、後の回が終了回数を越える可能性がある。
                if (date <= through)
                {
                    yield return item;
                }

                yield break;
            }

            if (item.Due is not null)
            {
                accepted++;
            }

            if (date >= from)
            {
                yield return item;
            }

            if (rule.Count is { } count && accepted >= count)
            {
                yield break;
            }
        }
    }

    /// <summary>調整先が未収録になった場合も未確定に留める</summary>
    public static ScheduledTask Adjust(DateOnly date, RepeatRule rule, HolidayCalendar holidays)
    {
        if (rule.Adjustment == "none")
        {
            return new(date, date, false);
        }

        var candidate = date;
        while (true)
        {
            if (rule.NeedsHolidays && (candidate.Year < holidays.FirstYear || candidate.Year > holidays.LastYear))
            {
                return new(date, null, true);
            }

            var excluded = (rule.AvoidWeekends && candidate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                || (rule.AvoidHolidays && holidays.Dates.Contains(candidate));
            if (!excluded)
            {
                return new(date, candidate, false);
            }

            if (rule.Adjustment == "skip" || (rule.Adjustment == "previous" && candidate == DateOnly.MinValue)
                || (rule.Adjustment == "next" && candidate == DateOnly.MaxValue))
            {
                return new(date, null, false);
            }

            candidate = candidate.AddDays(rule.Adjustment == "previous" ? -1 : 1);
        }
    }

    /// <summary>周期の先頭へ直接移動し、開始日前の候補は作らない</summary>
    private static IEnumerable<DateOnly> Candidates(RepeatRule rule, DateOnly start, DateOnly from, DateOnly through)
    {
        var begin = from > start ? from : start;
        if (begin > through)
        {
            yield break;
        }

        if (rule.Kind == "daily")
        {
            var first = start.DayNumber + (begin.DayNumber - start.DayNumber + rule.Interval - 1) / rule.Interval * rule.Interval;
            for (var number = first; number <= through.DayNumber; number += rule.Interval)
            {
                yield return DateOnly.FromDayNumber(number);
            }

            yield break;
        }

        if (rule.Kind == "weekly")
        {
            var monday = start.DayNumber - ((int)start.DayOfWeek + 6) % 7;
            var firstWeek = (begin.DayNumber - monday) / 7 / rule.Interval * rule.Interval;
            for (var week = monday + firstWeek * 7; week <= through.DayNumber; week += rule.Interval * 7)
            {
                for (var offset = 0; offset < 7; offset++)
                {
                    var number = week + offset;
                    if (number >= begin.DayNumber && number <= through.DayNumber && (rule.Weekdays & (1 << ((offset + 1) % 7))) != 0)
                    {
                        yield return DateOnly.FromDayNumber(number);
                    }
                }
            }

            yield break;
        }

        var anchor = (start.Year - 1) * 12 + (rule.Kind == "yearly" ? rule.Month : start.Month) - 1;
        var step = rule.Interval * (rule.Kind == "yearly" ? 12 : 1);
        var beginMonth = (begin.Year - 1) * 12 + begin.Month - 1;
        var index = anchor + Math.Max(0, (beginMonth - anchor) / step) * step;
        for (; index <= (through.Year - 1) * 12 + through.Month - 1; index += step)
        {
            var year = index / 12 + 1;
            var month = index % 12 + 1;
            var last = DateTime.DaysInMonth(year, month);
            var day = rule.MonthMode switch
            {
                "end" => last - rule.EndOffset,
                "weekday" when rule.Ordinal == -1 => last - ((int)new DateOnly(year, month, last).DayOfWeek - rule.Weekday + 7) % 7,
                "weekday" => 1 + (rule.Weekday - (int)new DateOnly(year, month, 1).DayOfWeek + 7) % 7 + (rule.Ordinal - 1) * 7,
                _ => rule.MonthDay > last && rule.Missing == "last" ? last : rule.MonthDay
            };
            if (day >= 1 && day <= last)
            {
                var date = new DateOnly(year, month, day);
                if (date >= begin && date <= through)
                {
                    yield return date;
                }
            }
        }
    }
}
