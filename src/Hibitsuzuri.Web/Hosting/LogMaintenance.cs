namespace Hibitsuzuri.Web.Hosting;

/// <summary>利用のない日もログの保持期間と保存先の復旧を定期的に確認する</summary>
public sealed class LogMaintenance(ILogger<LogMaintenance> logger, TimeProvider clock) : BackgroundService
{
    /// <summary>起動時と1時間ごとの記録で、日次整理と書き込み確認を進める</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        try
        {
            do
            {
                logger.LogInformation((int)OperationalEvent.LogMaintenance, "ログの保持期間と保存先を確認します。");
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停止では次回起動時の確認へ引き継ぐ。
        }
    }
}
