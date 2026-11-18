using Microsoft.Data.Sqlite;

namespace Hibitsuzuri.Web.Storage;

/// <summary>公開領域から分離した永続ファイルと、単一ホストでの使用権を管理する</summary>
public sealed class DataDirectory : IDisposable
{
    private readonly FileStream _lease;

    /// <summary>永続領域の絶対パス</summary>
    public string RootPath
    {
        get;
    }

    /// <summary>添付画像の保存先</summary>
    public string ImagesPath
    {
        get;
    }

    /// <summary>再起動後もCookie等を復号するための鍵の保存先</summary>
    public string KeysPath
    {
        get;
    }

    /// <summary>外部キー検証と待機時間を指定したSQLite接続文字列</summary>
    public string ConnectionString
    {
        get;
    }

    /// <summary>保存先を検証し、同一領域を使う別ホストの起動を拒否する</summary>
    public DataDirectory(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configuredPath = configuration["Storage:Directory"];
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException("Storage:Directoryには永続領域を指定してください。");
        }

        RootPath = Path.GetFullPath(configuredPath, environment.ContentRootPath);
        var webRoot = Path.GetFullPath(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"));
        var relativePath = Path.GetRelativePath(webRoot, RootPath);
        if (relativePath == "." || (!Path.IsPathRooted(relativePath)
            && relativePath != ".." && !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("永続領域をwwwroot内には配置できません。");
        }

        Directory.CreateDirectory(RootPath);
        ImagesPath = Path.Combine(RootPath, "images");
        KeysPath = Path.Combine(RootPath, "keys");
        Directory.CreateDirectory(ImagesPath);
        Directory.CreateDirectory(KeysPath);

        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(RootPath, "hibitsuzuri.db"),
            ForeignKeys = true,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString();

        // 起動時の移行と将来のファイル回収を、同じ領域で複数ホストに実行させない。
        _lease = new FileStream(Path.Combine(RootPath, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>永続領域の使用権を解放する</summary>
    public void Dispose()
    {
        _lease.Dispose();
    }
}
