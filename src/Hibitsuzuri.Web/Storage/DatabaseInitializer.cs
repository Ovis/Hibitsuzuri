using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Storage;

/// <summary>リクエスト受付前にDBを移行し、SQLiteの動作方式を確定する</summary>
public static class DatabaseInitializer
{
    /// <summary>移行に失敗した場合は起動を中断する</summary>
    public static async Task InitializeAsync(AppDbContext database, CancellationToken cancellationToken)
    {
        await database.Database.MigrateAsync(cancellationToken);
        await database.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var command = database.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            var mode = await command.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(mode as string, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("SQLiteのWALモードを有効にできませんでした。");
            }
        }
        finally
        {
            await database.Database.CloseConnectionAsync();
        }
    }
}
