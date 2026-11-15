using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hibitsuzuri.Web.Hosting;
using Hibitsuzuri.Web.Storage;

namespace Hibitsuzuri.Web.Auth;

/// <summary>永続領域の所有者だけが確認できる、初回管理者登録用の一時コード</summary>
public sealed class SetupToken(DataDirectory data, TimeProvider clock, ILogger<SetupToken> logger)
{
    private byte[]? _hash;
    private DateTimeOffset _expires;

    /// <summary>コードと有効期限を保存する非公開ファイル</summary>
    private string FilePath => Path.Combine(data.RootPath, "setup-token.txt");

    /// <summary>未登録時だけコードを準備し、期限内は維持する。ファイル異常は起動を拒否する</summary>
    public void Initialize(bool setupCompleted)
    {
        _hash = null;
        if (setupCompleted)
        {
            Invalidate();
            return;
        }

        RejectLinks(FilePath);
        if (!File.Exists(FilePath))
        {
            Create();
        }

        var stored = Read();
        if (stored.Expires <= clock.GetUtcNow())
        {
            Create();
            stored = Read();
        }

        if (stored.Expires > clock.GetUtcNow().AddHours(24))
        {
            throw new InvalidDataException("初回登録コードの有効期限が不正です。");
        }

        _hash = SHA256.HashData(Encoding.ASCII.GetBytes(stored.Token));
        _expires = stored.Expires;
    }

    /// <summary>期限とファイルの一致を固定長ハッシュで照合する。登録失敗ではコードを消費しない</summary>
    public bool Verify(string? supplied)
    {
        if (_hash is null || _expires <= clock.GetUtcNow() || supplied is not { Length: 64 })
        {
            return false;
        }

        try
        {
            var stored = Read();
            return stored.Expires == _expires
                && CryptographicOperations.FixedTimeEquals(_hash, SHA256.HashData(Encoding.ASCII.GetBytes(stored.Token)))
                && CryptographicOperations.FixedTimeEquals(_hash, SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning((int)OperationalEvent.SetupTokenReadFailed, "初回登録コードを読み取れません。");
            return false;
        }
    }

    /// <summary>DBの登録確定後に失効する。削除できなくてもメモリとSetupCompletedで再利用を拒否する</summary>
    public void Invalidate()
    {
        _hash = null;
        try
        {
            RejectLinks(FilePath);
            File.Delete(FilePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning((int)OperationalEvent.SetupTokenCleanupFailed, "初回登録コードを削除できません。");
        }
    }

    /// <summary>リンク・権限・サイズと2行の書式を検査し、コードと期限を読む</summary>
    private (string Token, DateTimeOffset Expires) Read()
    {
        RejectLinks(FilePath);
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(FilePath) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
        {
            throw new InvalidDataException("初回登録コードのファイル権限は0600にしてください。");
        }

        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 256)
        {
            throw new InvalidDataException("初回登録コードのファイルが不正です。");
        }

        using var reader = new StreamReader(stream, Encoding.ASCII);
        var token = reader.ReadLine();
        var deadline = reader.ReadLine();
        if (token is not { Length: 64 } || token.Any(c => !char.IsAsciiHexDigit(c)) || reader.ReadLine() is not null
            || !DateTimeOffset.TryParseExact(deadline, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry))
        {
            throw new InvalidDataException("初回登録コードのファイルが不正です。");
        }

        return (token, expiry);
    }

    /// <summary>256ビットの乱数と24時間の期限を、0600の一時ファイルから置換する</summary>
    private void Create()
    {
        RejectLinks(FilePath);
        var temporary = Path.Combine(data.RootPath, ".setup-token-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temporary, options))
            {
                var content = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)) + "\n"
                    + clock.GetUtcNow().AddHours(24).ToString("O", CultureInfo.InvariantCulture) + "\n";
                stream.Write(Encoding.ASCII.GetBytes(content));
                stream.Flush(true);
            }

            RejectLinks(FilePath);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>途中のディレクトリやリンク切れも含め、別の領域への読み書きを拒否する</summary>
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (new FileInfo(current).LinkTarget is not null
                || ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidDataException("初回登録コードの保存先にリンクは使用できません。");
            }
        }
    }
}
