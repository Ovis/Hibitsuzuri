using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hibitsuzuri.Web.Hosting;

/// <summary>秘密を含む自由文字列を除き、標準出力と日次ファイルへ同じJSON行を保存する</summary>
public sealed partial class OperationalLogging : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly TextWriter _output;
    private readonly long _maximumBytes;
    private DateOnly? _cleanedDate;
    private DateOnly? _writtenDate;
    private int _sequence;
    private bool _fileFailed;

    /// <summary>直近の書き込みでファイル保存に失敗し、まだ復旧を確認していない</summary>
    public bool FileFailed => Volatile.Read(ref _fileFailed);

    /// <summary>アプリ専用のログ保存先</summary>
    public string DirectoryPath
    {
        get;
    }

    /// <summary>自分が生成したUTC日付・分割番号付きファイルだけを保持期間の対象にする</summary>
    [GeneratedRegex("^hibitsuzuri-([0-9]{8})-[0-9]{6}\\.jsonl$")]
    private static partial Regex FilePattern();

    /// <summary>保存先は配置設定を優先し、省略時はデータ領域のlogsにする</summary>
    public OperationalLogging(IConfiguration configuration, IWebHostEnvironment environment, TimeProvider clock)
        : this(ResolveDirectory(configuration, environment), clock, Console.Out)
    {
    }

    /// <summary>50MB分割・UTCで当日を含む30暦日保持のログ保存先を初期化する</summary>
    /// <param name="maximumBytes">ファイルの最大バイト数。境界検証では小さい値を指定する</param>
    public OperationalLogging(string directory, TimeProvider clock, TextWriter output, long maximumBytes = 50_000_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        DirectoryPath = Path.GetFullPath(directory);
        _clock = clock;
        _output = output;
        _maximumBytes = maximumBytes;

        RejectLinks(DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
    }

    /// <summary>公開ディレクトリへの配置と空の保存先設定を拒否する</summary>
    private static string ResolveDirectory(IConfiguration configuration, IWebHostEnvironment environment)
    {
        // ロガーはホスト構築中に解決されるため、失敗時にDBの使用権を残さないようDataDirectoryを生成しない。
        var configured = configuration["Operations:LogDirectory"];
        if (configured is null)
        {
            var dataPath = configuration["Storage:Directory"];
            if (string.IsNullOrWhiteSpace(dataPath))
            {
                throw new InvalidOperationException("Storage:Directoryには永続領域を指定してください。");
            }

            configured = Path.Combine(dataPath, "logs");
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException("Operations:LogDirectoryにはログ保存先を指定してください。");
        }

        var path = Path.GetFullPath(configured, environment.ContentRootPath);
        var webRoot = Path.GetFullPath(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"));
        var relative = Path.GetRelativePath(webRoot, path);
        if (relative == "." || (!Path.IsPathRooted(relative)
            && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("ログ保存先をwwwroot内には配置できません。");
        }

        return path;
    }

    /// <summary>カテゴリ別の重要度制限と秘密排除を適用するロガーを返す</summary>
    public ILogger CreateLogger(string categoryName) => new SafeLogger(this, categoryName);

    /// <summary>ファイルは書き込みごとに閉じ、借用した標準出力は閉じない</summary>
    public void Dispose()
    {
    }

    /// <summary>動的メッセージ・引数・例外本文・スコープは採用せず、固定項目だけを出力する</summary>
    private void Write(string category, LogLevel level, EventId eventId, Exception? exception)
    {
        var now = _clock.GetUtcNow();
        var code = category.StartsWith("Hibitsuzuri.", StringComparison.Ordinal)
            && Enum.IsDefined(typeof(OperationalEvent), eventId.Id) ? ((OperationalEvent)eventId.Id).ToString() : null;
        var line = JsonSerializer.Serialize(new
        {
            timestamp = now,
            level = level.ToString(),
            category,
            eventId = eventId.Id,
            eventCode = code,
            traceId = Activity.Current?.TraceId.ToString(),
            exceptionType = exception?.GetType().FullName
        }) + "\n";

        lock (_gate)
        {
            _output.Write(line);
            _output.Flush();
            try
            {
                Append(line, DateOnly.FromDateTime(now.UtcDateTime));
                Volatile.Write(ref _fileFailed, false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // 保存失敗をログ自身へ再投入せず、標準出力と準備完了判定で知らせる。
                Volatile.Write(ref _fileFailed, true);
                _output.WriteLine(JsonSerializer.Serialize(new
                {
                    timestamp = now,
                    level = "Error",
                    eventCode = nameof(OperationalEvent.LogStorageFailed),
                    exceptionType = error.GetType().FullName
                }));
                _output.Flush();
            }
        }
    }

    /// <summary>保持期間の整理後、UTF-8の実バイト数でファイルを分割して追記する</summary>
    private void Append(string line, DateOnly date)
    {
        RejectLinks(DirectoryPath);
        if (_cleanedDate != date)
        {
            foreach (var file in Directory.EnumerateFiles(DirectoryPath, "hibitsuzuri-*.jsonl"))
            {
                var match = FilePattern().Match(Path.GetFileName(file));
                if (match.Success && DateOnly.TryParseExact(match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var stored) && stored < date.AddDays(-29))
                {
                    RejectLinks(file);
                    File.Delete(file);
                }
            }

            _cleanedDate = date;
        }

        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length > _maximumBytes)
        {
            throw new IOException("ログ行がファイルの上限を超えています。");
        }

        if (_writtenDate != date)
        {
            _sequence = 0;
            _writtenDate = date;
        }

        for (; _sequence < 1_000_000; _sequence++)
        {
            var file = Path.Combine(DirectoryPath, $"hibitsuzuri-{date:yyyyMMdd}-{_sequence:000000}.jsonl");
            RejectLinks(file);
            if (File.Exists(file) && new FileInfo(file).Length + bytes.Length > _maximumBytes)
            {
                continue;
            }

            using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return;
        }

        throw new IOException("ログの分割数が上限に達しました。");
    }

    /// <summary>保存先の差し替えで領域外を追記・削除しないよう、途中のリンクも拒否する</summary>
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (new FileInfo(current).LinkTarget is not null
                || ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0))
            {
                throw new IOException("ログ保存先のリンクは使用できません。");
            }
        }
    }

    /// <summary>アプリの情報以上とフレームワークの警告以上を、自由文字列の整形前に選別する</summary>
    private sealed class SafeLogger(OperationalLogging owner, string category) : ILogger
    {
        /// <summary>スコープ内の任意値はログへ取り込まない</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>通常操作やSQLの大量出力を抑え、無効指定を除く</summary>
        public bool IsEnabled(LogLevel level) => level != LogLevel.None
            && level >= (category.StartsWith("Hibitsuzuri.", StringComparison.Ordinal) ? LogLevel.Information : LogLevel.Warning);

        /// <summary>formatterを呼ばず、定型項目だけを出力する</summary>
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level))
            {
                owner.Write(category, level, eventId, exception);
            }
        }
    }
}
