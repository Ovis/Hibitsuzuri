using Hibitsuzuri.Web.Tasks;

namespace Hibitsuzuri.Tests;

/// <summary>暦・休日・終了条件の境界と元予定日の安定性を検証する</summary>
public sealed class RepeatCalculatorTests
{
    private static readonly HolidayCalendar Known = new(1955, 2030, new HashSet<DateOnly> { new(2026, 11, 3), new(2026, 11, 23) });

    /// <summary>月曜起点の隔週を、開始日より前へ戻さず複数曜日に展開する</summary>
    [Test]
    public void WeeklyUsesMondayAnchorAndSelectedWeekdays()
    {
        var rule = new RepeatRule { Kind = "weekly", Interval = 2, Weekdays = (1 << 1) | (1 << 5) };
        var dates = Dates(rule, "2026-11-04", "2026-11-30");
        Assert.That(dates, Is.EqualTo(new[] { "2026-11-06", "2026-11-16", "2026-11-20", "2026-11-30" }));
    }

    /// <summary>31日と月末補正、第5曜日・最後の曜日・月末からの日数を区別する</summary>
    [TestCase("day", 31, 1, 1, 0, "skip", "2026-01-31,2026-03-31")]
    [TestCase("day", 31, 1, 1, 0, "last", "2026-01-31,2026-02-28,2026-03-31")]
    [TestCase("weekday", 1, 5, 1, 0, "last", "2026-03-30")]
    [TestCase("weekday", 1, -1, 1, 0, "skip", "2026-01-26,2026-02-23,2026-03-30")]
    [TestCase("end", 1, 1, 1, 2, "skip", "2026-01-29,2026-02-26,2026-03-29")]
    public void MonthlyRulesHandleMissingDates(string mode, int day, int ordinal, int weekday, int offset, string missing, string expected)
    {
        var rule = new RepeatRule { Kind = "monthly", MonthMode = mode, MonthDay = day, Ordinal = ordinal, Weekday = weekday, EndOffset = offset, Missing = missing };
        Assert.That(Dates(rule, "2026-01-01", "2026-03-31"), Is.EqualTo(expected.Split(',')));
    }

    /// <summary>年の起点を維持し、非うるう年は指定した方法で扱う</summary>
    [Test]
    public void AnnualLeapDayAndIntervalPreserveAnchor()
    {
        var rule = new RepeatRule { Kind = "yearly", Month = 2, MonthDay = 29, Interval = 2, Missing = "last" };
        Assert.That(Dates(rule, "2024-03-01", "2030-12-31"), Is.EqualTo(new[] { "2026-02-28", "2028-02-29", "2030-02-28" }));
    }

    /// <summary>複数の元予定が同じ営業日に移っても合併しない</summary>
    [Test]
    public void HolidayMovesKeepDistinctOccurrencesAndCountAfterSkipping()
    {
        var start = new DateOnly(2026, 11, 21);
        var next = new RepeatRule { Adjustment = "next", AvoidWeekends = true, AvoidHolidays = true, Until = start.AddDays(2) };
        var moved = RepeatCalculator.Generate(next, start, start, start.AddDays(10), Known).ToArray();
        Assert.That(moved.Select(x => x.Due), Is.All.EqualTo(new DateOnly(2026, 11, 24)));
        Assert.That(moved.Select(x => TaskSchedule.Key("segment", x.Scheduled)).Distinct().Count(), Is.EqualTo(3));

        var skip = new RepeatRule { Adjustment = "skip", AvoidWeekends = true, AvoidHolidays = true, Count = 2 };
        var counted = RepeatCalculator.Generate(skip, start, start, start.AddDays(10), Known).Where(x => x.Due != null).ToArray();
        Assert.That(counted.Select(x => x.Due), Is.EqualTo(new[] { new DateOnly(2026, 11, 24), new DateOnly(2026, 11, 25) }));
    }

    /// <summary>未収録の年で祝日なしと推定せず、祝日不要のルールは継続する</summary>
    [Test]
    public void UnknownHolidaysWaitAndDoNotGuessCount()
    {
        var start = new DateOnly(2031, 1, 1);
        var dependent = new RepeatRule { Adjustment = "skip", AvoidHolidays = true, Count = 3 };
        var waiting = RepeatCalculator.Generate(dependent, start, start, start.AddDays(10), Known).ToArray();
        Assert.That(waiting, Has.Length.EqualTo(1));
        Assert.That(waiting[0].Waiting, Is.True);
        Assert.That(waiting[0].Due, Is.Null);

        var independent = new RepeatRule { Adjustment = "skip", AvoidWeekends = true, Count = 3 };
        Assert.That(RepeatCalculator.Generate(independent, start, start, start.AddDays(10), Known).Count(x => x.Due != null), Is.EqualTo(3));
    }

    /// <summary>既に到来した自動スキップも、祝日訂正で終了回数に算入し直さない</summary>
    [Test]
    public void FrozenSkippedDateDoesNotConsumeCountAfterHolidayCorrection()
    {
        var start = new DateOnly(2026, 11, 3);
        var rule = new RepeatRule { Adjustment = "skip", AvoidHolidays = true, Count = 1 };
        var frozen = new Dictionary<DateOnly, ScheduledTask> { [start] = new(start, null, false) };
        var changed = new HolidayCalendar(2026, 2026, new HashSet<DateOnly>());
        var dates = RepeatCalculator.Generate(rule, start, start, start.AddDays(5), changed, frozen).Where(x => x.Due != null).ToArray();
        Assert.That(dates.Single().Due, Is.EqualTo(start.AddDays(1)));
    }

    /// <summary>暦の両端でも加減算が範囲外へ出ない</summary>
    [Test]
    public void CalendarLimitsDoNotOverflow()
    {
        var rule = new RepeatRule { Kind = "weekly", Weekdays = 127 };
        Assert.That(RepeatCalculator.Generate(rule, DateOnly.MinValue, DateOnly.MinValue, DateOnly.MinValue, Known).Count(), Is.EqualTo(1));
        Assert.That(RepeatCalculator.Generate(rule, DateOnly.MaxValue, DateOnly.MaxValue, DateOnly.MaxValue, Known).Count(), Is.EqualTo(1));
        Assert.That(TaskSchedule.Offset(DateOnly.MaxValue, 366), Is.EqualTo(DateOnly.MaxValue));
        Assert.That(TaskSchedule.Offset(DateOnly.MinValue, -366), Is.EqualTo(DateOnly.MinValue));
    }

    /// <summary>期待する期限を読みやすい固定書式へ揃える</summary>
    private static string[] Dates(RepeatRule rule, string start, string end)
    {
        var begin = DateOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture);
        return RepeatCalculator.Generate(rule, begin, begin, DateOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture), Known)
            .Select(x => x.Due?.ToString("yyyy-MM-dd") ?? "skip").ToArray();
    }
}
