using System.Net;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>監査画面の認可・保存期間・条件指定・削除済み利用者の表示</summary>
public sealed class AuditTests
{
    /// <summary>管理者だけが90日以内を条件付きで50件ずつ参照できる</summary>
    [Test]
    public async Task AdminAuditFiltersAndPaginatesWithoutRecoveringDeletedNames()
    {
        await using var app = new TestApplication();
        using var admin = app.CreateBrowser();
        using var member = app.CreateBrowser();
        await AuthClient.RegisterAsync(admin);
        await AuthClient.RegisterAsync(member, "member", false);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = app.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        for (var i = 0; i < 51; i++)
        {
            db.SecurityAuditLogs.Add(new SecurityAuditLog { OccurredAt = now - i, Event = "export.backup", ActorId = "removed-user", SubjectId = "removed-user", Succeeded = true });
        }

        db.SecurityAuditLogs.Add(new SecurityAuditLog { OccurredAt = app.Clock.GetUtcNow().AddDays(-90).ToUnixTimeMilliseconds(), Event = "expired-event" });
        await db.SaveChangesAsync();

        using var denied = await member.GetAsync("/admin/audit");
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var first = WebUtility.HtmlDecode(await admin.GetStringAsync("/admin/audit?type=export.backup&user=removed-user"));
        var second = WebUtility.HtmlDecode(await admin.GetStringAsync("/admin/audit?type=export.backup&user=removed-user&page=2"));
        Assert.Multiple(() =>
        {
            Assert.That(first, Does.Contain("次の50件").And.Contain("削除済みユーザー（removed-user）").And.Not.Contain("expired-event"));
            Assert.That(first.Split("class=\"audit-row\"").Length - 1, Is.EqualTo(50));
            Assert.That(second.Split("class=\"audit-row\"").Length - 1, Is.EqualTo(1));
            Assert.That(second, Does.Not.Contain("次の50件"));
        });

        var future = DateOnly.FromDateTime(app.Clock.GetLocalNow().DateTime).AddDays(1);
        var filtered = await admin.GetStringAsync($"/admin/audit?from={future:yyyy-MM-dd}");
        Assert.That(filtered, Does.Not.Contain("class=\"audit-row\""));
        await db.Users.Where(x => x.UserName == "owner").ExecuteUpdateAsync(set => set.SetProperty(x => x.IsAdmin, false));
        using var revoked = await admin.GetAsync("/admin/audit");
        Assert.That(revoked.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }
}
