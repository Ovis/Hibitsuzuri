using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hibitsuzuri.Web.Backups;
using Hibitsuzuri.Web.Calendar;
using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Images;
using Hibitsuzuri.Web.Notifications;
using Hibitsuzuri.Web.Storage;
using Hibitsuzuri.Web.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Hibitsuzuri.Tests;

/// <summary>実SQLite・実画像・HTTPで個人バックアップの移行と失敗境界を検証する</summary>
public sealed class BackupTests
{
    /// <summary>完全削除済み画像の参照を含む本文も移せるが、画像実体はバックアップから復活しない</summary>
    [Test]
    public async Task DeletedImageReferencesRoundTripWithoutRestoringImage()
    {
        await using var app = new TestApplication();
        using var source = app.CreateBrowser();
        using var target = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, source);
        await AuthClient.RegisterAsync(app, target, "target", false);
        await SeedAsync(app, source);
        string image;
        await using (var setup = app.Services.CreateAsyncScope())
        {
            image = await setup.ServiceProvider.GetRequiredService<AppDbContext>().DiaryImages.Select(x => x.Id).SingleAsync();
        }

        using var deleted = await AuthClient.PostAsync(source, $"/api/images/{image}/delete", new()
        {
            ["draftId"] = Guid.NewGuid().ToString(),
            ["content"] = "",
            ["confirmed"] = "true",
            ["permanently"] = "true",
            ["referencesConfirmed"] = "true"
        });
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var bytes = await ExportAsync(source);
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
        {
            Assert.That(zip.Entries.Any(x => x.FullName.StartsWith("assets/", StringComparison.Ordinal)), Is.False);
        }

