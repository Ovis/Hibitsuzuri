using System.Net;
using System.Text.Json;
using Hibitsuzuri.Web.Calendar;
using Hibitsuzuri.Web.Notifications;
using Hibitsuzuri.Web.Storage;
using Hibitsuzuri.Web.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>繰り返し、操作の取り消し、再起動にまたがる通知設定の保持</summary>
public sealed class NotificationScheduleTests
{
    /// <summary>段階5の既存タスクと系列を移行しても、空文字を通知JSONとして読み込まない</summary>
    [Test]
    public async Task ExistingTasksMigrateWithEmptyReminderLists()
    {
        await using var app = NotificationTests.Application(new RecordingPush());
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        await TaskTests.SaveAsync(client, new TaskChange { NotebookId = book, Title = "従来のタスク", OperationId = Guid.NewGuid().ToString() });
        await TaskTests.SaveAsync(client, new TaskChange
        {
            NotebookId = book,
            Title = "従来の系列",
            StartDate = DateOnly.FromDateTime(app.Clock.GetLocalNow().DateTime),
            Rule = new RepeatRule { Count = 2 },
            OperationId = Guid.NewGuid().ToString()
        });
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = db.GetService<IMigrator>();

        // この試験専用DBだけを旧スキーマへ戻し、実際のアップグレードを通す。
        await migrator.MigrateAsync("20261113045349_Tasks");
        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

        Assert.That(await db.TaskRecords.Select(x => x.ReminderJson).SingleAsync(), Is.EqualTo("[]"));
        Assert.That(await db.TaskSegments.Select(x => x.ReminderJson).SingleAsync(), Is.EqualTo("[]"));
        Assert.That((await TaskTests.ReadAsync(client, "/api/tasks?bookId=" + book)).GetProperty("matched").GetInt32(), Is.EqualTo(3));
    }

    /// <summary>期限なしに個別変更した繰り返しの回も、絶対日時の通知対象に含める</summary>
    [Test]
    public async Task UndatedOccurrenceUsesAbsoluteReminder()
    {
        var sender = new RecordingPush();
        await using var app = NotificationTests.Application(sender);
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        await NotificationTests.EnableAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        var today = DateOnly.FromDateTime(app.Clock.GetLocalNow().DateTime);
        await TaskTests.SaveAsync(client, new TaskChange
        {
            NotebookId = book,
            Title = "期限の変更",
            StartDate = today,
            Rule = new RepeatRule { Count = 1 },
            OperationId = Guid.NewGuid().ToString()
        });
        var list = await TaskTests.ReadAsync(client, "/api/tasks?bookId=" + book + "&day=" + today.ToString("yyyy-MM-dd"));
        var row = list.GetProperty("items")[0];
        await TaskTests.SaveAsync(client, new TaskChange
        {
            Id = row.GetProperty("id").GetString(),
            Version = row.GetProperty("version").GetString() ?? "",
            Title = "期限なしの今回",
            Reminders = [new Reminder { Kind = "absolute", At = app.Clock.GetLocalNow().AddMinutes(2).ToString("yyyy-MM-ddTHH:mm") }],
            OperationId = Guid.NewGuid().ToString()
        });

        app.Clock.Advance(TimeSpan.FromMinutes(2));
        await NotificationTests.RunAsync(app);
        Assert.That(sender.Messages.Count, Is.EqualTo(1));
    }

