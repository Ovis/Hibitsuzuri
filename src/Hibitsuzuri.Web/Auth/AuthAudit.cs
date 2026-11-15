using System.Security.Claims;
using Hibitsuzuri.Web.Hosting;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Auth;

/// <summary>重要イベントを秘密や可変のログイン名なしで記録する</summary>
public sealed class AuthAudit(AppDbContext db, TimeProvider clock)
{
    /// <summary>イベント発生から90日を経過した監査だけを削除する</summary>
    public Task PruneAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = clock.GetUtcNow().AddDays(-90).ToUnixTimeMilliseconds();
        return db.SecurityAuditLogs.Where(x => x.OccurredAt <= cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>操作と同じトランザクションで保存する監査行を追加する</summary>
    public void Add(HttpContext context, string eventName, string? subjectId, bool succeeded = true)
    {
        var agent = context.Request.Headers.UserAgent.ToString();

        db.SecurityAuditLogs.Add(new SecurityAuditLog
        {
            OccurredAt = clock.GetUtcNow().ToUnixTimeMilliseconds(),
            Event = eventName,
            ActorId = context.User.FindFirstValue(ClaimTypes.NameIdentifier),
            SubjectId = subjectId,
            Succeeded = succeeded,
            IpAddress = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = agent.Length <= 512 ? agent : agent[..512]
        });
    }
}

/// <summary>90日を過ぎた監査と期限切れの専用フローを回収する</summary>
public sealed class AuthMaintenance(IServiceScopeFactory scopes, AuthWriteGate gate, TimeProvider clock, ILogger<AuthMaintenance> logger) : BackgroundService
{
    /// <summary>起動後と1時間ごとに回収する。失敗はログへ残し、次の周期で再試行する</summary>
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
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var now = clock.GetUtcNow();

                    await scope.ServiceProvider.GetRequiredService<AuthAudit>().PruneAsync(stoppingToken);
                    await db.AccountFlows.Where(x => x.ExpiresAt <= now.ToUnixTimeMilliseconds()).ExecuteDeleteAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError((int)OperationalEvent.AuthCleanupFailed, exception, "認証の期限切れデータの回収に失敗しました。");
                }
                finally
                {
                    gate.Semaphore.Release();
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // ホスト停止時は次回起動の回収へ引き継ぐ。
        }
    }
}
