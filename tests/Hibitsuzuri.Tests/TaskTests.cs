using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hibitsuzuri.Web.Storage;
using Hibitsuzuri.Web.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>認証・CSRF・SQLiteを通してタスクの操作と系列の保持を検証する</summary>
public sealed class TaskTests
{
    /// <summary>予定プレビューは休日調整と未収録を示し、予定や通知を保存しない</summary>
    [TestCase("2026-11-14", false, "2026-11-16", false)]
    [TestCase("2035-01-01", true, null, true)]
    public async Task PreviewUsesHolidayRulesWithoutSaving(string start, bool holidays, string? expected, bool waiting)
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tasks/preview")
        {
            Content = JsonContent.Create(new TaskChange
            {
                StartDate = DateOnly.Parse(start),
                Rule = new RepeatRule { Adjustment = "next", AvoidWeekends = true, AvoidHolidays = holidays, Count = 5 }
            })
        };
        request.Headers.Add("RequestVerificationToken", await client.GetStringAsync("/fixture/token"));

        using var response = await client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var first = result.GetProperty("items")[0];
        Assert.That(first.GetProperty("due").GetString(), Is.EqualTo(expected));
        Assert.That(first.GetProperty("waiting").GetBoolean(), Is.EqualTo(waiting));

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(await db.TaskSeries.CountAsync(), Is.Zero);
        Assert.That(await db.TaskSlots.CountAsync(), Is.Zero);
        Assert.That(await db.TaskRecords.CountAsync(), Is.Zero);
    }

    /// <summary>不正なクエリや欠けたIDを500エラーにしない</summary>
    [TestCase("/api/tasks?query=")]
    [TestCase("/api/tasks?mode=unknown")]
    [TestCase("/api/tasks?through=invalid")]
    [TestCase("/api/tasks/item")]
    public async Task InvalidRequestsReturnValidationErrors(string path)
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);

        using var response = await client.GetAsync(path);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>完了した期間は期限ではなく完了日時で判定し、50件の境界と再完了を保持する</summary>
    [Test]
    public async Task CompletionHistoryUsesInclusiveLocalDatesAndStablePaging()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);
        var today = DateOnly.FromDateTime(app.Clock.GetLocalNow().DateTime);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var i = 0; i < 55; i++)
        {
            db.TaskRecords.Add(new TaskRecord { NotebookId = book, Title = "完了 " + i, CompletedAt = app.Clock.GetUtcNow().ToUnixTimeMilliseconds() });
        }

        db.TaskRecords.Add(new TaskRecord { NotebookId = book, Title = "31日前", CompletedAt = app.Clock.GetUtcNow().AddDays(-31).ToUnixTimeMilliseconds() });
        await db.SaveChangesAsync();
        var first = await ReadAsync(client, "/api/tasks?mode=completed");
        var second = await ReadAsync(client, "/api/tasks?mode=completed&page=2");
        Assert.That(first.GetProperty("total").GetInt32(), Is.EqualTo(55));
        Assert.That(first.GetProperty("items").GetArrayLength(), Is.EqualTo(50));
        Assert.That(second.GetProperty("items").GetArrayLength(), Is.EqualTo(5));
        Assert.That(first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Select(x => x.GetProperty("id").GetString()).Distinct().Count(), Is.EqualTo(55));
        var all = await ReadAsync(client, "/api/tasks?mode=completed&period=all");
        Assert.That(all.GetProperty("total").GetInt32(), Is.EqualTo(56));
        var range = await ReadAsync(client, $"/api/tasks?mode=completed&period=custom&start={today:yyyy-MM-dd}&end={today:yyyy-MM-dd}");
        Assert.That(range.GetProperty("total").GetInt32(), Is.EqualTo(55));

        var item = first.GetProperty("items")[0];
        var reset = await SaveAsync(client, new TaskChange { Id = item.GetProperty("id").GetString(), Version = item.GetProperty("version").GetString() ?? "", Action = "complete", Completed = false, OperationId = Guid.NewGuid().ToString() });
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        await SaveAsync(client, new TaskChange { Id = reset.GetProperty("id").GetString(), Version = reset.GetProperty("version").GetString() ?? "", Action = "complete", Completed = true, OperationId = Guid.NewGuid().ToString() });
        var detail = await ReadAsync(client, "/api/tasks/item?id=" + reset.GetProperty("id").GetString());
        Assert.That(detail.GetProperty("item").GetProperty("completedAt").GetInt64(), Is.GreaterThan(item.GetProperty("completedAt").GetInt64()));
    }

    /// <summary>停止中の未操作だけをスキップし、再開日から再生成する</summary>
    [Test]
    public async Task PauseKeepsEarlierAndEditedTasksWithoutCatchingUpSkippedDays()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);
        var today = DateOnly.FromDateTime(app.Clock.GetLocalNow().DateTime);
        var created = await SaveAsync(client, new TaskChange { NotebookId = book, Title = "停止対象", StartDate = today.AddDays(-1), Rule = new RepeatRule { Count = 5 }, OperationId = Guid.NewGuid().ToString() });
        var series = created.GetProperty("seriesId").GetString();
        var list = await ReadAsync(client, $"/api/tasks?seriesId={series}");
        var rows = list.GetProperty("items").Deserialize<TaskRow[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        var edited = rows.Single(x => x.ScheduledDate == today);
        await SaveAsync(client, new TaskChange { Id = edited.Id, Version = edited.Version, Title = "停止中も保持", DueDate = today, OperationId = Guid.NewGuid().ToString() });
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var version = await db.Notebooks.Where(x => x.Id == book).Select(x => x.Version).SingleAsync();
        using var pause = await AuthClient.PostAsync(client, $"/tasks/pause/{book}", new()
        {
            ["version"] = version,
            ["paused"] = "true"
        });
        Assert.That(pause.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        using var blocked = await SendAsync(client, new TaskChange { NotebookId = book, Title = "禁止", OperationId = Guid.NewGuid().ToString() });
        Assert.That(blocked.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        var pastDay = today.AddDays(-1).ToString("yyyy-MM-dd");
        var pastDiary = await client.GetStringAsync($"/diary/{book}/{pastDay}");
        Assert.Multiple(() =>
        {
            Assert.That(pastDiary, Does.Contain("この日記帳のタスクは停止中のため、閲覧のみです。"));
            Assert.That(pastDiary, Does.Not.Contain($"変更は<a href=\"/tasks?bookId={book}\">"));
            Assert.That(pastDiary, Does.Contain($"href=\"/books/{book}\""));
            Assert.That(pastDiary, Does.Contain($"href=\"/diary/{book}/{pastDay}/edit\""));
        });

        app.Clock.Advance(TimeSpan.FromDays(2));
        using var login = await AuthClient.LoginAsync(client);
        version = await db.Notebooks.Where(x => x.Id == book).Select(x => x.Version).SingleAsync();
        using var resume = await AuthClient.PostAsync(client, $"/tasks/pause/{book}", new()
        {
            ["version"] = version,
            ["paused"] = "false"
        });
        Assert.That(resume.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        list = await ReadAsync(client, $"/api/tasks?seriesId={series}");
        rows = list.GetProperty("items").Deserialize<TaskRow[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        Assert.That(rows.Select(x => x.ScheduledDate), Is.EquivalentTo(new[] { today.AddDays(-1), today, today.AddDays(2), today.AddDays(3) }));
        Assert.That(rows.All(x => !x.ReadOnly), Is.True);
    }

    /// <summary>祝日待ちのルールも設定を変更でき、未操作の予定が通常タスクへ変換される</summary>
    [Test]
    public async Task WaitingRulesCanBeEditedAndFollowingCanBecomeNormal()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);
        await SaveAsync(client, new TaskChange
        {
            NotebookId = book,
            Title = "祝日待ち",
            StartDate = new DateOnly(2035, 1, 1),
            Rule = new RepeatRule { AvoidHolidays = true, Adjustment = "skip", Count = 5 },
            OperationId = Guid.NewGuid().ToString()
        });
        var list = await ReadAsync(client, $"/api/tasks?bookId={book}&day=2035-01-01");
        var waiting = list.GetProperty("waiting")[0];
        var detail = await ReadAsync(client, "/api/tasks/item?id=" + waiting.GetProperty("id").GetString());
        Assert.That(detail.GetProperty("item").GetProperty("waiting").GetBoolean(), Is.True);
        var converted = await SaveAsync(client, new TaskChange
        {
            Id = waiting.GetProperty("id").GetString(),
            Version = detail.GetProperty("item").GetProperty("version").GetString() ?? "",
            SeriesVersion = detail.GetProperty("seriesVersion").GetString() ?? "",
            Scope = "following",
            Title = "普通のタスク",
            DueDate = new DateOnly(2035, 1, 1),
            OperationId = Guid.NewGuid().ToString()
        });
        list = await ReadAsync(client, $"/api/tasks?bookId={book}&day=2035-01-01");
        Assert.That(list.GetProperty("waiting").GetArrayLength(), Is.Zero);
        Assert.That(list.GetProperty("items")[0].GetProperty("id").GetString(), Is.EqualTo(converted.GetProperty("id").GetString()));
        Assert.That(list.GetProperty("items")[0].GetProperty("seriesId").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    /// <summary>日本語検索・期限なし・再送・版競合・完了日時・所有者境界を確認する</summary>
    [Test]
    public async Task NormalTasksPreserveCompletionAndRejectStaleOrForeignWrites()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        using var other = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        await AuthClient.RegisterAsync(app, other, "other", false);
        var book = await DiaryClient.CreateBookAsync(client);
        var create = new TaskChange { NotebookId = book, Title = "買い物 ＡＢＣ", Memo = "花を買う", OperationId = Guid.NewGuid().ToString() };
        var saved = await SaveAsync(client, create);
        var replay = await SaveAsync(client, create);
        Assert.That(replay.GetProperty("id").GetString(), Is.EqualTo(saved.GetProperty("id").GetString()));

        var list = await ReadAsync(client, "/api/tasks?query=abc&kind=undated");
        Assert.That(list.GetProperty("total").GetInt32(), Is.EqualTo(1));
        var item = list.GetProperty("items")[0].Deserialize<TaskRow>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidOperationException();
        var complete = new TaskChange { Id = item.Id, Version = item.Version, Action = "complete", Completed = true, OperationId = Guid.NewGuid().ToString() };
        using var denied = await SendAsync(other, complete);
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        await SaveAsync(client, complete);
        using var stale = await SendAsync(client, new TaskChange { Id = item.Id, Version = item.Version, Title = "古い編集", OperationId = Guid.NewGuid().ToString() });
        Assert.That(stale.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        var history = await ReadAsync(client, "/api/tasks?mode=completed");
        var finished = history.GetProperty("items")[0];
        var completedAt = finished.GetProperty("completedAt").GetInt64();
        var edited = await SaveAsync(client, new TaskChange
        {
            Id = item.Id,
            Version = finished.GetProperty("version").GetString() ?? "",
            Title = "買い物の補足",
            Memo = "変更後",
            OperationId = Guid.NewGuid().ToString()
        });
        history = await ReadAsync(client, "/api/tasks?mode=completed");
        Assert.That(history.GetProperty("items")[0].GetProperty("completedAt").GetInt64(), Is.EqualTo(completedAt));

        using var missingToken = await client.PostAsJsonAsync("/api/tasks/change", complete);
        Assert.That(missingToken.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using var past = await SendAsync(client, new TaskChange
        {
            Id = item.Id,
            Version = edited.GetProperty("version").GetString() ?? "",
            Action = "complete",
            Completed = false,
            ContextDay = new DateOnly(2020, 1, 1),
            ContextBook = book,
            OperationId = Guid.NewGuid().ToString()
        });
        Assert.That(past.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    /// <summary>ルールの分割後も完了・個別編集・手動スキップを旧区間に保持する</summary>
    [Test]
    public async Task FollowingChangeRetainsOperatedOccurrencesWithoutCopyingTheirState()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);
        var today = DateOnly.FromDateTime(app.Clock.GetLocalNow().DateTime);
        var created = await SaveAsync(client, new TaskChange
        {
            NotebookId = book,
            Title = "毎日の記録",
            StartDate = today,
            Rule = new RepeatRule { Count = 5 },
            OperationId = Guid.NewGuid().ToString()
        });
        var series = created.GetProperty("seriesId").GetString();
        var page = await ReadAsync(client, $"/api/tasks?seriesId={series}");
        var rows = page.GetProperty("items").Deserialize<TaskRow[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        Assert.That(rows, Has.Length.EqualTo(5));
        await SaveAsync(client, new TaskChange { Id = rows[1].Id, Version = rows[1].Version, Action = "complete", Completed = true, OperationId = Guid.NewGuid().ToString() });
        await SaveAsync(client, new TaskChange { Id = rows[2].Id, Version = rows[2].Version, Title = "個別編集", DueDate = today.AddDays(10), OperationId = Guid.NewGuid().ToString() });
        await SaveAsync(client, new TaskChange { Id = rows[3].Id, Version = rows[3].Version, Action = "delete", OperationId = Guid.NewGuid().ToString() });
        var detail = await ReadAsync(client, $"/api/tasks/item?id={rows[0].Id}");
        Assert.That(detail.GetProperty("retained").GetInt32(), Is.EqualTo(2));
        await SaveAsync(client, new TaskChange
        {
            Id = rows[0].Id,
            Version = rows[0].Version,
            SeriesVersion = detail.GetProperty("seriesVersion").GetString() ?? "",
            Scope = "following",
            Title = "新しい毎日",
            StartDate = today,
            Rule = new RepeatRule { Count = 5 },
            OperationId = Guid.NewGuid().ToString()
        });

        page = await ReadAsync(client, $"/api/tasks?seriesId={series}");
        Assert.That(page.GetProperty("total").GetInt32(), Is.EqualTo(6));
        var history = await ReadAsync(client, $"/api/tasks?seriesId={series}&mode=completed");
        Assert.That(history.GetProperty("total").GetInt32(), Is.EqualTo(1));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(await db.TaskRecords.CountAsync(x => x.Skipped), Is.EqualTo(1));
        Assert.That(await db.TaskSegments.CountAsync(), Is.EqualTo(2));
        Assert.That(await db.TaskSeries.CountAsync(), Is.EqualTo(1));
    }

    /// <summary>全期間の件数と50件のページを分け、日記とカレンダーにも予定を反映する</summary>
    [Test]
    public async Task RecurringOverviewCountsPastAndLoadsFiftyAtATime()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);
        var today = DateOnly.FromDateTime(app.Clock.GetLocalNow().DateTime);
        var result = await SaveAsync(client, new TaskChange
        {
            NotebookId = book,
            Title = "定期",
            StartDate = today.AddDays(-100),
            Rule = new RepeatRule(),
            OperationId = Guid.NewGuid().ToString()
        });
        var series = result.GetProperty("seriesId").GetString();
        var overview = await ReadAsync(client, "/api/tasks");
        Assert.That(overview.GetProperty("groups")[0].GetProperty("count").GetInt32(), Is.EqualTo(131));
        Assert.That(overview.GetProperty("groups")[0].GetProperty("overdue").GetInt32(), Is.EqualTo(100));
        var second = await ReadAsync(client, $"/api/tasks?seriesId={series}&page=2");
        Assert.That(second.GetProperty("items").GetArrayLength(), Is.EqualTo(50));
        var diary = await ReadAsync(client, $"/api/tasks?bookId={book}&day={today:yyyy-MM-dd}");
        Assert.That(diary.GetProperty("total").GetInt32(), Is.EqualTo(1));
        var calendar = WebUtility.HtmlDecode(await client.GetStringAsync($"/calendar?bookId={book}&month={today:yyyy-MM}"));
        Assert.That(calendar, Does.Contain("未完了タスクあり"));
    }

    /// <summary>JSON要求にも実際のCSRFトークンを付ける</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, TaskChange change)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tasks/change") { Content = JsonContent.Create(change) };
        request.Headers.Add("RequestVerificationToken", await client.GetStringAsync("/fixture/token"));
        return await client.SendAsync(request);
    }

    /// <summary>正常保存を確認して結果を呼び出し側へ渡す</summary>
    public static async Task<JsonElement> SaveAsync(HttpClient client, TaskChange change)
    {
        using var response = await SendAsync(client, change);
        var text = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), text);
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    /// <summary>APIの失敗を空の結果に置き換えずに検出する</summary>
    public static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), text);
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }
}
