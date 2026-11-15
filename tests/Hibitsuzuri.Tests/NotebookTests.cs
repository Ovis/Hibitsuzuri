using System.Net;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>日記帳の所有境界と、設定変更による意図しない上書きを検証する</summary>
public sealed class NotebookTests
{
    /// <summary>同じ所有者の重複だけを拒否し、管理者にも他人の編集を許可しない</summary>
    [Test]
    public async Task NamesAndManagementAreScopedToOwner()
    {
        await using var app = new TestApplication();
        using var admin = app.CreateBrowser();
        using var member = app.CreateBrowser();
        await AuthClient.RegisterAsync(admin);
        await AuthClient.RegisterAsync(member, "member", false);
        await DiaryClient.CreateBookAsync(admin);
        var book = await DiaryClient.CreateBookAsync(member);

        using var duplicate = await AuthClient.PostAsync(member, "/books", new()
        {
            ["name"] = " 日記 "
        });
        using var read = await admin.GetAsync($"/books/{book}");
        using var edit = await AuthClient.PostAsync(admin, $"/books/{book}", new()
        {
            ["name"] = "横取り"
        });
        using var move = await AuthClient.PostAsync(admin, $"/books/{book}/move", new()
        {
            ["direction"] = "-1"
        });

        Assert.Multiple(() =>
        {
            Assert.That(duplicate.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(read.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(edit.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(move.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    /// <summary>テンプレートと並び順を永続化し、古い設定画面からの上書きを拒否する</summary>
    [Test]
    public async Task TemplateConflictAndOrderPreserveLatestSettings()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        var first = await DiaryClient.CreateBookAsync(client, "生活");
        var second = await DiaryClient.CreateBookAsync(client, "仕事");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var version = await db.Notebooks.Where(x => x.Id == first).Select(x => x.Version).SingleAsync();

        using var saved = await AuthClient.PostAsync(client, $"/books/{first}", new()
        {
            ["name"] = "暮らし",
            ["template"] = "## 今日の記録\n",
            ["version"] = version
        });
        using var conflict = await AuthClient.PostAsync(client, $"/books/{first}", new()
        {
            ["name"] = "古い名前",
            ["template"] = "古い入力",
            ["version"] = version
        });
        using var moved = await AuthClient.PostAsync(client, $"/books/{second}/move", new()
        {
            ["direction"] = "-1"
        });
        var books = await db.Notebooks.OrderBy(x => x.Position).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(saved.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(conflict.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(moved.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(books.Select(x => x.Id), Is.EqualTo(new[] { second, first }));
            Assert.That(books[1].Template, Is.EqualTo("## 今日の記録\n"));
            Assert.That(books[1].Name, Is.EqualTo("暮らし"));
        });
    }

    /// <summary>通常一覧の並べ替えはアーカイブを飛び越し、アーカイブ内の順序を変えない</summary>
    [Test]
    public async Task MoveKeepsArchivedNotebookPositions()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        var first = await DiaryClient.CreateBookAsync(client, "生活");
        var archived = await DiaryClient.CreateBookAsync(client, "以前の記録");
        var last = await DiaryClient.CreateBookAsync(client, "仕事");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var book = await db.Notebooks.SingleAsync(x => x.Id == archived);
        book.Archived = true;
        await db.SaveChangesAsync();
        var archivedPosition = book.Position;

        using var moved = await AuthClient.PostAsync(client, $"/books/{last}/move", new()
        {
            ["direction"] = "-1"
        });
        var books = await db.Notebooks.AsNoTracking().OrderBy(x => x.Position).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(moved.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(books.Select(x => x.Id), Is.EqualTo(new[] { last, archived, first }));
            Assert.That(books.Single(x => x.Id == archived).Position, Is.EqualTo(archivedPosition));
        });
    }

    /// <summary>選択記憶はブラウザとユーザーで分け、無効な選択は先頭に戻す</summary>
    [Test]
    public async Task LastNotebookIsLocalToBrowserAndUser()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        using var otherBrowser = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        var first = await DiaryClient.CreateBookAsync(client, "生活");
        var second = await DiaryClient.CreateBookAsync(client, "仕事");

        using var selected = await client.GetAsync($"/diary/{second}/2020-05-10");
        using var home = await client.GetAsync("/");

        using var login = await AuthClient.LoginAsync(otherBrowser);
        using var otherHome = await otherBrowser.GetAsync("/");

        using var logout = await AuthClient.PostAsync(client, "/auth/logout");
        await AuthClient.RegisterAsync(client, "member", false);
        using var memberHome = await client.GetAsync("/");

        Assert.Multiple(() =>
        {
            Assert.That(home.Headers.Location?.ToString(), Does.StartWith($"/diary/{second}/"));
            Assert.That(otherHome.Headers.Location?.ToString(), Does.StartWith($"/diary/{first}/"));
            Assert.That(memberHome.Headers.Location?.ToString(), Is.EqualTo("/books"));
        });
    }
}
