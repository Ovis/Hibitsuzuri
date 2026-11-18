using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.Identity;

namespace Hibitsuzuri.Web.Auth;

/// <summary>パスワードだけの失敗回数と停止期間を管理する</summary>
public sealed class PasswordAuthentication(UserManager<AppUser> users, AppDbContext db, TimeProvider clock, AuthAudit audit)
{
    // 存在しないIDも同じハッシュ検証を通し、即時応答による列挙を避ける。
    private static readonly string DummyHash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), AuthRules.Token());

    /// <summary>通常ログインと再認証に共通のパスワード確認。Passkeyにはこの制限を適用しない</summary>
    public async Task<AppUser?> VerifyAsync(HttpContext context, string loginId, string password)
    {
        if (!AuthRules.ValidText(password, 1, 128) || !AuthRules.ValidLoginId(loginId))
        {
            return null;
        }

        var user = await users.FindByNameAsync(loginId);
        if (user is null)
        {
            users.PasswordHasher.VerifyHashedPassword(new AppUser(), DummyHash, password);

            return null;
        }

        var now = clock.GetUtcNow();
        if (user.IsSuspended || user.LockoutEnd > now)
        {
            return null;
        }

        if (user.LockoutEnd != null)
        {
            user.AccessFailedCount = 0;
            user.LockoutEnd = null;
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            user.AccessFailedCount++;
            if (user.AccessFailedCount >= 5)
            {
                user.AccessFailedCount = 0;
                user.LockoutEnd = now.AddMinutes(5);
                audit.Add(context, "password-lockout", user.Id);
            }

            await db.SaveChangesAsync();

            return null;
        }

        user.AccessFailedCount = 0;
        user.LockoutEnd = null;

        await db.SaveChangesAsync();

        return user;
    }
}
