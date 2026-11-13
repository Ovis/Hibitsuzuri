using System.Net;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>共通ナビゲーションの所有者境界と画面移動時の選択保持を確認する</summary>
public sealed class NavigationTests
{
    /// <summary>他人と削除済みの日記帳を出さず、アーカイブは開いている場合だけ切替欄に残す</summary>
    [Test]
    public async Task BookSwitchOnlyShowsAvailableOwnedBooks()
    {
        await using var app = new TestApplication();
        using var owner = app.CreateBrowser();
        using var other = app.CreateBrowser();
        await AuthClient.RegisterAsync(owner);
        await AuthClient.RegisterAsync(other, "other", false);
        await DiaryClient.CreateBookAsync(owner, "available-book");
        var archived = await DiaryClient.CreateBookAsync(owner, "archived-book");
        var deleted = await DiaryClient.CreateBookAsync(owner, "deleted-book");
        await DiaryClient.CreateBookAsync(other, "other-private-book");

        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.Notebooks.Where(x => x.Id == archived).ExecuteUpdateAsync(set => set.SetProperty(x => x.Archived, true));
        await database.Notebooks.Where(x => x.Id == deleted).ExecuteUpdateAsync(set => set.SetProperty(x => x.DeletedAt, 1L));

        var settings = await owner.GetStringAsync("/settings");
        var archivePage = await owner.GetStringAsync($"/diary/{archived}/2020-05-10");
        var otherSettings = await other.GetStringAsync("/settings");

        Assert.Multiple(() =>
        {
            Assert.That(settings, Does.Contain("available-book").And.Not.Contain("archived-book")
                .And.Not.Contain("deleted-book").And.Not.Contain("other-private-book"));
            Assert.That(archivePage, Does.Contain("archived-book").And.Not.Contain("deleted-book").And.Not.Contain("other-private-book"));
            Assert.That(settings, Does.Contain("/admin/users"));
            Assert.That(otherSettings, Does.Not.Contain("/admin/users").And.Not.Contain("available-book"));
        });
    }

    /// <summary>日付や検索条件を保って切り替え、外部転送と他人の日記帳選択を拒否する</summary>
    [Test]
    public async Task SwitchingKeepsSelectionAndOnlyRedirectsLocally()
    {
        await using var app = new TestApplication();
        using var owner = app.CreateBrowser();
        using var other = app.CreateBrowser();
        await AuthClient.RegisterAsync(owner);
        await AuthClient.RegisterAsync(other, "other", false);
        var book = await DiaryClient.CreateBookAsync(owner);
        var foreign = await DiaryClient.CreateBookAsync(other);
        var target = $"/calendar?month=2020-05&bookId={book}";

        using var selected = await owner.GetAsync($"/diary/{book}?returnUrl={Uri.EscapeDataString(target)}");
        using var home = await owner.GetAsync("/");
        using var external = await owner.GetAsync($"/diary/{book}?returnUrl=https://outside.example/");
        using var forbidden = await owner.GetAsync($"/diary/{foreign}?returnUrl=/settings");

        Assert.Multiple(() =>
        {
            Assert.That(selected.Headers.Location?.ToString(), Is.EqualTo(target));
            Assert.That(home.Headers.Location?.ToString(), Does.StartWith($"/diary/{book}/"));
            Assert.That(external.Headers.Location?.ToString(), Does.StartWith($"/diary/{book}/"));
            Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    /// <summary>振り返り欄には同じ日記帳の同月同日の現存する別年だけを新しい順に2件出す</summary>
    [Test]
    public async Task MemoriesExcludeDeletedAndUnrelatedEntries()
    {
        await using var app = new TestApplication();
        using var browser = app.CreateBrowser();
        await AuthClient.RegisterAsync(browser);
        var book = await DiaryClient.CreateBookAsync(browser);
        var another = await DiaryClient.CreateBookAsync(browser, "別の日記帳");
        await DiaryClient.SaveVersionAsync(browser, book, "old-memory", date: "2019-05-10");
        await DiaryClient.SaveVersionAsync(browser, book, "first-memory", date: "2021-05-10");
        await DiaryClient.SaveVersionAsync(browser, book, "latest-memory", date: "2022-05-10");
        await DiaryClient.SaveVersionAsync(browser, book, "deleted-memory", date: "2023-05-10");
        await DiaryClient.SaveVersionAsync(browser, book, "other-day", date: "2024-05-11");
        await DiaryClient.SaveVersionAsync(browser, another, "other-book-memory", date: "2024-05-10");

        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.DiaryEntries.Where(x => x.NotebookId == book && x.EntryDate == new DateOnly(2023, 5, 10))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.DeletedAt, 1L));

        var html = await browser.GetStringAsync($"/diary/{book}/2020-05-10");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("first-memory").And.Contain("latest-memory")
                .And.Not.Contain("deleted-memory").And.Not.Contain("other-day")
                .And.Not.Contain("other-book-memory").And.Not.Contain("old-memory"));
            Assert.That(html.IndexOf("latest-memory", StringComparison.Ordinal), Is.LessThan(html.IndexOf("first-memory", StringComparison.Ordinal)));
        });
    }
}
