using Hibitsuzuri.Web.Auth;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Storage;

/// <summary>Identity標準テーブルとアプリの永続状態を同じトランザクションで扱う</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    /// <summary>登録受付と初回セットアップ状態</summary>
    public DbSet<AccountPolicy> AccountPolicies => Set<AccountPolicy>();

    /// <summary>ログイン状態と明示失効の記録</summary>
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();

    /// <summary>未使用の復旧コード</summary>
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();

    /// <summary>通常認証とは別の短命な手続き</summary>
    public DbSet<AccountFlow> AccountFlows => Set<AccountFlow>();

    /// <summary>重要な認証操作の監査記録</summary>
    public DbSet<SecurityAuditLog> SecurityAuditLogs => Set<SecurityAuditLog>();

    /// <summary>標準Identityスキーマを維持し、アプリ独自の所有関係だけ追加する</summary>
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AccountPolicy>().HasData(new AccountPolicy());
        builder.Entity<RecoveryCode>().HasKey(x => x.Hash);
        builder.Entity<AccountFlow>().HasKey(x => x.TokenHash);
        builder.Entity<AuthSession>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<RecoveryCode>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<AccountFlow>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<AuthSession>().HasIndex(x => new { x.UserId, x.BrowserId });
        builder.Entity<AccountFlow>().HasIndex(x => x.ExpiresAt);
        builder.Entity<SecurityAuditLog>().HasIndex(x => x.OccurredAt);
    }
}
