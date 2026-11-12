using System.Security.Claims;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Auth;

/// <summary>固定期限の通常ログインと、毎リクエストの失効確認</summary>
public sealed class SessionService(AppDbContext db, UserManager<AppUser> users, SignInManager<AppUser> signIn, TimeProvider clock, IWebHostEnvironment environment)
{
    public const string SessionClaim = "hibitsuzuri:session";

    /// <summary>本人確認済みのユーザーへ固定期限のCookieを発行する</summary>
    public async Task SignInAsync(HttpContext context, AppUser user, bool rememberMe)
    {
        var now = clock.GetUtcNow();
        var browserCookie = CookieName("Browser");
        var browserId = context.Request.Cookies[browserCookie];
        if (browserId is null || browserId.Length != 64 || !browserId.All(char.IsAsciiHexDigit))
        {
            browserId = AuthRules.Token();
        }

        // 同じブラウザの古いログインを残さず、端末単位の失効を一意に扱う。
        await db.AuthSessions.Where(x => x.BrowserId == browserId && x.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAt, now.ToUnixTimeMilliseconds()));
        var session = new AuthSession
        {
            UserId = user.Id,
            SecurityStamp = await users.GetSecurityStampAsync(user),
            BrowserId = browserId,
            VerifiedAt = now.ToUnixTimeMilliseconds(),
            ExpiresAt = (rememberMe ? now.AddDays(30) : now.AddHours(8)).ToUnixTimeMilliseconds()
        };
        db.AuthSessions.Add(session);
        await db.SaveChangesAsync();

        context.Response.Cookies.Append(browserCookie, browserId, CookieOptions(TimeSpan.FromDays(365)));
        await signIn.SignInWithClaimsAsync(user, new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            IssuedUtc = now,
            ExpiresUtc = DateTimeOffset.FromUnixTimeMilliseconds(session.ExpiresAt),
            AllowRefresh = false
        }, [new Claim(SessionClaim, session.Id)]);
    }

    /// <summary>認証世代・停止状態・期限をDBで再確認する</summary>
    public async Task<AppUser?> CurrentUserAsync(ClaimsPrincipal principal)
    {
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var sessionId = principal.FindFirstValue(SessionClaim);
        var session = await db.AuthSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId && x.UserId == id);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (session is null || user is null || user.IsSuspended || !user.RecoveryCodesConfirmed
            || session.RevokedAt != null || session.ExpiresAt <= clock.GetUtcNow().ToUnixTimeMilliseconds()
            || session.SecurityStamp != user.SecurityStamp)
        {
            return null;
        }

        return user;
    }

    /// <summary>現在のセッションが10分以内に本人確認済みか調べる</summary>
    public async Task<bool> IsRecentAsync(ClaimsPrincipal principal)
    {
        var id = principal.FindFirstValue(SessionClaim);
        var cutoff = clock.GetUtcNow().AddMinutes(-10).ToUnixTimeMilliseconds();
        return await db.AuthSessions.AnyAsync(x => x.Id == id && x.VerifiedAt > cutoff && x.RevokedAt == null);
    }

    /// <summary>再認証の時刻だけ更新し、ログインの有効期限は延長しない</summary>
    public Task MarkVerifiedAsync(ClaimsPrincipal principal)
    {
        var id = principal.FindFirstValue(SessionClaim);
        return db.AuthSessions.Where(x => x.Id == id).ExecuteUpdateAsync(set => set.SetProperty(x => x.VerifiedAt, clock.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    /// <summary>全端末または指定セッション以外を強制失効する</summary>
    public Task RevokeAsync(string userId, string? exceptSession = null)
    {
        return db.AuthSessions.Where(x => x.UserId == userId && x.Id != exceptSession && x.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAt, clock.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    /// <summary>期限切れ後でも、このブラウザに残る通知対象を明示的に停止できる失効記録を残す</summary>
    public async Task SignOutAsync(HttpContext context)
    {
        var browserId = context.Request.Cookies[CookieName("Browser")];
        await db.AuthSessions.Where(x => x.BrowserId == browserId && x.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAt, clock.GetUtcNow().ToUnixTimeMilliseconds()));
        await signIn.SignOutAsync();
    }

    /// <summary>HTTPS本番環境ではHost限定Cookieを使用する</summary>
    public string CookieName(string suffix) => (environment.IsDevelopment() ? "" : "__Host-") + "Hibitsuzuri." + suffix;

    /// <summary>JavaScriptから読み取れない同一サイトの短命Cookie設定</summary>
    public CookieOptions CookieOptions(TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        Secure = !environment.IsDevelopment(),
        SameSite = SameSiteMode.Strict,
        Path = "/",
        MaxAge = lifetime
    };
}
