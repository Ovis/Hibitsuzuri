using System.Net;
using System.Text.Json;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>日記保存・履歴・並行操作・再送を実SQLiteとHTTPで確認する</summary>
public sealed class DiaryTests
{
    /// <summary>明示保存だけで作成し、本文が変わった場合だけ過去5版を残す</summary>
    [Test]
    public async Task ExplicitSavesKeepOnlyFiveChangedRevisions()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);

        using var read = await client.GetAsync($"/diary/{book}/2020-05-10");
        using var edit = await client.GetAsync($"/diary/{book}/2020-05-10/edit");

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(await db.DiaryEntries.CountAsync(), Is.Zero);

        var version = "";
        for (var i = 0; i < 8; i++)
        {
            version = await DiaryClient.SaveVersionAsync(client, book, $"本文{i}", version);
        }

        var unchanged = await DiaryClient.SaveVersionAsync(client, book, "本文7", version);
        var revisions = await db.DiaryRevisions.OrderBy(x => x.Sequence).ToListAsync();
        var editor = await client.GetStringAsync($"/diary/{book}/2020-05-10/edit");

        Assert.Multiple(() =>
        {
            Assert.That(unchanged, Is.EqualTo(version));
            Assert.That(revisions.Select(x => x.Content), Is.EqualTo(new[] { "本文2", "本文3", "本文4", "本文5", "本文6" }));
            Assert.That(editor, Does.Contain("revision-" + revisions[0].Id));
        });

        // 履歴を表示しただけでは変わらず、読み込んだ本文の明示保存で初めて現行版になる。
        Assert.That(await db.DiaryEntries.Select(x => x.Content).SingleAsync(), Is.EqualTo("本文7"));

        await DiaryClient.SaveVersionAsync(client, book, revisions[0].Content, version);

        Assert.That(await db.DiaryEntries.Select(x => x.Content).SingleAsync(), Is.EqualTo("本文2"));
        Assert.That(await db.DiaryRevisions.CountAsync(), Is.EqualTo(5));
    }

    /// <summary>同日同時作成・更新の片方だけを採用し、競合応答に最新版を含める</summary>
    [Test]
    public async Task ConcurrentWritesCannotOverwriteEachOther()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);

        var responses = await Task.WhenAll(DiaryClient.SaveAsync(client, book, "タブA"), DiaryClient.SaveAsync(client, book, "タブB"));

        Assert.That(responses.Select(x => x.StatusCode), Is.EquivalentTo(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));

        var winner = responses.Single(x => x.StatusCode == HttpStatusCode.OK);
        using var saved = JsonDocument.Parse(await winner.Content.ReadAsStringAsync());
        var version = saved.RootElement.GetProperty("version").GetString() ?? "";

        using var first = await DiaryClient.SaveAsync(client, book, "最新版", version);
        using var stale = await DiaryClient.SaveAsync(client, book, "古い版からの入力", version);
        using var conflict = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stale.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(conflict.RootElement.GetProperty("latest").GetProperty("content").GetString(), Is.EqualTo("最新版"));
        });

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>成功応答の喪失・再起動・後続編集後の再送でも元の保存を二重適用しない</summary>
    [Test]
    public async Task RetrySurvivesRestartAndDoesNotUndoLaterEdits()
    {
        var first = new TestApplication { PreserveData = true };
        var operation = Guid.NewGuid().ToString();
        string book;
        string version;

        await using (first)
        {
            using var client = first.CreateBrowser();
            await AuthClient.RegisterAsync(first, client);
            book = await DiaryClient.CreateBookAsync(client);

            using var save = await DiaryClient.SaveAsync(client, book, "最初の保存", operation: operation);
            using var json = JsonDocument.Parse(await save.Content.ReadAsStringAsync());
            version = json.RootElement.GetProperty("version").GetString() ?? "";

            await DiaryClient.SaveVersionAsync(client, book, "後の保存", version);
        }

        await using var second = new TestApplication(first.DataPath);
        using var browser = second.CreateBrowser();
        using var login = await AuthClient.LoginAsync(browser);

        using var retry = await DiaryClient.SaveAsync(browser, book, "最初の保存", operation: operation);
        using var mismatch = await DiaryClient.SaveAsync(browser, book, "内容をすり替え", operation: operation);
        using var result = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());

        await using var scope = second.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Multiple(() =>
        {
            Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(result.RootElement.GetProperty("version").GetString(), Is.EqualTo(version));
            Assert.That(result.RootElement.GetProperty("replayed").GetBoolean(), Is.True);
            Assert.That(mismatch.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        });
        Assert.That(await db.DiaryEntries.Select(x => x.Content).SingleAsync(), Is.EqualTo("後の保存"));
        Assert.That(await db.DiaryRevisions.CountAsync(), Is.EqualTo(1));
    }

    /// <summary>管理者も本文・履歴・保存・ゴミ箱の所有境界を越えられない</summary>
    [Test]
    public async Task AdministratorCannotAccessAnotherOwnersDiary()
    {
        await using var app = new TestApplication();
        using var admin = app.CreateBrowser();
        using var member = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, admin);
        await AuthClient.RegisterAsync(app, member, "member", false);
        var book = await DiaryClient.CreateBookAsync(member);
        var version = await DiaryClient.SaveVersionAsync(member, book, "秘密の本文");
        await DiaryClient.SaveVersionAsync(member, book, "新しい秘密", version);

        foreach (var path in new[] { $"/diary/{book}/2020-05-10", $"/diary/{book}/2020-05-10/edit", $"/diary/{book}/trash" })
        {
            using var read = await admin.GetAsync(path);
            Assert.That(read.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(await read.Content.ReadAsStringAsync(), Does.Not.Contain("秘密"));
        }

        using var save = await DiaryClient.SaveAsync(admin, book, "他人の書込み");
        using var trash = await AuthClient.PostAsync(admin, $"/diary/{book}/2020-05-10/trash", new()
        {
            ["action"] = "delete",
            ["version"] = version
        });
        using var empty = await AuthClient.PostAsync(admin, $"/diary/{book}/trash/empty");

        Assert.Multiple(() =>
        {
            Assert.That(save.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(trash.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(empty.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    /// <summary>Unicode文字数の境界、空白、未来日とCSRFを保存前に検証する</summary>
    [Test]
    public async Task ValidationRejectsEmptyFutureOversizeAndMissingCsrf()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);

        foreach (var invalid in new[] { " \r\n\t", new string('あ', 100001), "本文\0" })
        {
            using var response = await DiaryClient.SaveAsync(client, book, invalid);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        var future = DateOnly.FromDateTime(app.Clock.GetLocalNow().DateTime).AddDays(1).ToString("yyyy-MM-dd");
        using var futureSave = await DiaryClient.SaveAsync(client, book, "未来", date: future);
        using var csrf = await client.PostAsync($"/api/diary/{book}/2020-05-10/save", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["content"] = "CSRFなし",
            ["operationId"] = Guid.NewGuid().ToString()
        }));

        Assert.That(futureSave.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(csrf.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var maximum = string.Concat(Enumerable.Repeat("😀", 100000));
        await DiaryClient.SaveVersionAsync(client, book, maximum);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(await db.DiaryEntries.Select(x => x.Content).SingleAsync(), Is.EqualTo(maximum));
    }

    /// <summary>ログイン期限を過ぎた保存は401となり、DBに入力を残さない</summary>
    [Test]
    public async Task ExpiredSessionCannotSave()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);

        app.Clock.Advance(TimeSpan.FromHours(8));
        using var response = await DiaryClient.SaveAsync(client, book, "期限切れの入力");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        await using var scope = app.Services.CreateAsyncScope();
        Assert.That(await scope.ServiceProvider.GetRequiredService<AppDbContext>().DiaryEntries.CountAsync(), Is.Zero);
    }

    /// <summary>ブラウザが送信時に追加するCRを文字数へ含めず、改行形式だけで履歴を増やさない</summary>
    [Test]
    public async Task FormLineEndingsShareEditorCharacterLimitAndVersion()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);
        var content = new string('あ', 99999) + "\n";

        var version = await DiaryClient.SaveVersionAsync(client, book, content.Replace("\n", "\r\n", StringComparison.Ordinal));
        var unchanged = await DiaryClient.SaveVersionAsync(client, book, content, version);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(unchanged, Is.EqualTo(version));
        Assert.That(await db.DiaryEntries.Select(x => x.Content).SingleAsync(), Is.EqualTo(content));
        Assert.That(await db.DiaryRevisions.CountAsync(), Is.Zero);
    }
}
