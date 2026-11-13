using System.Net;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hibitsuzuri.Tests;

/// <summary>登録受付・管理者権限・利用停止と最後の管理者の保護</summary>
public sealed class UserManagementTests
{
    /// <summary>一般ユーザーの管理操作を拒否し、受付停止は既存利用へ影響しない</summary>
    [Test]
    public async Task RegistrationSwitchIsEnforcedOnServer()
    {
        await using var app = new TestApplication();
        using var admin = app.CreateBrowser();
        await AuthClient.RegisterAsync(admin);
        using var member = app.CreateBrowser();
        await AuthClient.RegisterAsync(member, "member", false);

        using var denied = await AuthClient.PostAsync(member, "/admin/users/registration", new()
        {
            ["enabled"] = "false"
        });

        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        using var closed = await AuthClient.PostAsync(admin, "/admin/users/registration", new()
        {
            ["enabled"] = "false"
        });

        Assert.That(closed.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        using var newcomer = app.CreateBrowser();
        using var blocked = await AuthClient.PostAsync(newcomer, "/auth/register", new()
        {
            ["LoginId"] = "newcomer",
            ["Password"] = AuthClient.Password,
            ["PasswordConfirmation"] = AuthClient.Password
        });

        Assert.That(blocked.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        using var retained = await member.GetAsync("/account");
        Assert.That(retained.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    /// <summary>最後の管理者への権限解除・停止・削除をすべて拒否する</summary>
    [TestCase("revoke")]
    [TestCase("suspend")]
    [TestCase("delete")]
    public async Task LastAdministratorCannotBeLost(string operation)
    {
        await using var app = new TestApplication();
        using var admin = app.CreateBrowser();
        await AuthClient.RegisterAsync(admin);
        var id = await UserIdAsync(app, "owner");

        using var result = await ChangeAsync(admin, id, operation, "owner");

        Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        using var stillAdmin = await admin.GetAsync("/admin/users");
        Assert.That(stillAdmin.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    /// <summary>並行して管理者を停止しても利用可能な管理者を一人残す</summary>
    [Test]
    public async Task ConcurrentAdminRemovalPreservesOneAdministrator()
    {
        await using var app = new TestApplication();
        using var first = app.CreateBrowser();
        using var second = app.CreateBrowser();
        await AuthClient.RegisterAsync(first);
        await AuthClient.RegisterAsync(second, "second", false);
        var firstId = await UserIdAsync(app, "owner");
        var secondId = await UserIdAsync(app, "second");
        using var grant = await ChangeAsync(first, secondId, "grant");
        using var login = await AuthClient.LoginAsync(second, "second");

        var results = await Task.WhenAll(ChangeAsync(first, firstId, "suspend"), ChangeAsync(second, secondId, "suspend"));

        Assert.That(results.Select(x => x.StatusCode), Is.EquivalentTo(new[] { HttpStatusCode.Redirect, HttpStatusCode.Conflict }));

        await using var scope = app.Services.CreateAsyncScope();
        Assert.That(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync(x => x.IsAdmin && !x.IsSuspended), Is.EqualTo(1));

        foreach (var result in results)
        {
            result.Dispose();
        }
    }

    /// <summary>停止は既存セッションへ即座に反映し、再開時にも古いセッションを戻さない</summary>
    [Test]
    public async Task SuspensionAndResumeRequireFreshLogin()
    {
        await using var app = new TestApplication();
        using var admin = app.CreateBrowser();
        using var member = app.CreateBrowser();
        await AuthClient.RegisterAsync(admin);
        await AuthClient.RegisterAsync(member, "member", false);
        var id = await UserIdAsync(app, "member");

        using var stopped = await ChangeAsync(admin, id, "suspend");
        using var rejected = await member.GetAsync("/account");

        Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        using var loginBlocked = await AuthClient.LoginAsync(member, "member");
        Assert.That(loginBlocked.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var resumed = await ChangeAsync(admin, id, "resume");
        using var stillRejected = await member.GetAsync("/account");

        Assert.That(stillRejected.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        using var fresh = await AuthClient.LoginAsync(member, "member");
        Assert.That(fresh.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
    }

    /// <summary>削除は認証関連を回収し、監査には内部IDだけを残す</summary>
    [Test]
    public async Task DeletionRemovesCredentialsAndRetainsAuditByInternalId()
    {
        await using var app = new TestApplication();
        using var admin = app.CreateBrowser();
        using var member = app.CreateBrowser();
        await AuthClient.RegisterAsync(admin);
        await AuthClient.RegisterAsync(member, "member", false);
        var id = await UserIdAsync(app, "member");

        using var wrongName = await ChangeAsync(admin, id, "delete", "other");
        Assert.That(wrongName.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        using var deleted = await ChangeAsync(admin, id, "delete", "member");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(await db.Users.AnyAsync(x => x.Id == id), Is.False);
        Assert.That(await db.AuthSessions.AnyAsync(x => x.UserId == id), Is.False);
        Assert.That(await db.RecoveryCodes.AnyAsync(x => x.UserId == id), Is.False);
        Assert.That(await db.SecurityAuditLogs.AnyAsync(x => x.Event == "user-delete" && x.SubjectId == id), Is.True);
    }

    /// <summary>試験が作成したアカウントの内部IDを取得する</summary>
    private static async Task<string> UserIdAsync(TestApplication app, string name)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(x => x.UserName == name).Select(x => x.Id).SingleAsync();
    }

    /// <summary>確認内容を付けた管理操作を送る</summary>
    private static Task<HttpResponseMessage> ChangeAsync(HttpClient client, string id, string operation, string name = "")
    {
        return AuthClient.PostAsync(client, "/admin/users/change", new()
        {
            ["id"] = id,
            ["operation"] = operation,
            ["confirmed"] = "true",
            ["confirmationName"] = name
        });
    }
}
