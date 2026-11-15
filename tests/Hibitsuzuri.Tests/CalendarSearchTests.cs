using System.Net;
using Hibitsuzuri.Web.Calendar;
using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>検索・暦・記念日の表示と所有者境界を検証する</summary>
public sealed class CalendarSearchTests
{
    /// <summary>検索導入前の日記を初回起動で索引化し、原文と履歴を変更しない</summary>
    [Test]
    public async Task StartupBackfillsExistingDiaryText()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        var version = await DiaryClient.SaveVersionAsync(client, book, "## 昔のＡＢＣ\n[本文](https://hidden.example)");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.DiaryEntries.ExecuteUpdateAsync(update => update.SetProperty(x => x.SearchText, (string?)null).SetProperty(x => x.PlainText, ""));

        await DiarySearch.BackfillAsync(db, CancellationToken.None);
        var entry = await db.DiaryEntries.SingleAsync();
        Assert.That(entry.SearchText, Does.Contain("昔のabc").And.Not.Contain("hidden.example"));
        Assert.That(entry.Content, Does.Contain("https://hidden.example"));
        Assert.That(entry.Version, Is.EqualTo(version));
        Assert.That(await db.DiaryRevisions.CountAsync(), Is.Zero);
    }

    /// <summary>リンク先・記号を除き、英数字だけを正規化して安全に強調する</summary>
    [Test]
    public void SearchUsesVisibleTextAndPreservesKanaDistinctions()
    {
        var text = new DiaryMarkdown().PlainText("# 花 ＡｂＣ１２３\n[表示](https://secret.example) **強調** ひらがな カタカナ");

        Assert.That(text, Does.Contain("表示").And.Not.Contain("secret.example").And.Not.Contain("**"));
        Assert.That(DiarySearch.Normalize(text), Does.Contain("abc123").And.Contain("ひらがな カタカナ"));
        Assert.That(DiarySearch.Snippet("<script>ＡｂＣ</script>", "abc"), Is.EqualTo("&lt;script&gt;<mark>ＡｂＣ</mark>&lt;/script&gt;"));
    }

    /// <summary>非うるう年の振替は元の2月29日を変更しない</summary>
    [TestCase("skip", null)]
    [TestCase("feb28", "2023-02-28")]
    [TestCase("mar1", "2023-03-01")]
    public void LeapAnniversaryUsesExplicitPolicy(string policy, string? expected)
    {
        var anniversary = new Anniversary { Month = 2, Day = 29, LeapDayPolicy = policy };

        Assert.That(anniversary.InYear(2023)?.ToString("yyyy-MM-dd"), Is.EqualTo(expected));
        Assert.That(anniversary.InYear(2024), Is.EqualTo(new DateOnly(2024, 2, 29)));
        Assert.That(anniversary.Day, Is.EqualTo(29));
    }

    /// <summary>日付範囲は両端を含み、日本語1文字と全日記帳の検索でも他人とゴミ箱を除く</summary>
    [Test]
    public async Task SearchAndYearsRespectDateScopeAndTrash()
    {
        await using var app = new TestApplication();
        using var owner = app.CreateBrowser();
        using var other = app.CreateBrowser();
        await AuthClient.RegisterAsync(owner);
        await AuthClient.RegisterAsync(other, "other", false);
        var book = await DiaryClient.CreateBookAsync(owner, "一冊目");
        var second = await DiaryClient.CreateBookAsync(owner, "二冊目");
        var foreign = await DiaryClient.CreateBookAsync(other, "他人用");
        await DiaryClient.SaveVersionAsync(owner, book, "花 ＡＢＣ１２", date: "2001-05-10");
        await DiaryClient.SaveVersionAsync(owner, book, "花 現在", date: "2020-05-10");
        await DiaryClient.SaveVersionAsync(owner, second, "花 別冊", date: "2020-05-11");
        await DiaryClient.SaveVersionAsync(other, foreign, "花 秘密", date: "2020-05-10");
        var removed = await DiaryClient.SaveVersionAsync(owner, book, "花 削除", date: "2020-05-12");
        using var trash = await AuthClient.PostAsync(owner, $"/diary/{book}/2020-05-12/trash", new()
        {
            ["version"] = removed,
            ["action"] = "delete"
        });

        var search = WebUtility.HtmlDecode(await owner.GetStringAsync("/search?keyword=花&bookId=&start=2020-05-10&end=2020-05-12"));
        var width = WebUtility.HtmlDecode(await owner.GetStringAsync("/search?keyword=abc12"));
        var years = WebUtility.HtmlDecode(await owner.GetStringAsync($"/diary/{book}/2020-05-10/years"));
        Assert.That(search, Does.Contain("現在").And.Contain("別冊").And.Not.Contain("秘密").And.Not.Contain("花 削除").And.Not.Contain("ＡＢＣ１２"));
        Assert.That(width, Does.Contain("<mark>ＡＢＣ１２</mark>"));
        Assert.That(years, Does.Contain("2001年5月10日").And.Contain("2020年5月10日").And.Not.Contain("秘密").And.Not.Contain("別冊"));

        var calendar = WebUtility.HtmlDecode(await owner.GetStringAsync($"/calendar?bookId={book}&month=2020-05"));
        Assert.That(calendar, Does.Contain("花 現在").And.Not.Contain("花 削除").And.Not.Contain("花 秘密").And.Not.Contain("花 別冊"));

        foreach (var path in new[] { $"/calendar?bookId={foreign}", $"/search?keyword=花&bookId={foreign}", $"/diary/{foreign}/2020-05-10/years" })
        {
            using var response = await owner.GetAsync(path);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
    }

    /// <summary>選択先と閏日振替を月表示にも適用し、祝日の未収録を区別する</summary>
    [Test]
    public async Task AnniversaryScopesAndCalendarCoverageAreEnforced()
    {
        await using var app = new TestApplication();
        using var owner = app.CreateBrowser();
        using var other = app.CreateBrowser();
        await AuthClient.RegisterAsync(owner);
        await AuthClient.RegisterAsync(other, "other", false);
        var book = await DiaryClient.CreateBookAsync(owner);
        var second = await DiaryClient.CreateBookAsync(owner, "別冊");
        var foreign = await DiaryClient.CreateBookAsync(other);
        var values = new Dictionary<string, string>
        {
            ["title"] = "閏日の記念",
            ["month"] = "2",
            ["day"] = "29",
            ["leapDayPolicy"] = "mar1",
            ["allNotebooks"] = "false",
            ["bookIds"] = book
        };
        using var save = await AuthClient.PostAsync(owner, "/anniversaries", values);
        Assert.That(save.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        values["bookIds"] = foreign;
        using var invalid = await AuthClient.PostAsync(owner, "/anniversaries", values);
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var item = await db.Anniversaries.SingleAsync();
        var reader = scope.ServiceProvider.GetRequiredService<CalendarReader>();
        var month = await reader.ReadAsync(item.UserId, book, new DateOnly(2023, 3, 1));
        var unselected = await reader.ReadAsync(item.UserId, second, new DateOnly(2023, 3, 1));
        Assert.That(month?.Anniversaries[new DateOnly(2023, 3, 1)], Is.EqualTo(new[] { "閏日の記念" }));
        Assert.That(unselected?.Anniversaries.Count, Is.Zero);
        Assert.That(month?.Holidays[new DateOnly(2023, 3, 21)], Is.EqualTo("春分の日"));
        var unknown = WebUtility.HtmlDecode(await owner.GetStringAsync($"/calendar?bookId={book}&month=2030-01"));
        Assert.That(unknown, Does.Contain("未収録"));

        using var stale = await AuthClient.PostAsync(owner, $"/anniversaries/{item.Id}/delete", new()
        {
            ["version"] = "stale"
        });
        using var denied = await AuthClient.PostAsync(other, $"/anniversaries/{item.Id}/delete", new()
        {
            ["version"] = item.Version
        });
        using var deleted = await AuthClient.PostAsync(owner, $"/anniversaries/{item.Id}/delete", new()
        {
            ["version"] = item.Version
        });
        Assert.That(stale.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(await db.Anniversaries.CountAsync(), Is.Zero);
    }
}
