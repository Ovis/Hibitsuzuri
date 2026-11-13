using System.Net;
using Hibitsuzuri.Web.Calendar;
using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Images;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>日記帳のアーカイブ・削除・復元と関連データの保持境界</summary>
public sealed class NotebookLifecycleTests
{
    /// <summary>削除中は閲覧・検索・通知から除外し、復元しても過去の通知を送り直さない</summary>
    [Test]
    public async Task DeletedNotebookIsHiddenAndRestoreRequiresUniqueName()
    {
        var sender = new RecordingPush();
        await using var app = NotificationTests.Application(sender);
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        await DiaryClient.SaveVersionAsync(client, book, "削除中に見えない本文");
        await NotificationTests.EnableAsync(client);
        await NotificationTests.AddTaskAsync(app, book, TimeSpan.FromMinutes(1));

        using var archive = await ChangeAsync(app, client, book, "archive");
        Assert.That(archive.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        using var archivedRead = await client.GetAsync($"/diary/{book}/2020-05-10");
        Assert.That(archivedRead.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var delete = await ChangeAsync(app, client, book, "delete");
        Assert.That(delete.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        using var hidden = await client.GetAsync($"/diary/{book}/2020-05-10");
        Assert.That(hidden.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var search = WebUtility.HtmlDecode(await client.GetStringAsync("/search?bookId=&keyword=見えない"));
        Assert.That(search, Does.Not.Contain("削除中に見えない本文"));

        app.Clock.Advance(TimeSpan.FromMinutes(2));
        await NotificationTests.RunAsync(app);
        Assert.That(sender.Messages, Is.Empty);
        await DiaryClient.CreateBookAsync(client);
        using var collision = await ChangeAsync(app, client, book, "restore", "日記");
        Assert.That(collision.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        using var restored = await ChangeAsync(app, client, book, "restore", "復元した日記帳");
        Assert.That(restored.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        await NotificationTests.RunAsync(app);
        Assert.That(sender.Messages, Is.Empty);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Notebooks.SingleAsync(x => x.Id == book);
        Assert.That(row.Archived, Is.True);
        Assert.That(row.TasksPausedOn, Is.Null);
        Assert.That(await db.DiaryEntries.CountAsync(x => x.NotebookId == book), Is.EqualTo(1));
    }

    /// <summary>完全削除は専用記念日だけを消し、他の日記帳の本文やテンプレートの画像を保持する</summary>
    [Test]
    public async Task PurgePreservesSharedImagesAndAnniversaries()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        var deleted = await DiaryClient.CreateBookAsync(client, "完全削除する日記帳");
        var retained = await DiaryClient.CreateBookAsync(client, "残す日記帳");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = await db.Notebooks.Where(x => x.Id == deleted).Select(x => x.UserId).SingleAsync();
        var image = new DiaryImage { NotebookId = deleted, UserId = owner, Name = "共有画像", MediaType = "image/png" };
        var only = new Anniversary { UserId = owner, Title = "専用", Month = 1, Day = 1, AllNotebooks = false };
        only.Notebooks.Add(new()
        {
            AnniversaryId = only.Id,
            NotebookId = deleted
        });
        var shared = new Anniversary { UserId = owner, Title = "共有", Month = 1, Day = 2, AllNotebooks = false };
        shared.Notebooks.AddRange([new() { AnniversaryId = shared.Id, NotebookId = deleted }, new() { AnniversaryId = shared.Id, NotebookId = retained }]);
        db.Anniversaries.AddRange(only, shared, new Anniversary { UserId = owner, Title = "全日記帳", Month = 1, Day = 3 });
        db.DiaryImages.Add(image);
        await db.SaveChangesAsync();
        await db.Notebooks.Where(x => x.Id == retained).ExecuteUpdateAsync(set => set.SetProperty(x => x.Template, $"![共有](attachment:{image.Id})"));
        var imagePath = scope.ServiceProvider.GetRequiredService<ImageStore>().PathFor(image.Id);
        await File.WriteAllBytesAsync(imagePath, [1]);

        using var delete = await ChangeAsync(app, client, deleted, "delete");
        Assert.That(delete.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/books"));
        Assert.That(html, Does.Contain("同時に削除する専用の記念日：1 件"));
        using var staleCount = await ChangeAsync(app, client, deleted, "purge", "完全削除する日記帳", 0);
        Assert.That(staleCount.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        using var wrongName = await ChangeAsync(app, client, deleted, "purge", "間違い", 1);
        Assert.That(wrongName.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using var purge = await ChangeAsync(app, client, deleted, "purge", "完全削除する日記帳", 1);
        Assert.That(purge.StatusCode, Is.EqualTo(HttpStatusCode.Redirect), await purge.Content.ReadAsStringAsync());

        db.ChangeTracker.Clear();
        Assert.That(await db.Notebooks.IgnoreQueryFilters().AnyAsync(x => x.Id == deleted), Is.False);
        Assert.That(await db.Anniversaries.Select(x => x.Title).ToListAsync(), Is.EquivalentTo(new[] { "共有", "全日記帳" }));
        Assert.That(await db.Set<AnniversaryNotebook>().CountAsync(), Is.EqualTo(1));
        Assert.That(await db.DiaryImages.Select(x => x.NotebookId).SingleAsync(), Is.EqualTo(retained));
        await scope.ServiceProvider.GetRequiredService<ImageStore>().CleanupAsync(CancellationToken.None);
        Assert.That(File.Exists(imagePath), Is.True);
        Assert.That((await scope.ServiceProvider.GetRequiredService<ImageStore>().AvailableAsync(owner)).Select(x => x.Id), Does.Contain(image.Id));
    }

    /// <summary>別所有者・未確認・古い版からの日記帳操作を拒否する</summary>
    [Test]
    public async Task OwnershipConfirmationAndVersionAreRequired()
    {
        await using var app = new TestApplication();
        using var owner = app.CreateBrowser();
        using var other = app.CreateBrowser();
        await AuthClient.RegisterAsync(owner);
        await AuthClient.RegisterAsync(other, "other", false);
        var book = await DiaryClient.CreateBookAsync(owner);
        using var foreign = await ChangeAsync(app, other, book, "delete");
        Assert.That(foreign.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using var unconfirmed = await AuthClient.PostAsync(owner, $"/books/{book}/state", new()
        {
            ["operation"] = "delete"
        });
        Assert.That(unconfirmed.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using var stale = await AuthClient.PostAsync(owner, $"/books/{book}/state", new()
        {
            ["operation"] = "delete",
            ["confirmed"] = "true",
            ["version"] = "old"
        });
        Assert.That(stale.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    /// <summary>画面に相当する最新の版と明示確認を送信する</summary>
    private static async Task<HttpResponseMessage> ChangeAsync(TestApplication app, HttpClient client, string book, string operation, string name = "", int count = 0)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var version = await db.Notebooks.IgnoreQueryFilters().Where(x => x.Id == book).Select(x => x.Version).SingleAsync();
        return await AuthClient.PostAsync(client, $"/books/{book}/state", new()
        {
            ["operation"] = operation,
            ["version"] = version,
            ["confirmed"] = "true",
            ["name"] = name,
            ["anniversaryCount"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
    }
}