    /// <summary>個別の通知なし指定は共通設定を変えず、初回の実際の期限で未来時刻を検証する</summary>
    [Test]
    public async Task OccurrenceCanDisableCommonReminderAndWeekdayUsesActualFirstDate()
    {
        await using var app = NotificationTests.Application(new RecordingPush());
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        var today = DateOnly.FromDateTime(app.Clock.GetLocalNow().DateTime);
        var first = today.AddDays(2);
        var created = await TaskTests.SaveAsync(client, new TaskChange
        {
            NotebookId = book,
            Title = "週の共通通知",
            StartDate = today,
            Rule = new RepeatRule { Kind = "weekly", Weekdays = 1 << (int)first.DayOfWeek, Count = 2 },
            Reminders = [new Reminder { Time = "00:00" }],
            OperationId = Guid.NewGuid().ToString()
        });
        var list = await TaskTests.ReadAsync(client, "/api/tasks?bookId=" + book + "&seriesId=" + created.GetProperty("seriesId").GetString());
        var row = list.GetProperty("items")[0];
        Assert.That(row.GetProperty("dueDate").GetString(), Is.EqualTo(first.ToString("yyyy-MM-dd")));
        await TaskTests.SaveAsync(client, new TaskChange
        {
            Id = row.GetProperty("id").GetString(),
            Version = row.GetProperty("version").GetString() ?? "",
            Title = "今回だけ通知なし",
            DueDate = first,
            Reminders = [],
            OperationId = Guid.NewGuid().ToString()
        });
        var detail = await TaskTests.ReadAsync(client, "/api/tasks/item?id=" + row.GetProperty("id").GetString());
        Assert.That(detail.GetProperty("reminders").GetArrayLength(), Is.Zero);
        Assert.That(detail.GetProperty("commonReminders").GetArrayLength(), Is.EqualTo(1));
    }

    /// <summary>完了を戻した後は、未送信かつ取り消し操作より未来の通知だけを再開する</summary>
    [Test]
    public async Task UndoDoesNotCatchUpButKeepsFutureReminder()
    {
        var sender = new RecordingPush();
        await using var app = NotificationTests.Application(sender);
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        await NotificationTests.EnableAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        var local = app.Clock.GetLocalNow();
        var saved = await TaskTests.SaveAsync(client, new TaskChange
        {
            NotebookId = book,
            Title = "完了取消",
            Reminders =
            [
                new Reminder { Kind = "absolute", At = local.AddMinutes(2).ToString("yyyy-MM-ddTHH:mm") },
                new Reminder { Kind = "absolute", At = local.AddMinutes(10).ToString("yyyy-MM-ddTHH:mm") }
            ],
            OperationId = Guid.NewGuid().ToString()
        });
        var completed = await TaskTests.SaveAsync(client, new TaskChange
        {
            Id = saved.GetProperty("id").GetString(),
            Version = saved.GetProperty("version").GetString() ?? "",
            Action = "complete",
            Completed = true,
            OperationId = Guid.NewGuid().ToString()
        });
        app.Clock.Advance(TimeSpan.FromMinutes(3));
        await TaskTests.SaveAsync(client, new TaskChange
        {
            Id = completed.GetProperty("id").GetString(),
            Version = completed.GetProperty("version").GetString() ?? "",
            Action = "complete",
            Completed = false,
            OperationId = Guid.NewGuid().ToString()
        });
        await NotificationTests.RunAsync(app);
        Assert.That(sender.Messages, Is.Empty);

        app.Clock.Advance(TimeSpan.FromMinutes(7));
        await NotificationTests.RunAsync(app);
        Assert.That(sender.Messages.Count, Is.EqualTo(1));
    }

