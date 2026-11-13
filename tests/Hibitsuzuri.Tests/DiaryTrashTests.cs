using System.Net;
using System.Text.RegularExpressions;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>ゴミ箱内の一意性と、削除による履歴・再送記録の扱いを確認する</summary>
public sealed class DiaryTrashTests
{
    /// <summary>ゴミ箱でも日付を占有し、復元後は古い編集画面から保存させない</summary>
    [Test]
    public async Task TrashReservesDateAndRestoreChangesVersion()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        var version = await DiaryClient.SaveVersionAsync(client, book, "残す本文");

        using var deleted = await ChangeAsync(client, book, version, "delete");
        using var recreate = await DiaryClient.SaveAsync(client, book, "重複作成");
        using var oldSave = await DiaryClient.SaveAsync(client, book, "削除前の画面", version);
        using var staleRestore = await ChangeAsync(client, book, version, "restore");

        var trashVersion = await VersionAsync(app, book);
        using var restored = await ChangeAsync(client, book, trashVersion, "restore");
        using var oldDelete = await ChangeAsync(client, book, trashVersion, "delete");

        Assert.Multiple(() =>
        {
            Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(recreate.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(oldSave.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(staleRestore.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(restored.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(oldDelete.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        });
        Assert.That(await client.GetStringAsync($"/diary/{book}/2020-05-10"), Does.Contain("残す本文"));
    }

    /// <summary>完全削除は履歴も消すが、古い保存要求の再送では日記を復活させない</summary>
    [Test]
    public async Task PurgeRemovesHistoryButReplayCannotRecreateEntry()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        var operation = Guid.NewGuid().ToString();
        using var created = await DiaryClient.SaveAsync(client, book, "初版", operation: operation);
        var version = await DiaryClient.SaveVersionAsync(client, book, "現行", await VersionAsync(app, book));

        using var deleted = await ChangeAsync(client, book, version, "delete");
        using var purged = await ChangeAsync(client, book, await VersionAsync(app, book), "purge");
        using var replay = await DiaryClient.SaveAsync(client, book, "初版", operation: operation);

        Assert.That(purged.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(await db.DiaryEntries.CountAsync(), Is.Zero);
        Assert.That(await db.DiaryRevisions.CountAsync(), Is.Zero);

        await DiaryClient.SaveVersionAsync(client, book, "新しく作成");
    }

    /// <summary>空化の確認後にゴミ箱が変わったら全件を維持し、別の日記帳は削除しない</summary>
    [Test]
    public async Task EmptyTrashChecksSnapshotAndOnlyDeletesSelectedNotebook()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        var other = await DiaryClient.CreateBookAsync(client, "仕事");
        var version = await DiaryClient.SaveVersionAsync(client, book, "一件目");
        using var deleted = await ChangeAsync(client, book, version, "delete");
        var oldSnapshot = SnapshotFromHtml(await client.GetStringAsync($"/diary/{book}/trash"));

        var next = await DiaryClient.SaveVersionAsync(client, book, "二件目", date: "2020-05-11");
        using var nextDeleted = await ChangeAsync(client, book, next, "delete", "2020-05-11");

        var unrelated = await DiaryClient.SaveVersionAsync(client, other, "ほかの日記帳");
        using var unrelatedDeleted = await ChangeAsync(client, other, unrelated, "delete");

        using var stale = await AuthClient.PostAsync(client, $"/diary/{book}/trash/empty", new()
        {
            ["name"] = "日記",
            ["count"] = "1",
            ["snapshot"] = oldSnapshot
        });

        Assert.That(stale.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        var current = SnapshotFromHtml(await client.GetStringAsync($"/diary/{book}/trash"));
        using var empty = await AuthClient.PostAsync(client, $"/diary/{book}/trash/empty", new()
        {
            ["name"] = "日記",
            ["count"] = "2",
            ["snapshot"] = current
        });

        Assert.That(empty.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(await db.DiaryEntries.Select(x => x.NotebookId).ToListAsync(), Is.EqualTo(new[] { other }));
    }

    /// <summary>ユーザー削除に日記帳・日記・履歴・保存操作の削除が追従する</summary>
    [Test]
    public async Task UserDeletionCascadesAllDiaryData()
    {
        await using var app = new TestApplication();
        using var admin = app.CreateBrowser();
        using var member = app.CreateBrowser();
        await AuthClient.RegisterAsync(admin);
        await AuthClient.RegisterAsync(member, "member", false);
        var book = await DiaryClient.CreateBookAsync(member);
        var version = await DiaryClient.SaveVersionAsync(member, book, "初版");
        await DiaryClient.SaveVersionAsync(member, book, "改訂", version);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(x => x.UserName == "member");

        using var deletion = await AuthClient.PostAsync(admin, "/admin/users/change", new()
        {
            ["id"] = user.Id,
            ["operation"] = "delete",
            ["confirmed"] = "true",
            ["confirmationName"] = "member"
        });

        Assert.That(deletion.StatusCode, Is.EqualTo(HttpStatusCode.Redirect), await deletion.Content.ReadAsStringAsync());
        Assert.That(await db.Notebooks.CountAsync(), Is.Zero);
        Assert.That(await db.DiaryEntries.CountAsync(), Is.Zero);
        Assert.That(await db.DiaryRevisions.CountAsync(), Is.Zero);
        Assert.That(await db.DiarySaveOperations.CountAsync(), Is.Zero);
    }

    /// <summary>現在版を新しいDBスコープから読む</summary>
    private static async Task<string> VersionAsync(TestApplication app, string book)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().DiaryEntries
            .Where(x => x.NotebookId == book && x.EntryDate == new DateOnly(2020, 5, 10)).Select(x => x.Version).SingleAsync();
    }

    /// <summary>通常のフォームで単一日記の状態を変更する</summary>
    private static Task<HttpResponseMessage> ChangeAsync(HttpClient client, string book, string version, string action, string date = "2020-05-10")
    {
        return AuthClient.PostAsync(client, $"/diary/{book}/{date}/trash", new()
        {
            ["version"] = version,
            ["action"] = action
        });
    }

    /// <summary>確認画面に出力された対象集合のハッシュ</summary>
    private static string SnapshotFromHtml(string html) => Regex.Match(html, "name=\"snapshot\" value=\"([A-F0-9]+)\"").Groups[1].Value;
}
