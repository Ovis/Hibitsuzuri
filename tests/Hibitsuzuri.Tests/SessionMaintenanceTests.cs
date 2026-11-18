using System.Net;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>無効な認証記録の回収と、通知購読・有効なログインの独立性を検証する</summary>
public sealed class SessionMaintenanceTests
{
    /// <summary>期限の境界と明示ログアウトを回収しても、監査・有効なログイン・自然失効端末の通知を保持する</summary>
    [Test]
    public async Task CleanupPreservesActiveSessionsAuditAndNaturallyExpiredPush()
    {
        await using var app = NotificationTests.Application(new RecordingPush());
        using var expired = app.CreateBrowser();
        using var loggedOut = app.CreateBrowser();
        using var active = app.CreateBrowser();
        await AuthClient.RegisterAsync(app, expired);
        await NotificationTests.EnableAsync(expired);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var expiredSession = await db.AuthSessions.AsNoTracking().SingleAsync();

        using var secondLogin = await AuthClient.LoginAsync(loggedOut, "owner");
        Assert.That(secondLogin.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        await NotificationTests.EnableAsync(loggedOut);
        var revokedSession = await db.AuthSessions.AsNoTracking().SingleAsync(x => x.Id != expiredSession.Id);
        using var logout = await AuthClient.PostAsync(loggedOut, "/auth/logout");
        Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        using var thirdLogin = await AuthClient.LoginAsync(active, "owner");
        Assert.That(thirdLogin.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        var now = app.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        await db.AuthSessions.Where(x => x.Id == expiredSession.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.ExpiresAt, now));
        var auditCount = await db.SecurityAuditLogs.CountAsync();

        await scope.ServiceProvider.GetRequiredService<SessionService>().PruneAsync();

        Assert.That(await db.AuthSessions.CountAsync(), Is.EqualTo(1));
        Assert.That(await db.AuthSessions.AnyAsync(x => x.Id == expiredSession.Id || x.Id == revokedSession.Id), Is.False);
        Assert.That(await db.PushDevices.AnyAsync(x => x.BrowserId == expiredSession.BrowserId && x.Enabled), Is.True);
        Assert.That(await db.PushDevices.AnyAsync(x => x.BrowserId == revokedSession.BrowserId && x.Enabled), Is.False);
        Assert.That(await db.SecurityAuditLogs.CountAsync(), Is.EqualTo(auditCount));

        using var expiredRead = await expired.GetAsync("/account");
        using var loggedOutRead = await loggedOut.GetAsync("/account");
        using var activeRead = await active.GetAsync("/account");
        Assert.That(expiredRead.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(loggedOutRead.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(activeRead.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}
