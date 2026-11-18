using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hibitsuzuri.Web.Tasks;

namespace Hibitsuzuri.Tests;

/// <summary>個別編集と共通名の表示、入力項目ごとのエラー案内を確認する</summary>
public sealed class TaskPresentationTests
{
    /// <summary>将来の個別編集が残っていても、新しい共通設定の名前を系列見出しに使う</summary>
    [TestCase(0)]
    [TestCase(1)]
    public async Task FollowingGroupUsesCommonTitleInsteadOfLaterException(int offset)
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);
        var today = DateOnly.FromDateTime(app.Clock.GetLocalNow().DateTime);
        var created = await TaskTests.SaveAsync(client, new TaskChange
        {
            NotebookId = book,
            Title = "元の共通名",
            StartDate = today,
            Rule = new RepeatRule { Count = 10 },
            OperationId = Guid.NewGuid().ToString()
        });
        var series = created.GetProperty("seriesId").GetString();
        var page = await TaskTests.ReadAsync(client, $"/api/tasks?seriesId={series}");
        var rows = page.GetProperty("items").Deserialize<TaskRow[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        await TaskTests.SaveAsync(client, new TaskChange
        {
            Id = rows[9].Id,
            Version = rows[9].Version,
            Title = "末尾の個別編集",
            DueDate = today.AddDays(9),
            OperationId = Guid.NewGuid().ToString()
        });
        var detail = await TaskTests.ReadAsync(client, "/api/tasks/item?id=" + rows[offset].Id);
        await TaskTests.SaveAsync(client, new TaskChange
        {
            Id = rows[offset].Id,
            Version = rows[offset].Version,
            SeriesVersion = detail.GetProperty("seriesVersion").GetString() ?? "",
            Scope = "following",
            Title = "新しい共通名",
            StartDate = today.AddDays(offset),
            Rule = new RepeatRule { Count = 3 },
            OperationId = Guid.NewGuid().ToString()
        });

        var overview = await TaskTests.ReadAsync(client, "/api/tasks");
        Assert.That(overview.GetProperty("groups")[0].GetProperty("title").GetString(), Is.EqualTo("新しい共通名"));
        var remaining = await TaskTests.ReadAsync(client, $"/api/tasks?seriesId={series}");
        Assert.That(remaining.GetProperty("items").EnumerateArray().Count(x => x.GetProperty("title").GetString() == "末尾の個別編集"), Is.EqualTo(1));
    }

    /// <summary>プレビューと保存が、同じ具体的な入力エラーを返す</summary>
    [TestCase("weekday", "繰り返す曜日を1つ以上選択してください。")]
    [TestCase("until", "終了日は開始日以降の日付を指定してください。")]
    [TestCase("count", "繰り返しの回数は1～10,000で入力してください。")]
    public async Task PreviewAndSaveIdentifyInvalidCondition(string condition, string message)
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);
        var today = DateOnly.FromDateTime(app.Clock.GetLocalNow().DateTime);
        var rule = new RepeatRule
        {
            Kind = "weekly",
            Weekdays = condition == "weekday" ? 0 : 2,
            Until = condition == "until" ? today.AddDays(-1) : null,
            Count = condition == "count" ? 0 : null
        };
        var change = new TaskChange { NotebookId = book, Title = "入力検証", StartDate = today, Rule = rule, OperationId = Guid.NewGuid().ToString() };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tasks/preview") { Content = JsonContent.Create(change) };
        request.Headers.Add("RequestVerificationToken", await client.GetStringAsync("/fixture/token"));
        using var preview = await client.SendAsync(request);
        using var saved = await TaskTests.SendAsync(client, change);

        foreach (var response in new[] { preview, saved })
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.That(body.GetProperty("message").GetString(), Is.EqualTo(message));
        }
    }
}
