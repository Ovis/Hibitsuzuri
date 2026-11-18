using System.Security.Cryptography;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Auth;

/// <summary>十分な乱数強度を持つ復旧コードの発行と単回消費</summary>
public sealed class RecoveryCodes(AppDbContext db)
{
    /// <summary>旧セットを失効し、新しい10個を呼び出し元へ一度だけ返す</summary>
    public async Task<string[]> ReplaceAsync(string userId)
    {
        // 別タブの古い表示確認や、旧コードで始めた復旧状態を新セットへ流用させない。
        await db.AccountFlows.Where(x => x.UserId == userId).ExecuteDeleteAsync();
        await db.RecoveryCodes.Where(x => x.UserId == userId).ExecuteDeleteAsync();

        var codes = Enumerable.Range(0, 10).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(16))).ToArray();
        foreach (var code in codes)
        {
            db.RecoveryCodes.Add(new RecoveryCode { UserId = userId, Hash = CodeHash(userId, code) });
        }

        await db.SaveChangesAsync();

        return codes;
    }

    /// <summary>整形用の空白・ハイフンを除去し、所有者に結び付けてハッシュ化する</summary>
    public static string CodeHash(string userId, string code)
    {
        var normalized = code.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        return AuthRules.Hash(userId + ":" + normalized);
    }
}

/// <summary>復旧状態を通常セッションやコード平文から分離する</summary>
public sealed class AccountFlows(AppDbContext db, SessionService sessions, TimeProvider clock)
{
    /// <summary>ログアウト後に保存確認から再ログインできないよう、ブラウザの専用状態を消費する</summary>
    public async Task CancelAsync(HttpContext context)
    {
        var name = sessions.CookieName("Flow");
        if (context.Request.Cookies[name] is { } token)
        {
            await db.AccountFlows.Where(x => x.TokenHash == AuthRules.Hash(token)).ExecuteDeleteAsync();
        }

        context.Response.Cookies.Delete(name, sessions.CookieOptions(TimeSpan.Zero));
    }

    /// <summary>ブラウザにだけ乱数を渡し、DBには短命の検証情報を保存する</summary>
    public async Task BeginAsync(HttpContext context, AppUser user, string purpose, bool rememberMe = false)
    {
        var token = AuthRules.Token();
        var prior = context.Request.Cookies[sessions.CookieName("Flow")];
        if (prior is not null)
        {
            await db.AccountFlows.Where(x => x.TokenHash == AuthRules.Hash(prior)).ExecuteDeleteAsync();
        }

        db.AccountFlows.Add(new AccountFlow
        {
            TokenHash = AuthRules.Hash(token),
            UserId = user.Id,
            SecurityStamp = user.SecurityStamp ?? throw new InvalidOperationException("認証世代がありません。"),
            Purpose = purpose,
            RememberMe = rememberMe,
            ExpiresAt = clock.GetUtcNow().AddMinutes(10).ToUnixTimeMilliseconds()
        });
        await db.SaveChangesAsync();

        context.Response.Cookies.Append(sessions.CookieName("Flow"), token, sessions.CookieOptions(TimeSpan.FromMinutes(10)));
    }

    /// <summary>手続きの期限と認証世代を確認し、通常利用を許可しない状態として返す</summary>
    public async Task<AccountFlow?> GetAsync(HttpContext context)
    {
        var token = context.Request.Cookies[sessions.CookieName("Flow")];
        if (token is null || token.Length != 64)
        {
            return null;
        }

        var hash = AuthRules.Hash(token);
        var flow = await db.AccountFlows.SingleOrDefaultAsync(x => x.TokenHash == hash);
        if (flow is null || flow.ExpiresAt <= clock.GetUtcNow().ToUnixTimeMilliseconds()
            || !await db.Users.AnyAsync(x => x.Id == flow.UserId && !x.IsSuspended && x.SecurityStamp == flow.SecurityStamp))
        {
            return null;
        }

        return flow;
    }

    /// <summary>保存確認を単回で消費する</summary>
    public async Task EndAsync(HttpContext context, AccountFlow flow)
    {
        db.AccountFlows.Remove(flow);
        await db.SaveChangesAsync();

        context.Response.Cookies.Delete(sessions.CookieName("Flow"), sessions.CookieOptions(TimeSpan.Zero));
    }
}