    /// <summary>期限より先に通知時刻が来た回は、祝日訂正でも日付を動かさない</summary>
    [Test]
    public async Task HolidayCorrectionFreezesOccurrencesWhoseReminderHasArrived()
    {
        await using var app = NotificationTests.Application(new RecordingPush());
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        var begin = DateOnly.FromDateTime(app.Clock.GetLocalNow().DateTime).AddDays(2);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Holidays.Where(x => x.Date >= begin && x.Date <= begin.AddDays(3)).ExecuteDeleteAsync();
        await TaskTests.SaveAsync(client, new TaskChange
        {
            NotebookId = book,
            Title = "休日訂正",
            StartDate = begin,
            Rule = new RepeatRule { Count = 2, Adjustment = "next", AvoidHolidays = true },
            Reminders = [new Reminder { DaysBefore = 1, Time = "00:00" }],
            OperationId = Guid.NewGuid().ToString()
        });
        await TaskTests.ReadAsync(client, "/api/tasks?bookId=" + book);
        app.Clock.Advance(TimeSpan.FromDays(1));
        using var login = await AuthClient.LoginAsync(client);
        db.Holidays.Add(new Holiday { Date = begin, Name = "訂正した休日" });
        var state = await db.HolidayStates.SingleAsync();
        state.LastSuccessAt = app.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        await db.SaveChangesAsync();

        var refreshed = await TaskTests.ReadAsync(client, "/api/tasks?bookId=" + book + "&day=" + begin.ToString("yyyy-MM-dd"));
        Assert.That(refreshed.GetProperty("items")[0].GetProperty("dueDate").GetString(), Is.EqualTo(begin.ToString("yyyy-MM-dd")));
    }

    /// <summary>未来の訂正で通知が過去へ移動した場合、遅延許容時間内でも自動配信しない</summary>
    [Test]
    public async Task HolidayCorrectionIntoPastRequiresReview()
    {
        var sender = new RecordingPush();
        await using var app = NotificationTests.Application(sender);
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        await NotificationTests.EnableAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        var tomorrow = DateOnly.FromDateTime(app.Clock.GetLocalNow().DateTime).AddDays(1);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Holidays.Where(x => x.Date >= tomorrow && x.Date <= tomorrow.AddDays(3)).ExecuteDeleteAsync();
        var time = app.Clock.GetLocalNow().AddMinutes(-1).ToString("HH:mm");
        await TaskTests.SaveAsync(client, new TaskChange
        {
            NotebookId = book,
            Title = "通知見直し",
            StartDate = tomorrow.AddDays(1),
            Rule = new RepeatRule { Count = 1, Adjustment = "previous", AvoidHolidays = true },
            Reminders = [new Reminder { DaysBefore = 1, Time = time }],
            OperationId = Guid.NewGuid().ToString()
        });
        await TaskTests.ReadAsync(client, "/api/tasks?bookId=" + book);
        db.Holidays.Add(new Holiday { Date = tomorrow.AddDays(1), Name = "新たな休日" });
        var state = await db.HolidayStates.SingleAsync();
        state.LastSuccessAt = app.Clock.GetUtcNow().AddSeconds(1).ToUnixTimeMilliseconds();
        await db.SaveChangesAsync();
        var refreshed = await TaskTests.ReadAsync(client, "/api/tasks?bookId=" + book + "&day=" + tomorrow.ToString("yyyy-MM-dd"));
        Assert.That(refreshed.GetProperty("items")[0].GetProperty("notificationAfter").GetInt64(), Is.GreaterThan(0));

        await NotificationTests.RunAsync(app);
        Assert.That(sender.Messages, Is.Empty);
    }

    /// <summary>再起動してもVAPID鍵を変えず、秘密鍵の平文は保存しない</summary>
    [Test]
    public async Task VapidKeySurvivesRestart()
    {
        string path;
        string key;
        await using (var app = NotificationTests.Application(new RecordingPush()))
        {
            app.PreserveData = true;
            using var client = app.CreateBrowser();
            await AuthClient.RegisterAsync(client);
            key = (await TaskTests.ReadAsync(client, "/api/push/device")).GetProperty("publicKey").GetString() ?? "";
            path = app.DataPath;
            var keys = app.Services.GetRequiredService<PushKeys>();
            Assert.That(await File.ReadAllTextAsync(Path.Combine(path, "vapid.json")), Does.Not.Contain(keys.Private));
        }

        await using var restarted = new TestApplication(path);
        using var browser = restarted.CreateBrowser();
        using var login = await AuthClient.LoginAsync(browser);
        Assert.That((await TaskTests.ReadAsync(browser, "/api/push/device")).GetProperty("publicKey").GetString(), Is.EqualTo(key));
    }
}
