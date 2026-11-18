using System.Text.Json;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>実SQLiteファイルとData Protectionキーの永続性を検証する</summary>
public sealed class StorageTests
{
    /// <summary>新規DBの列・索引・外部キーが現行モデルと一致し、移行専用の既定値を残さない</summary>
    [Test]
    public async Task MigratedSchemaMatchesCurrentModel()
    {
        await using var app = new TestApplication();
        using var browser = app.CreateBrowser();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var migrated = new SqliteConnection(app.Services.GetRequiredService<DataDirectory>().ConnectionString);
        await migrated.OpenAsync();
        await using var expected = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await expected.OpenAsync();
        await using var create = expected.CreateCommand();
        create.CommandText = db.Database.GenerateCreateScript();

        await create.ExecuteNonQueryAsync();

        Assert.That(db.Database.HasPendingModelChanges(), Is.False);
        Assert.That(await ReadSchemaAsync(migrated), Is.EquivalentTo(await ReadSchemaAsync(expected)));
    }

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
            Assert.That(migrations, Is.EquivalentTo(secondDatabase.Database.GetMigrations()));
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

    /// <summary>移行履歴と列順を除き、SQLiteに実際に作られた制約を列挙する</summary>
    private static async Task<List<string>> ReadSchemaAsync(SqliteConnection connection)
    {
        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EF%'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        var definitions = new List<string>();
        foreach (var table in tables)
        {
            foreach (var pragma in new[] { "table_info", "foreign_key_list" })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM pragma_{pragma}($table)";
                command.Parameters.AddWithValue("$table", table);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    // 先頭の列番号・外部キー番号は、テーブル再構築で変わっても同じ制約を表す。
                    definitions.Add(table + ":" + pragma + ":" + JsonSerializer.Serialize(values.Skip(1)));
                }
            }

            await using var indexes = connection.CreateCommand();
            indexes.CommandText = "SELECT sql FROM sqlite_schema WHERE type='index' AND tbl_name=$table AND sql IS NOT NULL";
            indexes.Parameters.AddWithValue("$table", table);
            await using var indexReader = await indexes.ExecuteReaderAsync();
            while (await indexReader.ReadAsync())
            {
                definitions.Add(indexReader.GetString(0));
            }
        }

        return definitions;
    }
}
