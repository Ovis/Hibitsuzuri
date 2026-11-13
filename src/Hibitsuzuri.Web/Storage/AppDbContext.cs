using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Calendar;
using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Images;
using Hibitsuzuri.Web.Tasks;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Storage;

/// <summary>Identity標準テーブルとアプリの永続状態を同じトランザクションで扱う</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    /// <summary>通常タスクと繰り返し各回の操作記録</summary>
    public DbSet<TaskRecord> TaskRecords => Set<TaskRecord>();

    /// <summary>区間をまたぐ繰り返しのまとまり</summary>
    public DbSet<TaskSeries> TaskSeries => Set<TaskSeries>();

    /// <summary>不変の繰り返しルール区間</summary>
    public DbSet<TaskSegment> TaskSegments => Set<TaskSegment>();

    /// <summary>必要な範囲に限定した予定の検索索引</summary>
    public DbSet<TaskSlot> TaskSlots => Set<TaskSlot>();

    /// <summary>計算済みの月と祝日の版</summary>
    public DbSet<TaskWindow> TaskWindows => Set<TaskWindow>();

    /// <summary>再送に返す確定済み操作結果</summary>
    public DbSet<TaskMutation> TaskMutations => Set<TaskMutation>();

    /// <summary>未操作予定の自動スキップに用いる停止期間</summary>
    public DbSet<TaskPause> TaskPauses => Set<TaskPause>();

    /// <summary>所有者だけが参照できる添付画像</summary>
    public DbSet<DiaryImage> DiaryImages => Set<DiaryImage>();

    /// <summary>下書きごとの画像の有効期限</summary>
    public DbSet<ImageDraftLease> ImageDraftLeases => Set<ImageDraftLease>();

    /// <summary>日記と保持中の履歴による画像参照</summary>
    public DbSet<DiaryImageReference> DiaryImageReferences => Set<DiaryImageReference>();

    /// <summary>本人の記念日と個別の表示先</summary>
    public DbSet<Anniversary> Anniversaries => Set<Anniversary>();

    /// <summary>公式の祝日・休日</summary>
    public DbSet<Holiday> Holidays => Set<Holiday>();

    /// <summary>祝日の収録期間と更新状況</summary>
    public DbSet<HolidayState> HolidayStates => Set<HolidayState>();

    /// <summary>所有者別の日記帳とテンプレート</summary>
    public DbSet<Notebook> Notebooks => Set<Notebook>();

    /// <summary>現在の本文と日記単位のゴミ箱</summary>
    public DbSet<DiaryEntry> DiaryEntries => Set<DiaryEntry>();

    /// <summary>日記ごとに保持する過去5版</summary>
    public DbSet<DiaryRevision> DiaryRevisions => Set<DiaryRevision>();

    /// <summary>保存要求の二重適用を防ぐ確定記録</summary>
    public DbSet<DiarySaveOperation> DiarySaveOperations => Set<DiarySaveOperation>();

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

        builder.Entity<TaskRecord>().HasOne<Notebook>().WithMany().HasForeignKey(x => x.NotebookId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<TaskRecord>().HasOne<TaskSeries>().WithMany().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<TaskRecord>().Property(x => x.Version).IsConcurrencyToken();
        builder.Entity<TaskRecord>().HasIndex(x => new { x.NotebookId, x.CompletedAt, x.Id });
        builder.Entity<TaskRecord>().HasIndex(x => new { x.SeriesId, x.ScheduledDate });
        builder.Entity<TaskRecord>().HasIndex(x => new { x.NotebookId, x.DueDate });

        builder.Entity<TaskSeries>().HasOne<Notebook>().WithMany().HasForeignKey(x => x.NotebookId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<TaskSeries>().Property(x => x.Version).IsConcurrencyToken();
        builder.Entity<TaskSegment>().HasOne<TaskSeries>().WithMany().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Cascade);

        builder.Entity<TaskSlot>().HasOne<TaskSegment>().WithMany().HasForeignKey(x => x.SegmentId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<TaskSlot>().HasIndex(x => new { x.SegmentId, x.DueDate });
        builder.Entity<TaskSlot>().HasIndex(x => new { x.SegmentId, x.ScheduledDate }).IsUnique();
        builder.Entity<TaskWindow>().HasKey(x => new { x.SegmentId, x.Month });
        builder.Entity<TaskWindow>().HasOne<TaskSegment>().WithMany().HasForeignKey(x => x.SegmentId).OnDelete(DeleteBehavior.Cascade);

        builder.Entity<TaskMutation>().HasKey(x => new { x.UserId, x.Id });
        builder.Entity<TaskMutation>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        builder.Entity<TaskPause>().HasOne<Notebook>().WithMany().HasForeignKey(x => x.NotebookId).OnDelete(DeleteBehavior.Cascade);

        builder.Entity<Holiday>().HasKey(x => x.Date);

        builder.Entity<DiaryImage>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<DiaryImage>().HasOne<Notebook>().WithMany().HasForeignKey(x => x.NotebookId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ImageDraftLease>().HasKey(x => new { x.ImageId, x.DraftId });
        builder.Entity<ImageDraftLease>().HasOne<DiaryImage>().WithMany().HasForeignKey(x => x.ImageId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ImageDraftLease>().HasIndex(x => x.ExpiresAt);
        builder.Entity<DiaryImageReference>().HasKey(x => new { x.ImageId, x.EntryId });
        builder.Entity<DiaryImageReference>().HasOne<DiaryImage>().WithMany().HasForeignKey(x => x.ImageId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<DiaryImageReference>().HasOne<DiaryEntry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);

        builder.Entity<Anniversary>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<Anniversary>().Property(x => x.Version).IsConcurrencyToken();
        builder.Entity<AnniversaryNotebook>().HasKey(x => new { x.AnniversaryId, x.NotebookId });
        builder.Entity<Anniversary>().HasMany(x => x.Notebooks).WithOne().HasForeignKey(x => x.AnniversaryId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<AnniversaryNotebook>().HasOne<Notebook>().WithMany().HasForeignKey(x => x.NotebookId).OnDelete(DeleteBehavior.Cascade);

        builder.Entity<Notebook>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<Notebook>().HasIndex(x => new { x.UserId, x.Name }).IsUnique();
        builder.Entity<Notebook>().Property(x => x.Version).IsConcurrencyToken();

        builder.Entity<DiaryEntry>().HasOne<Notebook>().WithMany().HasForeignKey(x => x.NotebookId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<DiaryEntry>().HasIndex(x => new { x.NotebookId, x.EntryDate }).IsUnique();
        builder.Entity<DiaryEntry>().Property(x => x.Version).IsConcurrencyToken();

        builder.Entity<DiaryRevision>().HasOne<DiaryEntry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<DiaryRevision>().HasIndex(x => new { x.EntryId, x.Sequence });

        builder.Entity<DiarySaveOperation>().HasKey(x => new { x.NotebookId, x.Id });
        builder.Entity<DiarySaveOperation>().HasOne<Notebook>().WithMany().HasForeignKey(x => x.NotebookId).OnDelete(DeleteBehavior.Cascade);

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
