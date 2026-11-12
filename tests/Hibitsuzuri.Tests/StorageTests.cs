using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>実SQLiteファイルとData Protectionキーの永続性を検証する</summary>
public sealed class StorageTests
{
    /// <summary>再起動しても移行履歴・DB内の情報・鍵による復号を維持する</summary>
    [Test]
    public async Task RestartPreservesDatabaseAndProtectionKeys()
    {
        var first = new TestApplication { PreserveData = true };
        string protectedValue;
        await using (first)
        {
            using var browser = first.CreateBrowser();
            protectedValue = first.Services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("restart-test").Protect("persisted-value");

            await using var scope = first.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await database.Database.ExecuteSqlRawAsync("CREATE TABLE RestartProbe (Value TEXT NOT NULL);");
            await database.Database.ExecuteSqlRawAsync("INSERT INTO RestartProbe (Value) VALUES ('kept');");
        }

        await using var second = new TestApplication(first.DataPath);
        using var secondBrowser = second.CreateBrowser();
        var recovered = second.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("restart-test").Unprotect(protectedValue);
        await using var connection = new SqliteConnection(second.Services.GetRequiredService<DataDirectory>().ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM RestartProbe;";
        var value = await command.ExecuteScalarAsync();

        await using var secondScope = second.Services.CreateAsyncScope();
        var secondDatabase = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrations = await secondDatabase.Database.GetAppliedMigrationsAsync();
        var pending = await secondDatabase.Database.GetPendingMigrationsAsync();
        Assert.Multiple(() =>
        {
            Assert.That(recovered, Is.EqualTo("persisted-value"));
            Assert.That(value, Is.EqualTo("kept"));
            Assert.That(migrations.Count(), Is.EqualTo(2));
            Assert.That(pending, Is.Empty);
        });
    }

    /// <summary>WALと外部キー制約を実際の接続で有効にする</summary>
    [Test]
    public async Task SqliteUsesWalAndRejectsOrphanRows()
    {
        await using var app = new TestApplication();
        using var browser = app.CreateBrowser();
        await using var connection = new SqliteConnection(app.Services.GetRequiredService<DataDirectory>().ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";

        Assert.That(await command.ExecuteScalarAsync(), Is.EqualTo("wal"));
        command.CommandText = "CREATE TABLE Parent (Id INTEGER PRIMARY KEY); CREATE TABLE Child (ParentId INTEGER REFERENCES Parent(Id));";
        await command.ExecuteNonQueryAsync();

        command.CommandText = "INSERT INTO Child (ParentId) VALUES (999);";
        Assert.ThrowsAsync<SqliteException>(async () => await command.ExecuteNonQueryAsync());
    }

    /// <summary>同じデータ領域を使用する別ホストの起動を拒否する</summary>
    [Test]
    public async Task ConcurrentHostCannotShareDataDirectory()
    {
        await using var first = new TestApplication();
        using var browser = first.CreateBrowser();
        await using var second = new TestApplication(first.DataPath) { PreserveData = true };

        Assert.Throws<IOException>(() => second.CreateBrowser());
    }
}
