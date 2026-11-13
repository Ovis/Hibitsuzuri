using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Backups;

/// <summary>確認期限を過ぎた復元ZIPと中断した一時ファイルを回収する</summary>
public sealed class BackupMaintenance(IServiceScopeFactory scopes, AuthWriteGate gate, TimeProvider clock, ILogger<BackupMaintenance> logger) : BackgroundService
{
    /// <summary>起動時と毎時に期限切れを回収し、失敗をログへ残す</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        try
        {
            do
            {
                await gate.Semaphore.WaitAsync(stoppingToken);
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await CleanupAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                        scope.ServiceProvider.GetRequiredService<BackupService>(), clock, stoppingToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.LogError(error, "バックアップの一時ファイルの回収に失敗しました。");
                }
                finally
                {
                    gate.Semaphore.Release();
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 次回起動時に期限切れを再確認する。
        }
    }

    /// <summary>確定済みの操作IDは残し、内容を持つファイルだけを削除する</summary>
    public static async Task CleanupAsync(AppDbContext db, BackupService backups, TimeProvider clock, CancellationToken token)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var expired = await db.RestoreTransfers.Where(x => x.Completed || x.ExpiresAt <= now).Select(x => x.Id).ToListAsync(token);
        foreach (var id in expired)
        {
            File.Delete(backups.PathFor(id));
        }

        await db.RestoreTransfers.Where(x => !x.Completed && x.ExpiresAt <= now).ExecuteDeleteAsync(token);
        if (!Directory.Exists(backups.DirectoryPath))
        {
            return;
        }

        // ZIP内の名前は使わず、この機能が作ったGUID形式の直下ファイルだけを対象にする。
        foreach (var path in Directory.EnumerateFiles(backups.DirectoryPath))
        {
            if (BackupInspection.ValidId(Path.GetFileNameWithoutExtension(path)) && Path.GetExtension(path) is ".zip" or ".input" or ".output"
                && File.GetLastWriteTimeUtc(path) < clock.GetUtcNow().AddDays(-1).UtcDateTime)
            {
                File.Delete(path);
            }
        }
    }
}
