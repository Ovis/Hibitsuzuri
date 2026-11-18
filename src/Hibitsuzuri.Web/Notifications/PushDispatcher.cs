using System.Text.Json;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Hosting;
using Hibitsuzuri.Web.Storage;
using Hibitsuzuri.Web.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Notifications;

/// <summary>通知対象を再評価し、予定から1時間以内に限って永続台帳から配信する</summary>
public sealed class PushDispatcher(AppDbContext db, TaskReader tasks, TaskSchedule schedule, PushSubscriptions subscriptions,
    IPushTransport transport, TimeProvider clock, AuthWriteGate gate)
{
    /// <summary>ブラウザごとに直近の対象だけを登録する。古い未送信分を起動時に追いかけない</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await gate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var devices = await db.PushDevices.AsNoTracking().Where(x => x.Enabled
                && db.Users.Any(u => u.Id == x.UserId && !u.IsSuspended && u.RecoveryCodesConfirmed)).ToListAsync(cancellationToken);
            foreach (var owner in devices.GroupBy(x => x.UserId))
            {
                var books = await db.Notebooks.Where(x => x.UserId == owner.Key && x.TasksPausedOn == null
                    && db.TaskSeries.Any(s => s.NotebookId == x.Id && db.TaskSegments.Any(g => g.SeriesId == s.Id && g.ReminderJson != "[]")))
                    .Select(x => x.Id).ToArrayAsync(cancellationToken);
                await schedule.EnsureAsync(books, TaskSchedule.Offset(schedule.Today, -1), TaskSchedule.Offset(schedule.Today, 365), true);
                var rows = await tasks.Rows(owner.Key).Where(x => !x.ReadOnly && x.CompletedAt == null && x.ReminderJson != "[]"
                    && (x.DueDate >= TaskSchedule.Offset(schedule.Today, -1) || db.TaskRecords.Any(r => r.Id == x.Id)))
                    .ToListAsync(cancellationToken);
                foreach (var device in owner)
                {
                    var existing = await db.PushDeliveries.Where(x => x.DeviceId == device.Id && x.Generation == device.Generation
                        && x.ScheduledAt >= now - 3600000).Select(x => new { x.TaskId, x.ReminderId, x.ScheduledAt }).ToListAsync(cancellationToken);
                    var keys = existing.Select(x => (x.TaskId, x.ReminderId, x.ScheduledAt)).ToHashSet();
                    foreach (var row in rows)
                    {
                        foreach (var reminder in Reminders.Read(row.ReminderJson))
                        {
                            if (Reminders.Resolve(reminder, row.DueDate, clock.LocalTimeZone) is { } at && at <= now && at >= now - 3600000
                                && at > device.EnabledSince && at > row.NotificationAfter && keys.Add((row.Id, reminder.Id, at)))
                            {
                                db.PushDeliveries.Add(subscriptions.Create(device, row.Id, reminder.Id, at));
                            }
                        }
                    }
                }
            }

            await db.PushDeliveries.Where(x => x.ScheduledAt < now - 7 * 86400000L).ExecuteDeleteAsync(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            gate.Semaphore.Release();
        }

        var pending = await db.PushDeliveries.AsNoTracking().Where(x => !x.Finished && x.NextAttemptAt <= clock.GetUtcNow().ToUnixTimeMilliseconds())
            .OrderBy(x => x.ScheduledAt).Take(50).Select(x => x.Id).ToListAsync(cancellationToken);
        foreach (var id in pending)
        {
            await SendAsync(id, cancellationToken);
        }
    }

    /// <summary>完了・削除・停止・設定変更後の待機中通知を取り消す</summary>
    public async Task<bool> EligibleAsync(PushDelivery delivery, PushDevice device, CancellationToken cancellationToken)
    {
        if (!device.Enabled || device.Generation != delivery.Generation
            || !await db.Users.AnyAsync(x => x.Id == device.UserId && !x.IsSuspended && x.RecoveryCodesConfirmed, cancellationToken))
        {
            return false;
        }

        if (delivery.Test)
        {
            return true;
        }

        var row = await tasks.Rows(device.UserId).SingleOrDefaultAsync(x => x.Id == delivery.TaskId, cancellationToken);
        return row is not null && !row.ReadOnly && row.CompletedAt is null && delivery.ScheduledAt > device.EnabledSince
            && delivery.ScheduledAt > row.NotificationAfter && Reminders.Read(row.ReminderJson).Any(x => x.Id == delivery.ReminderId
                && Reminders.Resolve(x, row.DueDate, clock.LocalTimeZone) == delivery.ScheduledAt);
    }

    /// <summary>変更操作と競合しないよう最終確認から送信までを短い排他区間にする</summary>
    private async Task SendAsync(string id, CancellationToken cancellationToken)
    {
        await gate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            db.ChangeTracker.Clear();
            var delivery = await db.PushDeliveries.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (delivery is null || delivery.Finished)
            {
                return;
            }

            var device = await db.PushDevices.SingleAsync(x => x.Id == delivery.DeviceId, cancellationToken);
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            if (now > delivery.ScheduledAt + 3600000 || !await EligibleAsync(delivery, device, cancellationToken))
            {
                delivery.Finished = true;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            // 外部サービスの受付直後に停止しても、同じ識別子・Topicで再試行する。
            delivery.Attempts++;
            delivery.NextAttemptAt = now + Math.Min(300000, 30000L * delivery.Attempts);
            await db.SaveChangesAsync(cancellationToken);
            var row = delivery.Test ? null : await tasks.Rows(device.UserId).SingleAsync(x => x.Id == delivery.TaskId, cancellationToken);
            var payload = JsonSerializer.Serialize(new
            {
                id = delivery.Id,
                device = device.Id,
                generation = device.Generation,
                expires = delivery.ScheduledAt + 10800000,
                title = delivery.Test ? "Hibitsuzuriのテスト通知" : "Hibitsuzuri",
                body = delivery.Test ? "このブラウザで通知を受け取れます。" : device.ShowName ? row?.Title : "タスクの予定時刻になりました。",
                receipt = $"/api/push/receipt/{delivery.Id}?token={subscriptions.Token(delivery)}",
                url = delivery.Test ? "/settings/notifications" : $"/tasks?bookId={row?.NotebookId}&taskId={Uri.EscapeDataString(delivery.TaskId)}"
            });
            var ttl = (int)Math.Max(1, (delivery.ScheduledAt + 10800000 - now) / 1000);
            var status = await transport.SendAsync(subscriptions.Read(device), payload, delivery.Id, ttl, cancellationToken);
            if (status is >= 200 and < 300)
            {
                delivery.SentAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
                delivery.Finished = true;
                device.Error = null;
            }
            else if (status is 404 or 410)
            {
                device.Enabled = false;
                device.Generation = Guid.NewGuid().ToString("N");
                device.Error = "ブラウザの購読が失効しました。通知を有効にし直してください。";
                delivery.Finished = true;
            }
            else
            {
                delivery.Finished = status < 500 && status != 429;
                device.Error = delivery.Finished ? "通知を送れませんでした。ブラウザの通知を有効にし直してください。"
                    : "通知サービスへ接続できません。予定から1時間以内に再試行します。";
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            gate.Semaphore.Release();
        }
    }
}

/// <summary>Webリクエストがなくても通知を評価する単一ホストの定期処理</summary>
public sealed class PushMaintenance(IServiceScopeFactory scopes, TimeProvider clock, ILogger<PushMaintenance> logger) : BackgroundService
{
    /// <summary>失敗を記録し、次周期で台帳から継続する。秘密の送信先をログに出さない</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PushDispatcher>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError((int)OperationalEvent.PushProcessingFailed, exception, "通知処理に失敗しました。");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                break;
            }
        }
    }
}
