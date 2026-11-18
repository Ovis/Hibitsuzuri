using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Auth;

/// <summary>復旧専用の本人確認と、認証情報一式の原子的な切り替え</summary>
public sealed class AccountRecoveryService(AppDbContext db, UserManager<AppUser> users, AccountFlows flows,
    RecoveryCodes codes, SessionService sessions, AuthAudit audit, TimeProvider clock)
{
    /// <summary>コードを1回だけ消費し、通常セッションを発行せず復旧状態へ進む</summary>
    public async Task<bool> BeginAsync(HttpContext context, string loginId, string code)
    {
        if (!AuthRules.ValidLoginId(loginId) || code.Length > 128)
        {
            return false;
        }

        var user = await users.FindByNameAsync(loginId);
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();

        // 不存在でも同じ固定長ハッシュを計算し、応答では失敗の理由を分けない。
        var hash = RecoveryCodes.CodeHash(user?.Id ?? "unknown", code);
        if (user is null || user.IsSuspended || user.RecoveryBlockedUntil > now)
        {
            return false;
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        var consumed = await db.RecoveryCodes.Where(x => x.UserId == user.Id && x.Hash == hash).ExecuteDeleteAsync();
        if (consumed != 1)
        {
            user.RecoveryFailures++;
            if (user.RecoveryFailures >= 5)
            {
                user.RecoveryFailures = 0;
                user.RecoveryBlockedUntil = clock.GetUtcNow().AddMinutes(15).ToUnixTimeMilliseconds();
                audit.Add(context, "recovery-lockout", user.Id);
            }

            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            return false;
        }

        user.RecoveryFailures = 0;
        user.RecoveryBlockedUntil = 0;
        await flows.BeginAsync(context, user, "recover");

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return true;
    }

    /// <summary>新パスワード・旧資格情報失効・新コードを一括確定し、保存確認へ移す</summary>
    public async Task<string[]?> CompleteAsync(HttpContext context, string password)
    {
        var flow = await flows.GetAsync(context);
        if (flow is null || flow.Purpose != "recover" || !AuthRules.ValidText(password, 12, 128))
        {
            return null;
        }

        var user = await users.FindByIdAsync(flow.UserId);
        if (user is null)
        {
            return null;
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        var token = await users.GeneratePasswordResetTokenAsync(user);
        AuthRules.RequireSuccess(await users.ResetPasswordAsync(user, token, password));

        foreach (var passkey in await users.GetPasskeysAsync(user))
        {
            AuthRules.RequireSuccess(await users.RemovePasskeyAsync(user, passkey.CredentialId));
        }

        await sessions.RevokeAsync(user.Id);
        user.RecoveryCodesConfirmed = false;
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;

        var freshCodes = await codes.ReplaceAsync(user.Id);
        await flows.BeginAsync(context, user, "confirm-recovery");
        audit.Add(context, "account-recovery", user.Id);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return freshCodes;
    }
}
