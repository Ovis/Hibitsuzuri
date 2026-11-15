using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Hibitsuzuri.Web.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hibitsuzuri.Tests;

/// <summary>運用ログの秘密排除・保存境界・障害検知を実ファイルで検証する</summary>
public sealed class OperationalLoggingTests
{
    private string _root = string.Empty;

    /// <summary>試験専用の空ディレクトリを用意する</summary>
    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "Hibitsuzuri.Logging.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>この試験が作成した領域だけを回収する</summary>
    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>自由文字列とスコープを除き、例外の種類と照合IDだけを両出力へ残す</summary>
    [Test]
    public void BothOutputsExcludeSecretsAndKeepDiagnosticIdentifiers()
    {
        using var output = new StringWriter();
        using var provider = new OperationalLogging(_root, new TestClock(), output);
        var logger = provider.CreateLogger("Hibitsuzuri.Web.Test");
        using var activity = new Activity("logging-test").Start();
        using var scope = logger.BeginScope("scope-secret");

        logger.LogError(new EventId((int)OperationalEvent.PushProcessingFailed, "event-name-secret"),
            new InvalidOperationException("exception-secret"), "diary-secret {Password}", "password-secret");
        provider.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").LogError(20102, "SELECT diary-secret");
        logger.LogDebug("debug-secret");

        var file = File.ReadAllText(Directory.GetFiles(_root).Single());
        Assert.That(file, Is.EqualTo(output.ToString()));
        Assert.That(file, Does.Not.Contain("secret"));

        var lines = file.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines, Has.Length.EqualTo(2));
        using var record = JsonDocument.Parse(lines[0]);
        Assert.Multiple(() =>
        {
            Assert.That(record.RootElement.GetProperty("eventCode").GetString(), Is.EqualTo("PushProcessingFailed"));
            Assert.That(record.RootElement.GetProperty("exceptionType").GetString(), Is.EqualTo(typeof(InvalidOperationException).FullName));
            Assert.That(record.RootElement.GetProperty("traceId").GetString(), Is.EqualTo(activity.TraceId.ToString()));
        });
    }

    /// <summary>並行出力でも行を壊さず上限内で分割し、再起動後は既存内容へ追記する</summary>
    [Test]
    public void ConcurrentWritesRollAtByteLimitAndRestartAppends()
    {
        var clock = new TestClock();
        using var output = new StringWriter();
        using (var first = new OperationalLogging(_root, clock, output, maximumBytes: 700))
        {
            var logger = first.CreateLogger("Hibitsuzuri.Web.Test");
            Parallel.For(0, 25, _ => logger.LogInformation((int)OperationalEvent.Started, "起動"));
            Assert.That(first.FileFailed, Is.False);
        }

        var before = Directory.GetFiles(_root).ToDictionary(path => path, File.ReadAllText);
        using (var second = new OperationalLogging(_root, clock, output, maximumBytes: 700))
        {
            second.CreateLogger("Hibitsuzuri.Web.Test").LogInformation((int)OperationalEvent.Started, "再起動");
            Assert.That(second.FileFailed, Is.False);
        }

        var files = Directory.GetFiles(_root);
        Assert.That(files.Length, Is.GreaterThan(1));
        Assert.That(files.All(path => new FileInfo(path).Length <= 700), Is.True);
        Assert.That(before.All(pair => File.ReadAllText(pair.Key).StartsWith(pair.Value, StringComparison.Ordinal)), Is.True);
        var lines = files.SelectMany(File.ReadAllLines).ToArray();
        Assert.That(lines, Has.Length.EqualTo(26));
        foreach (var line in lines)
        {
            using var record = JsonDocument.Parse(line);
            Assert.That(record.RootElement.GetProperty("eventCode").GetString(), Is.EqualTo("Started"));
        }
    }

    /// <summary>UTC日付で切り替え、当日を含む30日分と無関係なファイルを残す</summary>
    [Test]
    public void RetentionUsesUtcDatesAndOnlyOwnFileNames()
    {
        var clock = new TestClock();
        var midnight = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        clock.Advance(midnight.AddSeconds(-1) - clock.GetUtcNow());
        using var provider = new OperationalLogging(_root, clock, TextWriter.Null);
        var logger = provider.CreateLogger("Hibitsuzuri.Web.Test");
        logger.LogInformation((int)OperationalEvent.Started, "前日");

        var expired = Path.Combine(_root, "hibitsuzuri-20261016-000000.jsonl");
        var retained = Path.Combine(_root, "hibitsuzuri-20261017-000000.jsonl");
        var unrelated = Path.Combine(_root, "hibitsuzuri-20200101-manual.jsonl");
        File.WriteAllText(expired, "old");
        File.WriteAllText(retained, "keep");
        File.WriteAllText(unrelated, "manual");

        clock.Advance(TimeSpan.FromSeconds(1));
        logger.LogInformation((int)OperationalEvent.LogMaintenance, "当日");

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(expired), Is.False);
            Assert.That(File.Exists(retained), Is.True);
            Assert.That(File.Exists(unrelated), Is.True);
            Assert.That(File.Exists(Path.Combine(_root, "hibitsuzuri-20261114-000000.jsonl")), Is.True);
            Assert.That(File.Exists(Path.Combine(_root, "hibitsuzuri-20261115-000000.jsonl")), Is.True);
            Assert.That(provider.FileFailed, Is.False);
        });
    }

    /// <summary>上限を超える単一行でファイルを肥大させず、失敗を標準出力へ知らせる</summary>
    [Test]
    public void OversizedRecordReportsFailureWithoutWritingFile()
    {
        using var output = new StringWriter();
        using var provider = new OperationalLogging(_root, new TestClock(), output, maximumBytes: 1);
        provider.CreateLogger("Hibitsuzuri.Web.Test").LogError("secret");

        Assert.That(provider.FileFailed, Is.True);
        Assert.That(Directory.GetFiles(_root), Is.Empty);
        Assert.That(output.ToString(), Does.Contain("LogStorageFailed").And.Not.Contain("secret"));
    }

    /// <summary>配置設定の保存先を使用し、保存失敗から復旧するまで準備完了としない</summary>
    [Test]
    public async Task StorageFailureChangesReadinessUntilSuccessfulWrite()
    {
        await using var app = new TestApplication();
        var logPath = Path.Combine(_root, "configured-logs");
        app.Settings["Operations:LogDirectory"] = logPath;
        using var browser = app.CreateBrowser();
        var provider = app.Services.GetRequiredService<OperationalLogging>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Hibitsuzuri.Web.Test");
        Assert.That(provider.DirectoryPath, Is.EqualTo(logPath));

        var movedPath = Path.Combine(_root, "saved-logs");
        Directory.Move(logPath, movedPath);
        File.WriteAllText(logPath, "unwritable-directory");
        logger.LogError((int)OperationalEvent.PushProcessingFailed, "secret");

        Assert.That(provider.FileFailed, Is.True);
        Assert.That(await ReadinessAsync(app), Is.EqualTo(503));

        File.Delete(logPath);
        Directory.Move(movedPath, logPath);
        logger.LogInformation((int)OperationalEvent.LogMaintenance, "復旧確認");

        Assert.That(provider.FileFailed, Is.False);
        Assert.That(await ReadinessAsync(app), Is.EqualTo(200));
    }

    /// <summary>空の設定と公開ディレクトリへの保存を起動時に拒否する</summary>
    [TestCase("")]
    [TestCase("wwwroot/logs")]
    public async Task InvalidConfiguredDirectoryPreventsStartup(string configured)
    {
        await using var app = new TestApplication();
        app.Settings["Operations:LogDirectory"] = configured;

        Assert.Throws<InvalidOperationException>(() => app.CreateBrowser());
    }

    /// <summary>実際のコンテナ内部と同様に、ループバック接続として準備完了を調べる</summary>
    private static async Task<int> ReadinessAsync(TestApplication app)
    {
        var context = await app.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Method = "GET";
            context.Request.Path = "/health/ready";
            context.Request.Host = new HostString("diary.example.test");
        });

        return context.Response.StatusCode;
    }
}
