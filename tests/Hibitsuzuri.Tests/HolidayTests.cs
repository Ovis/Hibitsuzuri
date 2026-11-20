using System.Net;
using System.Text;
using System.Text.Json;
using Hibitsuzuri.Web.Calendar;
using Hibitsuzuri.Web.Storage;
using Hibitsuzuri.Web.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>公式取得先の解決、初期値、訂正、取得失敗、管理者認可を確認する</summary>
public sealed class HolidayTests
{
    /// <summary>祝日訂正は未来の未操作だけへ反映し、未表示だった過去や操作済みの回を動かさない</summary>
    [Test]
    public async Task CorrectionsRecalculateOnlyUnoperatedFutureTasks()
    {
        var csv = "";
        await using var app = new TestApplication
        {
            ServicesOverride = services => services.AddHttpClient<HolidaySource>()
                .ConfigurePrimaryHttpMessageHandler(() => new HolidayHandler(request => request.RequestUri?.AbsoluteUri == HolidaySource.MetadataUrl
                    ? Metadata("https://www8.cao.go.jp/new.csv") : Csv(csv)))
        };

        // 訂正前の期日が同梱の祝日で繰り越されないよう、祝日のない期間を使う。
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);
        var today = DateOnly.FromDateTime(app.Clock.GetLocalNow().DateTime);
        var creation = new TaskChange
        {
            NotebookId = book,
            Title = "訂正を検証",
            StartDate = today.AddDays(-2),
            Rule = new RepeatRule { Adjustment = "next", AvoidHolidays = true, Count = 10 },
            OperationId = Guid.NewGuid().ToString()
        };
        var saved = await TaskTests.SaveAsync(client, creation);
        var series = saved.GetProperty("seriesId").GetString();
        var list = await TaskTests.ReadAsync(client, $"/api/tasks?seriesId={series}");
        var rows = list.GetProperty("items").Deserialize<TaskRow[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        var edited = rows.Single(x => x.ScheduledDate == today.AddDays(2));
        var completed = rows.Single(x => x.ScheduledDate == today.AddDays(3));
        await TaskTests.SaveAsync(client, new TaskChange { Id = edited.Id, Version = edited.Version, Title = "個別に固定", DueDate = today.AddDays(2), OperationId = Guid.NewGuid().ToString() });
        await TaskTests.SaveAsync(client, new TaskChange { Id = completed.Id, Version = completed.Version, Action = "complete", Completed = true, OperationId = Guid.NewGuid().ToString() });
        creation.OperationId = Guid.NewGuid().ToString();
        creation.Title = "一度も表示していない系列";
        var unseen = await TaskTests.SaveAsync(client, creation);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var holidays = await db.Holidays.AsNoTracking().ToDictionaryAsync(x => x.Date, x => x.Name);
        foreach (var offset in new[] { -1, 1, 2, 3 })
        {
            holidays[today.AddDays(offset)] = "訂正した休日";
        }

        csv = "国民の祝日・休日月日,国民の祝日・休日名称\n" + string.Join("\n", holidays.OrderBy(x => x.Key).Select(x => $"{x.Key:yyyy/M/d},{x.Value}"));
        using var updated = await AuthClient.PostAsync(client, "/admin/holidays/update");
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That((await db.HolidayStates.AsNoTracking().SingleAsync()).Error, Is.Null);

        list = await TaskTests.ReadAsync(client, $"/api/tasks?seriesId={series}");
        rows = list.GetProperty("items").Deserialize<TaskRow[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        Assert.That(rows.Single(x => x.ScheduledDate == today.AddDays(-1)).DueDate, Is.EqualTo(today.AddDays(-1)));
        Assert.That(rows.Single(x => x.ScheduledDate == today.AddDays(1)).DueDate, Is.EqualTo(today.AddDays(4)));
        Assert.That(rows.Single(x => x.Id == edited.Id).DueDate, Is.EqualTo(today.AddDays(2)));
        var history = await TaskTests.ReadAsync(client, $"/api/tasks?mode=completed&seriesId={series}");
        Assert.That(history.GetProperty("items")[0].GetProperty("dueDate").GetString(), Is.EqualTo(today.AddDays(3).ToString("yyyy-MM-dd")));
        var cold = await TaskTests.ReadAsync(client, "/api/tasks?seriesId=" + unseen.GetProperty("seriesId").GetString());
        var coldRows = cold.GetProperty("items").Deserialize<TaskRow[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        Assert.That(coldRows.Single(x => x.ScheduledDate == today.AddDays(-1)).DueDate, Is.EqualTo(today.AddDays(-1)));
    }

    /// <summary>同梱値で初回利用でき、既存の訂正を再初期化で戻さない</summary>
    [Test]
    public async Task SeedWorksOfflineAndDoesNotOverwriteExistingData()
    {
        await using var app = new TestApplication();
        using var browser = app.CreateBrowser();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var state = await db.HolidayStates.SingleAsync();
        var equinox = await db.Holidays.FindAsync(new DateOnly(2027, 3, 21));

        Assert.That(state.FirstYear, Is.EqualTo(1955));
        Assert.That(state.LastYear, Is.EqualTo(2027));
        Assert.That(state.LastSuccessAt, Is.Null);
        Assert.That(equinox?.Name, Is.EqualTo("春分の日"));

        await db.Holidays.Where(x => x.Date == new DateOnly(2027, 3, 21)).ExecuteUpdateAsync(set => set.SetProperty(x => x.Name, "訂正を保持"));
        await HolidayUpdates.SeedAsync(db, CancellationToken.None);

        Assert.That(await db.Holidays.Where(x => x.Date == new DateOnly(2027, 3, 21)).Select(x => x.Name).SingleAsync(), Is.EqualTo("訂正を保持"));
    }

    /// <summary>CSVの場所はメタデータから解決し、公式ホスト以外へ接続しない</summary>
    [Test]
    public async Task SourceResolvesMetadataAndRejectsUntrustedUrl()
    {
        var requests = new List<string>();
        var csvUrl = "https://www8.cao.go.jp/moved/holidays.csv";
        using var handler = new HolidayHandler(request =>
        {
            requests.Add(request.RequestUri?.AbsoluteUri ?? "");
            return request.RequestUri?.AbsoluteUri == HolidaySource.MetadataUrl ? Metadata(csvUrl) : Csv(YearCsv());
        });
        using var client = new HttpClient(handler);
        var source = new HolidaySource(client);

        var result = await source.FetchAsync(CancellationToken.None);

        Assert.That(result.Url, Is.EqualTo(csvUrl));
        Assert.That(result.Holidays, Has.Count.EqualTo(10));
        Assert.That(requests, Is.EqualTo(new[] { HolidaySource.MetadataUrl, csvUrl }));

        csvUrl = "https://127.0.0.1/private";
        requests.Clear();

        Assert.ThrowsAsync<InvalidDataException>(async () => await source.FetchAsync(CancellationToken.None));
        Assert.That(requests, Is.EqualTo(new[] { HolidaySource.MetadataUrl }));
    }

    /// <summary>重複・不正な日付・欠落した年を部分採用しない</summary>
    [TestCase("duplicate")]
    [TestCase("date")]
    [TestCase("name")]
    [TestCase("partial")]
    public void InvalidCsvIsRejectedAsAWhole(string failure)
    {
        var csv = YearCsv();
        csv = failure switch
        {
            "duplicate" => csv + "2027/1/1,重複\n",
            "date" => csv.Replace("2027/1/1", "2027/2/30", StringComparison.Ordinal),
            "name" => csv.Replace("元日", "", StringComparison.Ordinal),
            _ => csv.Replace("2027/11/23,勤労感謝の日\n", "", StringComparison.Ordinal)
        };

        Assert.Throws<InvalidDataException>(() => HolidaySource.Parse(csv));
    }

    /// <summary>訂正を承認待ちなしで反映し、次の失敗では成功日時と保存値を維持する</summary>
    [Test]
    public async Task ManualUpdateAppliesCorrectionsAndKeepsDataOnFailure()
    {
        var csv = "";
        await using var app = new TestApplication
        {
            ServicesOverride = services => services.AddHttpClient<HolidaySource>()
                .ConfigurePrimaryHttpMessageHandler(() => new HolidayHandler(request =>
                    request.RequestUri?.AbsoluteUri == HolidaySource.MetadataUrl
                        ? Metadata("https://www8.cao.go.jp/new.csv") : Csv(csv)))
        };
        using var admin = app.CreateBrowser();
        using var member = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, admin);
        await AuthClient.RegisterAsync(app, member, "member", false);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.Holidays.AsNoTracking().OrderBy(x => x.Date).ToListAsync();
        csv = "国民の祝日・休日月日,国民の祝日・休日名称\n" + string.Join("\n", rows.Select(x => $"{x.Date:yyyy/M/d},{x.Name}"));
        csv = csv.Replace("2027/1/1,元日", "2027/1/1,訂正後の名称", StringComparison.Ordinal);

        using var rejected = await AuthClient.PostAsync(member, "/admin/holidays/update");
        using var updated = await AuthClient.PostAsync(admin, "/admin/holidays/update");
        var success = await db.HolidayStates.AsNoTracking().SingleAsync();

        Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(success.LastSuccessAt, Is.Not.Null);
        Assert.That(success.Error, Is.Null);
        Assert.That(await db.Holidays.Where(x => x.Date == new DateOnly(2027, 1, 1)).Select(x => x.Name).SingleAsync(), Is.EqualTo("訂正後の名称"));

        csv = YearCsv();
        using var failed = await AuthClient.PostAsync(admin, "/admin/holidays/update");
        var failure = await db.HolidayStates.AsNoTracking().SingleAsync();

        Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(failure.LastSuccessAt, Is.EqualTo(success.LastSuccessAt));
        Assert.That(failure.Error, Does.Contain("欠落"));
        Assert.That(await db.Holidays.CountAsync(), Is.EqualTo(rows.Count));
        Assert.That(await admin.GetStringAsync("/admin/holidays"), Does.Contain("保存済みデータは維持"));
    }

    /// <summary>失敗した取得も日次で制限し、翌日の境界で再試行する</summary>
    [Test]
    public async Task AutomaticAttemptsAreLimitedToOncePerDay()
    {
        var requests = 0;
        await using var app = new TestApplication
        {
            ServicesOverride = services => services.AddHttpClient<HolidaySource>()
                .ConfigurePrimaryHttpMessageHandler(() => new HolidayHandler(_ =>
                {
                    requests++;
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }))
        };
        using var browser = app.CreateBrowser();
        await using var scope = app.Services.CreateAsyncScope();
        var updates = scope.ServiceProvider.GetRequiredService<HolidayUpdates>();

        await updates.UpdateAsync(null, CancellationToken.None);
        app.Clock.Advance(TimeSpan.FromDays(1) - TimeSpan.FromMilliseconds(1));
        await updates.UpdateAsync(null, CancellationToken.None);

        Assert.That(requests, Is.EqualTo(1));

        app.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await updates.UpdateAsync(null, CancellationToken.None);

        Assert.That(requests, Is.EqualTo(2));
    }

    /// <summary>検証可能な通年データを用意する</summary>
    private static string YearCsv() => "国民の祝日・休日月日,国民の祝日・休日名称\n"
        + "2027/1/1,元日\n2027/1/11,成人の日\n2027/2/11,建国記念の日\n2027/2/23,天皇誕生日\n"
        + "2027/3/21,春分の日\n2027/4/29,昭和の日\n2027/5/3,憲法記念日\n2027/5/4,みどりの日\n"
        + "2027/5/5,こどもの日\n2027/11/23,勤労感謝の日\n";

    /// <summary>本物と同じメタデータ構造で取得先を返す</summary>
    private static HttpResponseMessage Metadata(string url) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            success = true,
            result = new
            {
                resources = new[] { new { format = "CSV", url } }
            }
        }))
    };

    /// <summary>公式配布と同じShift-JISの応答</summary>
    private static HttpResponseMessage Csv(string text)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.GetEncoding(932).GetBytes(text)) };
    }

    /// <summary>外部通信を発生させず、取得先と応答境界を試験する</summary>
    private sealed class HolidayHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        /// <summary>指定された試験応答を返す</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