        var transfer = await StartAsync(target);
        using var upload = await UploadAsync(target, transfer, bytes);
        Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.OK), await upload.Content.ReadAsStringAsync());
        using var restored = await ConfirmAsync(target, transfer);
        Assert.That(restored.StatusCode, Is.EqualTo(HttpStatusCode.OK), await restored.Content.ReadAsStringAsync());

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = await db.Users.Where(x => x.UserName == "target").Select(x => x.Id).SingleAsync();
        var book = await db.Notebooks.SingleAsync(x => x.UserId == owner);
        var entry = await db.DiaryEntries.SingleAsync(x => x.NotebookId == book.Id);
        Assert.That(entry.Content, Does.Contain("現在の本文").And.Contain("attachment:").And.Not.Contain(image));
        Assert.That(book.Template, Does.Contain("attachment:").And.Not.Contain(image));
        Assert.That(await db.DiaryImages.CountAsync(), Is.Zero);
        Assert.That(await scope.ServiceProvider.GetRequiredService<ImageStore>().RenderAsync(owner, entry.Content), Does.Contain("画像が見つかりません"));
    }

    /// <summary>履歴・画像・タスク・記念日を移し、認証とゴミ箱を除外し、再送で重複しない</summary>
    [Test]
    public async Task RoundTripRemapsOwnershipAndDisablesAllDevices()
    {
        await using var app = NotificationTests.Application(new RecordingPush());
        using var source = app.CreateBrowser();
        using var target = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, source);
        await AuthClient.RegisterAsync(app, target, "target", false);
        var original = await SeedAsync(app, source);
        var empty = await DiaryClient.CreateBookAsync(target, "残す空の日記帳");
        await NotificationTests.EnableAsync(target);

        var bytes = await ExportAsync(source);
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
        {
            var text = string.Join("\n", zip.Entries.Where(x => x.FullName.EndsWith(".json", StringComparison.Ordinal)).Select(ReadText));
            Assert.That(text, Does.Not.Contain("PasswordHash").And.Not.Contain("ProtectedSubscription").And.Not.Contain("ゴミ箱本文").And.Not.Contain("削除帳本文"));
            foreach (var record in zip.Entries.Where(x => x.FullName.EndsWith(".json", StringComparison.Ordinal) && x.FullName != "manifest.json"))
            {
                using var json = JsonDocument.Parse(ReadText(record));
                var names = json.RootElement.EnumerateObject().Select(x => x.Name).ToArray();
                Assert.That(names, Has.None.EqualTo("UserId"));
                Assert.That(names, Has.None.EqualTo("Version"));
                Assert.That(names, Has.None.EqualTo("PlainText"));
                Assert.That(names, Has.None.EqualTo("SearchText"));
                Assert.That(names, Has.None.EqualTo("NotificationAfter"));
                Assert.That(names, Has.None.EqualTo("RuleJson"));
                Assert.That(names, Has.None.EqualTo("ReminderJson"));
            }

            Assert.That(zip.Entries.Any(x => x.FullName.StartsWith("assets/", StringComparison.Ordinal)), Is.True);
        }

        var id = await StartAsync(target);
        using var upload = await UploadAsync(target, id, bytes);
        Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.OK), await upload.Content.ReadAsStringAsync());
        using var preview = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        Assert.That(preview.RootElement.GetProperty("problem").ValueKind, Is.EqualTo(JsonValueKind.Null));
        using var confirm = await ConfirmAsync(target, id);
        Assert.That(confirm.StatusCode, Is.EqualTo(HttpStatusCode.OK), await confirm.Content.ReadAsStringAsync());
        using var retry = await ConfirmAsync(target, id);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = await db.Users.Where(x => x.UserName == "target").Select(x => x.Id).SingleAsync();
        var books = await db.Notebooks.Where(x => x.UserId == owner).ToListAsync();
        var restored = books.Single(x => x.Name == "移行する日記帳");
        var entry = await db.DiaryEntries.SingleAsync(x => x.NotebookId == restored.Id);
        var image = await db.DiaryImages.SingleAsync(x => x.UserId == owner);
        var series = await db.TaskSeries.SingleAsync(x => x.NotebookId == restored.Id);
        var segment = await db.TaskSegments.SingleAsync(x => x.SeriesId == series.Id);
        var task = await db.TaskRecords.SingleAsync(x => x.NotebookId == restored.Id);
        var anniversary = await db.Anniversaries.Include(x => x.Notebooks).SingleAsync(x => x.UserId == owner);

        Assert.Multiple(() =>
        {
            Assert.That(books.Select(x => x.Id), Does.Contain(empty));
            Assert.That(books.Count, Is.EqualTo(2));
            Assert.That(restored.Id, Is.Not.EqualTo(original));
            Assert.That(restored.Archived, Is.True);
            Assert.That(restored.TasksPausedOn, Is.EqualTo(new DateOnly(2020, 1, 4)));
            Assert.That(entry.Content, Does.Contain("attachment:" + image.Id));
            Assert.That(entry.SearchText, Does.Contain("現在の本文"));
            Assert.That(restored.Template, Does.Contain("attachment:" + image.Id));
            Assert.That(task.Id, Is.EqualTo(TaskSchedule.Key(segment.Id, new DateOnly(2020, 1, 2))));
            Assert.That(task.CompletedAt, Is.Not.Null);
            Assert.That(task.Memo, Is.EqualTo("操作した回のメモ"));
            Assert.That(Reminders.Read(segment.ReminderJson).Single().Time, Is.EqualTo("08:30"));
            Assert.That(Reminders.Read(task.ReminderJson).Single().At, Is.EqualTo("2020-01-02T08:30"));
            Assert.That(Reminders.Read(task.ReminderJson).Single().Id, Is.Not.EqualTo("00000000000000000000000000000002"));
            Assert.That(anniversary.Notebooks.Single().NotebookId, Is.EqualTo(restored.Id));
        });
        Assert.That(await db.DiaryRevisions.CountAsync(x => x.EntryId == entry.Id), Is.EqualTo(1));
        Assert.That(await db.DiaryImageReferences.AnyAsync(x => x.EntryId == entry.Id && x.ImageId == image.Id), Is.True);
        Assert.That(await db.TaskSlots.CountAsync(x => x.SegmentId == segment.Id && x.Frozen), Is.EqualTo(3));
        Assert.That(await db.PushDevices.AnyAsync(x => x.UserId == owner && x.Enabled), Is.False);
        Assert.That(await db.RestoreTransfers.CountAsync(x => x.UserId == owner && x.Completed), Is.EqualTo(1));
        Assert.That(await db.SecurityAuditLogs.CountAsync(x => x.SubjectId == owner && x.Event == "backup.restore"), Is.EqualTo(1));

        using var imageRead = await target.GetAsync("/images/" + image.Id);
        Assert.That(imageRead.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var foreign = await source.GetAsync("/images/" + image.Id);
        Assert.That(foreign.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    /// <summary>復元の最後でDBが失敗しても本文・画像・通知停止を部分反映しない</summary>
    [Test]
    public async Task CommitFailureRollsBackFilesRecordsAndPushThenAllowsRetry()
    {
        await using var app = NotificationTests.Application(new RecordingPush());
        using var source = app.CreateBrowser();
        using var target = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, source);
        await AuthClient.RegisterAsync(app, target, "target", false);
        await SeedAsync(app, source);
        await NotificationTests.EnableAsync(target);
        var id = await StartAsync(target);
        using var upload = await UploadAsync(target, id, await ExportAsync(source));
        Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.OK), await upload.Content.ReadAsStringAsync());

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = await db.Users.Where(x => x.UserName == "target").Select(x => x.Id).SingleAsync();
        var before = Directory.GetFiles(Path.Combine(app.DataPath, "images"));
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_restore BEFORE UPDATE OF Completed ON RestoreTransfers WHEN NEW.Completed = 1 BEGIN SELECT RAISE(ABORT, 'test failure'); END");

        using var failure = await ConfirmAsync(target, id);
        Assert.That(failure.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(await db.Notebooks.AnyAsync(x => x.UserId == owner), Is.False);
        Assert.That(await db.DiaryImages.AnyAsync(x => x.UserId == owner), Is.False);
        Assert.That(await db.PushDevices.AllAsync(x => x.UserId != owner || x.Enabled), Is.True);
        Assert.That(Directory.GetFiles(Path.Combine(app.DataPath, "images")), Is.EquivalentTo(before));

        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_restore");
        using var retry = await ConfirmAsync(target, id);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.OK), await retry.Content.ReadAsStringAsync());
    }

    /// <summary>確認後の追加記録も再検査し、名前衝突や他人の復元操作を受け入れない</summary>
    [Test]
    public async Task RechecksTargetAndRejectsForeignOperation()
    {
        await using var app = new TestApplication();
        using var source = app.CreateBrowser();
        using var target = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, source);
        await AuthClient.RegisterAsync(app, target, "target", false);
        await DiaryClient.CreateBookAsync(source, "同名");
        var book = await DiaryClient.CreateBookAsync(target, "同名");
        var id = await StartAsync(target);

        using var upload = await UploadAsync(target, id, await ExportAsync(source));
        Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await upload.Content.ReadAsStringAsync(), Does.Contain("同名"));
        using var conflict = await ConfirmAsync(target, id);
        Assert.That(conflict.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        using var foreign = await ConfirmAsync(source, id);
        Assert.That(foreign.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await source.GetStringAsync("/api/data/restore/status?id=" + id), Does.Contain("none"));

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Notebooks.Where(x => x.Id == book).ExecuteUpdateAsync(set => set.SetProperty(x => x.Name, "変更済み"));
        await DiaryClient.SaveVersionAsync(target, book, "確認後に作成した記録");
        using var occupied = await ConfirmAsync(target, id);
        Assert.That(occupied.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await db.Notebooks.CountAsync(), Is.EqualTo(2));
    }

    /// <summary>Markdown出力には現行本文だけを含め、画像参照を書き換えコード例は変えない</summary>
    [Test]
    public async Task MarkdownExportPreservesTextAndExcludesHistory()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await SeedAsync(app, client);
        var bytes = await ExportAsync(client, "markdown", book);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var entry = zip.Entries.Single(x => x.FullName.EndsWith(".md", StringComparison.Ordinal));
        var content = ReadText(entry);
        Assert.Multiple(() =>
        {
            Assert.That(content, Does.StartWith("---\ndate: 2020-05-10\n"));
            Assert.That(content, Does.Contain("![写真](../assets/"));
            Assert.That(content, Does.Not.Contain("過去版の本文"));
            Assert.That(content, Does.Contain("`![例](attachment:00000000000000000000000000000001)`"));
            Assert.That(zip.GetEntry("manifest.json"), Is.Null);
            Assert.That(zip.GetEntry("markdown.json"), Is.Not.Null);
            Assert.That(zip.Entries.Count, Is.EqualTo(3));
        });
    }

    /// <summary>画像なしでも本文・履歴・テンプレートを保持し、画像実体を出力しない</summary>
    [TestCase("backup")]
    [TestCase("markdown")]
    public async Task ExportWithoutImagesPreservesReferences(string kind)
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        await SeedAsync(app, client);

        var bytes = await ExportAsync(client, kind, includeImages: false);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.That(zip.Entries.Any(x => x.FullName.StartsWith("assets/", StringComparison.Ordinal)
            || x.FullName.StartsWith("images/", StringComparison.Ordinal)), Is.False);
        var metadata = zip.GetEntry(kind == "backup" ? "manifest.json" : "markdown.json") ?? throw new InvalidDataException();
        using var manifest = JsonDocument.Parse(ReadText(metadata));
        Assert.That(manifest.RootElement.GetProperty("IncludesImages").GetBoolean(), Is.False);
        var text = string.Join("\n", zip.Entries.Select(ReadText));
        Assert.That(text, Does.Contain(kind == "backup" ? "attachment:" : "../assets/"));
        if (kind == "backup")
        {
            Assert.That(zip.Entries.Count(x => x.FullName.StartsWith("revisions/", StringComparison.Ordinal)), Is.EqualTo(1));
        }
    }

    /// <summary>リンク形式だけを変換し、コード・通常リンク・角括弧付きURLを正しく区別する</summary>
    [Test]
    public void ImageRewritePreservesMarkdownSyntax()
    {
        const string id = "0123456789abcdef0123456789abcdef";
        var markdown = new DiaryMarkdown();
        var text = $"![a](<attachment:{id}>)\n![b][photo]\n\n[photo]: attachment:{id} \"title\"\n\n`![code](attachment:{id})`\n[link](attachment:{id})";
        var rewritten = markdown.RewriteImages(text, _ => "assets/image.png");
        Assert.That(rewritten, Is.EqualTo($"![a](<assets/image.png>)\n![b][photo]\n\n[photo]: assets/image.png \"title\"\n\n`![code](attachment:{id})`\n[link](attachment:{id})"));
    }

    /// <summary>壊れた目録・パス・重複・参照を反映前に拒否する</summary>
    [TestCase("path")]
    [TestCase("duplicate")]
    [TestCase("version")]
    [TestCase("checksum")]
    [TestCase("foreign-book")]
    [TestCase("null")]
    [TestCase("missing-id")]
    [TestCase("missing-content")]
    [TestCase("null-content")]
    [TestCase("internal-field")]
    public async Task RejectsInvalidArchivesWithoutWritingRecords(string damage)
    {
        await using var app = new TestApplication();
        using var source = app.CreateBrowser();
        using var target = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, source);
        await AuthClient.RegisterAsync(app, target, "target", false);
        var book = await DiaryClient.CreateBookAsync(source);
        await DiaryClient.SaveVersionAsync(source, book, "元の本文");
        var bytes = Damage(await ExportAsync(source), damage);
        var id = await StartAsync(target);

        using var result = await UploadAsync(target, id, bytes);
        Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), await result.Content.ReadAsStringAsync());
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(await db.DiaryEntries.CountAsync(), Is.EqualTo(1));
        Assert.That(await db.RestoreTransfers.AnyAsync(x => x.Ready), Is.False);
        Assert.That(File.Exists(scope.ServiceProvider.GetRequiredService<BackupService>().PathFor(id)), Is.False);
    }

    /// <summary>小さな試験上限で実読み取り量の境界を検証する</summary>
    [Test]
    public async Task CopyStopsBeforeWritingPastLimit()
    {
        using var input = new MemoryStream(new byte[10]);
        using var output = new MemoryStream();
        await Assert.ThatAsync(async () => await BackupArchive.CopyAsync(input, output, 9, CancellationToken.None), Throws.TypeOf<InvalidDataException>());
        Assert.That(output.Length, Is.Zero);

        input.Position = 0;
        Assert.That(await BackupArchive.CopyAsync(input, output, 10, CancellationToken.None), Is.EqualTo(10));
    }

    /// <summary>ホストを再起動し一時ZIPを回収しても、確定済み復元の再送を二重適用しない</summary>
    [Test]
    public async Task CompletedRestoreSurvivesRestartAndTemporaryFileCleanup()
    {
        var first = new TestApplication { PreserveData = true };
        string id;
        await using (first)
        {
            using var source = first.CreateBrowser();
            using var target = first.CreateBrowser();
            await AuthClient.RegisterAsync(first, source);
            await AuthClient.RegisterAsync(first, target, "target", false);
            var book = await DiaryClient.CreateBookAsync(source);
            await DiaryClient.SaveVersionAsync(source, book, "再起動後も一度だけ復元");
            id = await StartAsync(target);
            using var upload = await UploadAsync(target, id, await ExportAsync(source));
            Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var confirm = await ConfirmAsync(target, id);
            Assert.That(confirm.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        await using var second = new TestApplication(first.DataPath);
        using var browser = second.CreateBrowser();
        using var login = await AuthClient.LoginAsync(browser, "target");
        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        await using var scope = second.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var backups = scope.ServiceProvider.GetRequiredService<BackupService>();
        var gate = scope.ServiceProvider.GetRequiredService<Hibitsuzuri.Web.Auth.AuthWriteGate>();
        await gate.Semaphore.WaitAsync();
        try
        {
            await BackupMaintenance.CleanupAsync(db, backups, second.Clock, CancellationToken.None);
        }
        finally
        {
            gate.Semaphore.Release();
        }

        Assert.That(File.Exists(backups.PathFor(id)), Is.False);
        using var retry = await ConfirmAsync(browser, id);
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await browser.GetStringAsync("/api/data/restore/status?id=" + id), Does.Contain("completed"));
        Assert.That(await db.DiaryEntries.CountAsync(), Is.EqualTo(2));
    }

    /// <summary>確認期限が過ぎたZIPでは反映せず、取り消してやり直せる</summary>
    [Test]
    public async Task ConfirmationExpiresAndCancellationReleasesUploadSlot()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var bytes = await ExportAsync(client);
        var id = await StartAsync(client);
        using var upload = await UploadAsync(client, id, bytes);
        Assert.That(upload.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        app.Clock.Advance(TimeSpan.FromMinutes(61));

        using var expired = await ConfirmAsync(client, id);
        Assert.That(expired.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using var cancel = await AuthClient.PostAsync(client, $"/api/data/restore/{id}/cancel");
        Assert.That(cancel.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var next = await StartAsync(client);
        Assert.That(next, Is.Not.EqualTo(id));
    }

    /// <summary>未来に計算した後で期限が到来した索引も、元の調整日を固定して出力する</summary>
    [Test]
    public async Task ExportFreezesArrivedSlotsEvenWhenCachedMonthWasNotRecomputed()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, client);
        var book = await DiaryClient.CreateBookAsync(client);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var series = new TaskSeries { NotebookId = book };
        var segment = new TaskSegment
        {
            SeriesId = series.Id,
            Title = "元の予定",
            StartDate = new DateOnly(2020, 1, 1),
            RuleJson = JsonSerializer.Serialize(new RepeatRule { Count = 1 })
        };
        db.TaskSeries.Add(series);
        db.TaskSegments.Add(segment);
        db.TaskSlots.Add(new TaskSlot
        {
            Id = TaskSchedule.Key(segment.Id, segment.StartDate),
            SegmentId = segment.Id,
            ScheduledDate = segment.StartDate,
            DueDate = new DateOnly(2020, 1, 3),
            Frozen = false
        });
        db.TaskWindows.Add(new TaskWindow { SegmentId = segment.Id, Month = new DateOnly(2020, 1, 1) });
        await db.SaveChangesAsync();

        using var zip = new ZipArchive(new MemoryStream(await ExportAsync(client)));
        var slot = JsonSerializer.Deserialize<BackupTaskSlot>(ReadText(zip.Entries.Single(x => x.FullName.StartsWith("slots/", StringComparison.Ordinal))), BackupArchive.Json) ?? throw new InvalidDataException();
        Assert.Multiple(() =>
        {
            Assert.That(slot.ScheduledDate, Is.EqualTo(new DateOnly(2020, 1, 1)));
            Assert.That(slot.DueDate, Is.EqualTo(new DateOnly(2020, 1, 3)));
        });
    }

    /// <summary>保持対象と除外対象が混在するバックアップ元を作る</summary>
    private static async Task<string> SeedAsync(TestApplication app, HttpClient client)
    {
        var bookId = await DiaryClient.CreateBookAsync(client, "移行する日記帳");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var book = await db.Notebooks.SingleAsync(x => x.Id == bookId);
        var now = app.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        var image = new DiaryImage
        {
            UserId = book.UserId,
            NotebookId = book.Id,
            EntryDate = new DateOnly(2020, 5, 10),
            Name = "写真.png",
            MediaType = "image/png",
            Width = 2,
            Height = 2,
            CreatedAt = now
        };
        using (var bitmap = new SKBitmap(2, 2))
        using (var picture = SKImage.FromBitmap(bitmap))
        using (var encoded = picture.Encode(SKEncodedImageFormat.Png, 100))
        {
            var bytes = encoded.ToArray();
            image.Length = bytes.Length;
            await File.WriteAllBytesAsync(scope.ServiceProvider.GetRequiredService<ImageStore>().PathFor(image.Id), bytes);
        }

        db.DiaryImages.Add(image);
        db.ImageDraftLeases.Add(new ImageDraftLease { ImageId = image.Id, DraftId = Guid.NewGuid().ToString(), ExpiresAt = now + 60000 });
        await db.SaveChangesAsync();
        var version = await DiaryClient.SaveVersionAsync(client, bookId, "過去版の本文");
        await DiaryClient.SaveVersionAsync(client, bookId, $"現在の本文\n![写真](attachment:{image.Id})\n`![例](attachment:00000000000000000000000000000001)`", version);

        book.Archived = true;
        book.TasksPausedOn = new DateOnly(2020, 1, 4);
        book.Template = $"テンプレート\n![写真](attachment:{image.Id})";
        var deleted = new Notebook { UserId = book.UserId, Name = "削除した日記帳", DeletedAt = now };
        db.Notebooks.Add(deleted);
        db.DiaryEntries.AddRange(new DiaryEntry { NotebookId = bookId, EntryDate = new DateOnly(2020, 5, 11), Content = "ゴミ箱本文", DeletedAt = now },
            new DiaryEntry { NotebookId = deleted.Id, EntryDate = new DateOnly(2020, 5, 12), Content = "削除帳本文" });
        var series = new TaskSeries { NotebookId = bookId };
        var segment = new TaskSegment
        {
            SeriesId = series.Id,
            Title = "繰り返し",
            Memo = "共通メモ",
            ReminderJson = JsonSerializer.Serialize(new[] { new Reminder { Id = "00000000000000000000000000000001", Kind = "relative", DaysBefore = 1, Time = "08:30" } }),
            StartDate = new DateOnly(2020, 1, 1),
            RuleJson = JsonSerializer.Serialize(new RepeatRule { Count = 3 })
        };
        db.TaskSeries.Add(series);
        db.TaskSegments.Add(segment);
        db.TaskRecords.Add(new TaskRecord
        {
            Id = TaskSchedule.Key(segment.Id, new DateOnly(2020, 1, 2)),
            SeriesId = series.Id,
            SegmentId = segment.Id,
            NotebookId = bookId,
            ScheduledDate = new DateOnly(2020, 1, 2),
            DueDate = new DateOnly(2020, 1, 3),
            CompletedAt = now,
            Edited = true,
            ReminderJson = JsonSerializer.Serialize(new[] { new Reminder { Id = "00000000000000000000000000000002", Kind = "absolute", At = "2020-01-02T08:30" } }),
            Title = "操作した回",
            Memo = "操作した回のメモ"
        });
        var anniversary = new Anniversary { UserId = book.UserId, Title = "記念日", Month = 2, Day = 29, LeapDayPolicy = "feb28", AllNotebooks = false };
        anniversary.Notebooks = [new() { AnniversaryId = anniversary.Id, NotebookId = bookId }, new() { AnniversaryId = anniversary.Id, NotebookId = deleted.Id }];
        db.Anniversaries.Add(anniversary);
        await db.SaveChangesAsync();
        return bookId;
    }

    /// <summary>HTTPの添付ファイル応答を取得する</summary>
    private static async Task<byte[]> ExportAsync(HttpClient client, string kind = "backup", string book = "", bool includeImages = true)
    {
        using var response = await AuthClient.PostAsync(client, "/data/export", new()
        {
            ["kind"] = kind,
            ["bookId"] = book,
            ["includeImages"] = includeImages.ToString()
        });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>確認前に操作IDを取得する</summary>
    private static async Task<string> StartAsync(HttpClient client)
    {
        using var response = await AuthClient.PostAsync(client, "/api/data/restore", []);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString() ?? throw new InvalidDataException();
    }

    /// <summary>実際のCSRFヘッダーを付けてZIPをストリーム送信する</summary>
    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string id, byte[] bytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/data/restore/{id}/upload") { Content = new ByteArrayContent(bytes) };
        request.Headers.Add("RequestVerificationToken", await client.GetStringAsync("/fixture/token"));
        return await client.SendAsync(request);
    }

    /// <summary>確認済みの復元を実行する</summary>
    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, string id) => AuthClient.PostAsync(client, $"/api/data/restore/{id}/confirm", new() { ["confirmed"] = "true" });

    /// <summary>試験用の小さなZIP項目をUTF-8で読む</summary>
    private static string ReadText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>チェックサム更新の有無を分けて、構造と意味の両方の破損を再現する</summary>
    private static byte[] Damage(byte[] bytes, string damage)
    {
        using var stream = new MemoryStream();
        stream.Write(bytes);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, true))
        {
            var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException();
            var manifest = JsonSerializer.Deserialize<BackupManifest>(ReadText(manifestEntry)) ?? throw new InvalidDataException();
            if (damage == "path")
            {
                zip.CreateEntry("../outside.txt");
            }
            else if (damage == "duplicate")
            {
                zip.CreateEntry("manifest.json");
            }
            else if (damage == "version")
            {
                manifest = manifest with
                {
                    Version = 999
                };
            }
            else
            {
                var entry = zip.Entries.Single(x => x.FullName.StartsWith("entries/", StringComparison.Ordinal));
                var name = entry.FullName;
                var record = JsonSerializer.Deserialize<BackupEntry>(ReadText(entry), BackupArchive.Json) ?? throw new InvalidDataException();
                if (damage is "checksum" or "foreign-book")
                {
                    record = record with
                    {
                        NotebookId = Guid.NewGuid().ToString("N")
                    };
                }

                entry.Delete();
                var content = damage == "null" ? "null"u8.ToArray() : JsonSerializer.SerializeToUtf8Bytes(record);
                if (damage is "missing-id" or "missing-content" or "null-content" or "internal-field")
                {
                    var json = System.Text.Json.Nodes.JsonNode.Parse(content)?.AsObject() ?? throw new InvalidDataException();
                    if (damage is "missing-id" or "missing-content")
                    {
                        json.Remove(damage == "missing-id" ? "Id" : "Content");
                    }
                    else if (damage == "null-content")
                    {
                        json["Content"] = null;
                    }
                    else
                    {
                        json["Version"] = Guid.NewGuid().ToString("N");
                    }

                    content = JsonSerializer.SerializeToUtf8Bytes(json);
                }

                using (var writer = zip.CreateEntry(name).Open())
                {
                    writer.Write(content);
                }

                if (damage != "checksum")
                {
                    var index = manifest.Files.FindIndex(x => x.Path == name);
                    manifest.Files[index] = new BackupFile(name, content.Length, Convert.ToHexStringLower(SHA256.HashData(content)));
                }
            }

            if (damage is not ("path" or "duplicate"))
            {
                manifestEntry.Delete();
                using var writer = zip.CreateEntry("manifest.json").Open();
                JsonSerializer.Serialize(writer, manifest);
            }
        }

        return stream.ToArray();
    }
}
