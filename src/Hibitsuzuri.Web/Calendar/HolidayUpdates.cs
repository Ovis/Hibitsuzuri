using System.Security.Claims;
using System.Text.Json;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Hosting;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Calendar;

/// <summary>自動更新と手動更新の取得・反映を重複させない排他権</summary>
public sealed class HolidayUpdateGate
{
    /// <summary>外部取得を含む更新全体の排他</summary>
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}

/// <summary>同梱初期値と、検証済みデータの原子的な入れ替え</summary>
public sealed class HolidayUpdates(AppDbContext db, HolidaySource source, HolidayUpdateGate updates,
    AuthWriteGate writes, SessionService sessions, TimeProvider clock, ILogger<HolidayUpdates> logger, Hibitsuzuri.Web.Tasks.TaskSchedule tasks)
{
    /// <summary>初回だけ同梱データを入れる。既存データと外部更新結果は上書きしない</summary>
    public static async Task SeedAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        if (await db.HolidayStates.AnyAsync(cancellationToken))
        {
            return;
        }

        await using var stream = typeof(HolidayUpdates).Assembly.GetManifestResourceStream("Hibitsuzuri.Web.Calendar.holidays.json")
            ?? throw new InvalidOperationException("同梱の祝日データがありません。");
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var holidays = json.RootElement.GetProperty("holidays").Deserialize<Holiday[]>()
            ?? throw new InvalidDataException("同梱の祝日データを読み取れません。");
        HolidaySource.Validate(holidays);

        db.Holidays.AddRange(holidays);
        db.HolidayStates.Add(new HolidayState
        {
            FirstYear = holidays.Min(x => x.Date.Year),
            LastYear = holidays.Max(x => x.Date.Year),
            SourceUrl = json.RootElement.GetProperty("csvUrl").GetString() ?? ""
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>日次または明示更新を実行する。手動操作は反映直前にも現在の管理者権限を検証する</summary>
    /// <returns>手動操作の認証が失効した場合はfalse。取得失敗は管理状態へ記録する</returns>
    public async Task<bool> UpdateAsync(ClaimsPrincipal? administrator, CancellationToken cancellationToken)
    {
        await updates.Semaphore.WaitAsync(cancellationToken);
        try
        {
            var state = await db.HolidayStates.SingleAsync(cancellationToken);
            var now = clock.GetUtcNow();
            if (administrator is null && state.LastAttemptAt > now.AddDays(-1).ToUnixTimeMilliseconds())
            {
                return true;
            }

            IReadOnlyList<Holiday>? holidays = null;
            string? url = null;
            string? error = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                (url, holidays) = await source.FetchAsync(timeout.Token);
                if (holidays.Min(x => x.Date.Year) > state.FirstYear || holidays.Max(x => x.Date.Year) < state.LastYear)
                {
                    throw new InvalidDataException("保存済みの収録範囲が欠落しているため採用しませんでした。");
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException
                or JsonException or KeyNotFoundException or InvalidOperationException or System.Text.DecoderFallbackException
                or Microsoft.VisualBasic.FileIO.MalformedLineException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning((int)OperationalEvent.HolidayUpdateFailed, exception, "祝日データを更新できませんでした。保存済みデータを維持します。");
                error = exception is InvalidDataException ? exception.Message : "取得・検証に失敗しました。接続先やサーバーログを確認してください。";
            }

            // 通信中は日記等の保存を妨げず、DB入れ替え時だけ共通の書込み排他を取得する。
            await writes.Semaphore.WaitAsync(cancellationToken);
            try
            {
                if (administrator is not null && (await sessions.CurrentUserAsync(administrator))?.IsAdmin != true)
                {
                    return false;
                }

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                state.LastAttemptAt = now.ToUnixTimeMilliseconds();
                state.Error = error;
                if (error is null && holidays is not null && url is not null)
                {
                    await tasks.FreezePastAsync(cancellationToken);
                    await db.Holidays.ExecuteDeleteAsync(cancellationToken);
                    db.Holidays.AddRange(holidays);
                    state.FirstYear = holidays.Min(x => x.Date.Year);
                    state.LastYear = holidays.Max(x => x.Date.Year);
                    state.SourceUrl = url;
                    state.LastSuccessAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
                }

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            finally
            {
                writes.Semaphore.Release();
            }

            return true;
        }
        finally
        {
            updates.Semaphore.Release();
        }
    }
}

/// <summary>日次確認の時刻を保存し、再起動による重複取得を抑える</summary>
public sealed class HolidayMaintenance(IServiceScopeFactory scopes, TimeProvider clock, ILogger<HolidayMaintenance> logger) : BackgroundService
{
    /// <summary>起動後の最初の周期と以後1時間ごとに、日次確認が必要か判定する</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<HolidayUpdates>().UpdateAsync(null, stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError((int)OperationalEvent.HolidayStateFailed, exception, "祝日更新の状態を保存できませんでした。次の周期で再確認します。");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 中断した更新は確定せず、次回起動後の周期で確認する。
        }
    }
}
