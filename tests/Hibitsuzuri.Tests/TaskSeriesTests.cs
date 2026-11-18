using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Hibitsuzuri.Web.Storage;
using Hibitsuzuri.Web.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>系列変更・変換・繰り返し件数の保存規則と索引の再利用を検証する</summary>
public sealed class TaskSeriesTests
{
    /// <summary>表示範囲より前で回数が未確定だった系列も、収録後に保留表示を解消する</summary>
    [Test]
    public async Task NewlyCoveredYearClearsWaitingOutsideTheRequestedMonth()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);
        await TaskTests.SaveAsync(client, new TaskChange
        {
            NotebookId = book,
            Title = "1回だけの保留",
            StartDate = new DateOnly(2035, 1, 1),
            Rule = new RepeatRule { Count = 1, Adjustment = "skip", AvoidHolidays = true },
            OperationId = Guid.NewGuid().ToString()
        });
        var waiting = await TaskTests.ReadAsync(client, $"/api/tasks?bookId={book}&day=2036-01-01");
        Assert.That(waiting.GetProperty("waiting").GetArrayLength(), Is.EqualTo(1));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var state = await db.HolidayStates.SingleAsync();
        state.LastYear = 2036;
        state.LastSuccessAt = app.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        await db.SaveChangesAsync();

        var refreshed = await TaskTests.ReadAsync(client, $"/api/tasks?bookId={book}&day=2036-01-01");
        Assert.That(refreshed.GetProperty("waiting").GetArrayLength(), Is.Zero);
        var first = await TaskTests.ReadAsync(client, $"/api/tasks?bookId={book}&day=2035-01-01");
        Assert.That(first.GetProperty("total").GetInt32(), Is.EqualTo(1));
    }

    /// <summary>通常タスクを繰り返しへ変換しても、最初の回へ完了日時を引き継ぐ</summary>
    [Test]
    public async Task CompletedNormalTaskKeepsItsTimestampWhenConvertedToRepeating()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);
        var today = DateOnly.FromDateTime(app.Clock.GetLocalNow().DateTime);
        var saved = await TaskTests.SaveAsync(client, new TaskChange { NotebookId = book, Title = "一度だけ", OperationId = Guid.NewGuid().ToString() });
        var completed = await TaskTests.SaveAsync(client, new TaskChange
        {
            Id = saved.GetProperty("id").GetString(),
            Version = saved.GetProperty("version").GetString() ?? "",
            Action = "complete",
            Completed = true,
            OperationId = Guid.NewGuid().ToString()
        });
        var before = await TaskTests.ReadAsync(client, "/api/tasks/item?id=" + saved.GetProperty("id").GetString());
        var converted = await TaskTests.SaveAsync(client, new TaskChange
        {
            Id = completed.GetProperty("id").GetString(),
            Version = completed.GetProperty("version").GetString() ?? "",
            Title = "繰り返しへ変換",
            StartDate = today,
            Rule = new RepeatRule { Count = 3 },
            OperationId = Guid.NewGuid().ToString()
        });
        var pending = await TaskTests.ReadAsync(client, "/api/tasks?seriesId=" + converted.GetProperty("seriesId").GetString());
        var history = await TaskTests.ReadAsync(client, "/api/tasks?mode=completed");
        Assert.That(pending.GetProperty("total").GetInt32(), Is.EqualTo(2));
        Assert.That(history.GetProperty("total").GetInt32(), Is.EqualTo(1));
        Assert.That(history.GetProperty("items")[0].GetProperty("completedAt").GetInt64(), Is.EqualTo(before.GetProperty("item").GetProperty("completedAt").GetInt64()));
        Assert.That(history.GetProperty("groups")[0].GetProperty("count").GetInt32(), Is.EqualTo(1));
    }

    /// <summary>確認後の別操作を競合として返し、再確認後も編集・完了・手動スキップを削除しない</summary>
    [Test]
    public async Task FollowingDeletionRechecksSeriesAndKeepsProtectedRecords()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);
        var today = DateOnly.FromDateTime(app.Clock.GetLocalNow().DateTime);
        var saved = await TaskTests.SaveAsync(client, new TaskChange { NotebookId = book, Title = "系列", StartDate = today, Rule = new RepeatRule { Count = 5 }, OperationId = Guid.NewGuid().ToString() });
        var series = saved.GetProperty("seriesId").GetString();
        var list = await TaskTests.ReadAsync(client, $"/api/tasks?seriesId={series}");
        var rows = list.GetProperty("items").Deserialize<TaskRow[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        var detail = await TaskTests.ReadAsync(client, "/api/tasks/item?id=" + rows[0].Id);
        await TaskTests.SaveAsync(client, new TaskChange { Id = rows[1].Id, Version = rows[1].Version, Action = "complete", Completed = true, OperationId = Guid.NewGuid().ToString() });
        await TaskTests.SaveAsync(client, new TaskChange { Id = rows[2].Id, Version = rows[2].Version, Title = "期限なしへ個別変更", OperationId = Guid.NewGuid().ToString() });
        var skip = new TaskChange { Id = rows[3].Id, Version = rows[3].Version, Action = "delete", OperationId = Guid.NewGuid().ToString() };
        await TaskTests.SaveAsync(client, skip);
        await TaskTests.SaveAsync(client, skip);
        var deletion = new TaskChange
        {
            Id = rows[0].Id,
            Version = rows[0].Version,
            SeriesVersion = detail.GetProperty("seriesVersion").GetString() ?? "",
            Action = "delete",
            Scope = "following",
            OperationId = Guid.NewGuid().ToString()
        };
        using var stale = await TaskTests.SendAsync(client, deletion);
        Assert.That(stale.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        detail = await TaskTests.ReadAsync(client, "/api/tasks/item?id=" + rows[0].Id);
        Assert.That(detail.GetProperty("retained").GetInt32(), Is.EqualTo(2));
        deletion.SeriesVersion = detail.GetProperty("seriesVersion").GetString() ?? "";
        await TaskTests.SaveAsync(client, deletion);
        await TaskTests.SaveAsync(client, deletion);
        list = await TaskTests.ReadAsync(client, $"/api/tasks?seriesId={series}&kind=undated");
        Assert.That(list.GetProperty("total").GetInt32(), Is.EqualTo(1));
        var history = await TaskTests.ReadAsync(client, $"/api/tasks?seriesId={series}&mode=completed");
        Assert.That(history.GetProperty("total").GetInt32(), Is.EqualTo(1));
    }

    /// <summary>手動スキップは回数に含め、祝日訂正による自動スキップと混同しない</summary>
    [Test]
    public async Task ManualSkipStillConsumesCountAfterHolidayChange()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);
        var today = DateOnly.FromDateTime(app.Clock.GetLocalNow().DateTime);
        var begin = today.AddDays(1);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Holidays.Where(x => x.Date >= begin && x.Date <= begin.AddDays(10)).ExecuteDeleteAsync();
        var saved = await TaskTests.SaveAsync(client, new TaskChange
        {
            NotebookId = book,
            Title = "2回だけ",
            StartDate = begin,
            Rule = new RepeatRule { Count = 2, Adjustment = "skip", AvoidHolidays = true },
            OperationId = Guid.NewGuid().ToString()
        });
        var series = saved.GetProperty("seriesId").GetString();
        var list = await TaskTests.ReadAsync(client, $"/api/tasks?seriesId={series}");
        var first = list.GetProperty("items")[0];
        await TaskTests.SaveAsync(client, new TaskChange
        {
            Id = first.GetProperty("id").GetString(),
            Version = first.GetProperty("version").GetString() ?? "",
            Action = "delete",
            OperationId = Guid.NewGuid().ToString()
        });
        db.Holidays.Add(new Hibitsuzuri.Web.Calendar.Holiday { Date = begin, Name = "訂正した休日" });
        var state = await db.HolidayStates.SingleAsync();
        state.LastSuccessAt = app.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        await db.SaveChangesAsync();

        list = await TaskTests.ReadAsync(client, $"/api/tasks?seriesId={series}");
        Assert.That(list.GetProperty("total").GetInt32(), Is.EqualTo(1));
        Assert.That(list.GetProperty("items")[0].GetProperty("dueDate").GetString(), Is.EqualTo(begin.AddDays(1).ToString("yyyy-MM-dd")));
    }

    /// <summary>100年分を集計しても返却は50件に留め、再表示で予定を作り直さない</summary>
    [Test]
    public async Task LongHistoryReusesItsIndexAndOnlyReturnsOnePage()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);
        var today = DateOnly.FromDateTime(app.Clock.GetLocalNow().DateTime);
        var begin = today.AddYears(-100);
        var saved = await TaskTests.SaveAsync(client, new TaskChange { NotebookId = book, Title = "長期の系列", StartDate = begin, Rule = new RepeatRule(), OperationId = Guid.NewGuid().ToString() });
        var series = saved.GetProperty("seriesId").GetString();
        var watch = Stopwatch.StartNew();
        var first = await TaskTests.ReadAsync(client, $"/api/tasks?seriesId={series}");
        var cold = watch.ElapsedMilliseconds;
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var slots = await db.TaskSlots.CountAsync();
        var version = await db.TaskSlots.OrderBy(x => x.ScheduledDate).Select(x => x.Version).FirstAsync();
        watch.Restart();
        var warm = await TaskTests.ReadAsync(client, $"/api/tasks?seriesId={series}&page=2");

        TestContext.Out.WriteLine($"100年分の初回 {cold}ms / 再表示 {watch.ElapsedMilliseconds}ms / 索引 {slots}件");
        Assert.That(first.GetProperty("total").GetInt32(), Is.EqualTo(today.DayNumber - begin.DayNumber + 31));
        Assert.That(warm.GetProperty("items").GetArrayLength(), Is.EqualTo(50));
        Assert.That(await db.TaskSlots.CountAsync(), Is.EqualTo(slots));
        Assert.That(await db.TaskSlots.OrderBy(x => x.ScheduledDate).Select(x => x.Version).FirstAsync(), Is.EqualTo(version));
        Assert.That(await db.TaskRecords.CountAsync(), Is.Zero);
    }
}
