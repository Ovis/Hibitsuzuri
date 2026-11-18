using System.Security.Claims;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>管理者が重要操作を日付・種別・利用者で絞り込む読み取り専用画面</summary>
public sealed class AuditController(AppDbContext db, TimeProvider clock) : Controller
{
    /// <summary>監査の本文へ認証秘密や日記を含めず、90日以内を50件ずつ表示する</summary>
    [HttpGet("/admin/audit")]
    public async Task<IActionResult> Index(DateOnly? from, DateOnly? through, string? type, string? user, int page = 1)
    {
        var owner = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!await db.Users.AnyAsync(x => x.Id == owner && x.IsAdmin && !x.IsSuspended))
        {
            return Forbid();
        }

        if (!ModelState.IsValid || page is < 1 or > 100000 || from > through || type?.Length > 100 || user?.Length > 256)
        {
            return BadRequest("絞り込み条件を確認してください。");
        }

        var cutoff = clock.GetUtcNow().AddDays(-90).ToUnixTimeMilliseconds();
        var query = db.SecurityAuditLogs.AsNoTracking().Where(x => x.OccurredAt > cutoff);
        if (from is { } first)
        {
            var boundary = Boundary(first);
            query = query.Where(x => x.OccurredAt >= boundary);
        }

        if (through is { } last && last != DateOnly.MaxValue)
        {
            var boundary = Boundary(last.AddDays(1));
            query = query.Where(x => x.OccurredAt < boundary);
        }

        if (!string.IsNullOrEmpty(type))
        {
            query = query.Where(x => x.Event == type);
        }

        if (!string.IsNullOrEmpty(user))
        {
            query = query.Where(x => x.ActorId == user || x.SubjectId == user);
        }

        var rows = await query.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id).Skip((page - 1) * 50).Take(51).ToListAsync();
        return View(new AuditPage
        {
            Rows = rows.Take(50).ToList(),
            Users = await db.Users.ToDictionaryAsync(x => x.Id, x => x.UserName ?? x.Id),
            Types = await db.SecurityAuditLogs.Where(x => x.OccurredAt > cutoff).Select(x => x.Event).Distinct().Order().ToListAsync(),
            From = from,
            Through = through,
            Type = type,
            User = user,
            Page = page,
            HasMore = rows.Count > 50,
            Zone = clock.LocalTimeZone
        });
    }

    /// <summary>表示日付をサーバーのタイムゾーンでUTCへ変換する</summary>
    private long Boundary(DateOnly day) => new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), clock.LocalTimeZone)).ToUnixTimeMilliseconds();
}

/// <summary>監査画面の絞り込み条件とページング結果</summary>
public sealed class AuditPage
{
    /// <summary>一画面50件までのイベント</summary>
    public required IReadOnlyList<SecurityAuditLog> Rows
    {
        get; init;
    }

    /// <summary>現存する利用者の表示名。削除済みは履歴から名前を復元しない</summary>
    public required IReadOnlyDictionary<string, string> Users
    {
        get; init;
    }

    /// <summary>保存期間内のイベント分類</summary>
    public required IReadOnlyList<string> Types
    {
        get; init;
    }

    /// <summary>開始日を含む絞り込み</summary>
    public DateOnly? From
    {
        get; init;
    }

    /// <summary>終了日を含む絞り込み</summary>
    public DateOnly? Through
    {
        get; init;
    }

    /// <summary>固定イベント名の完全一致</summary>
    public string? Type
    {
        get; init;
    }

    /// <summary>実行者または対象者の内部ID</summary>
    public string? User
    {
        get; init;
    }

    /// <summary>1から始まるページ番号</summary>
    public int Page
    {
        get; init;
    }

    /// <summary>次のページの有無</summary>
    public bool HasMore
    {
        get; init;
    }

    /// <summary>日時表示に使うサーバーのタイムゾーン</summary>
    public required TimeZoneInfo Zone
    {
        get; init;
    }

    /// <summary>削除済み利用者も内部IDで追跡できる表示</summary>
    public string Label(string? id) => id is null ? "未認証" : Users.TryGetValue(id, out var name) ? name + "（" + id + "）" : "削除済みユーザー（" + id + "）";
}
