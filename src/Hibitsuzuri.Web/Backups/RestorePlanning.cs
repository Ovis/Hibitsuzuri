using System.Security.Cryptography;
using System.Text.Json;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Backups;

/// <summary>復元方式・表示名・確認時点を署名付きの確認内容に結び付ける</summary>
public sealed class RestorePlanning(AppDbContext db, IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector _protector = protection.CreateProtector("Hibitsuzuri.RestorePlan.v1");

    /// <summary>所有者と日記帳の重複を検証し、画面へ示す変更内容を確定する</summary>
    public async Task<RestorePlan> CreateAsync(string owner, RestoreTransfer transfer, BackupInspection inspection,
        string mode, Dictionary<string, string>? targets, CancellationToken token)
    {
        if (inspection.IsMarkdown ? mode != "markdown" : mode is not ("append" or "replace"))
        {
            throw new InvalidDataException("このZIPに指定された復元方式は使えません。");
        }

        var existing = await db.Notebooks.AsNoTracking().Where(x => x.UserId == owner).OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync(token);
        targets ??= inspection.Books.Keys.ToDictionary(x => x, _ => "");
        if (!targets.Keys.ToHashSet().SetEquals(inspection.Books.Keys)
            || targets.Values.Any(x => x is null || (x != "" && (!inspection.IsMarkdown || existing.All(b => b.Id != x))))
            || targets.Values.Where(x => x != "").Distinct().Count() != targets.Values.Count(x => x != ""))
        {
            throw new InvalidDataException("日記帳の取り込み先を確認してください。同じ日記帳へ複数のフォルダーは取り込めません。");
        }

        var names = mode == "replace" ? new HashSet<string>(StringComparer.Ordinal) : existing.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var resolved = new Dictionary<string, string>();
        foreach (var book in inspection.Books.Values.OrderBy(x => x.Position).ThenBy(x => x.Id))
        {
            resolved[book.Id] = targets[book.Id] == "" ? UniqueName(book.Name, names) : existing.Single(x => x.Id == targets[book.Id]).Name;
        }

        return new RestorePlan(transfer.Id, owner, mode, targets, resolved, await SnapshotAsync(owner, token), transfer.ExpiresAt);
    }

    /// <summary>対象選択や確認時点をブラウザから改変できない確認票を発行する</summary>
    public string Protect(RestorePlan plan) => _protector.Protect(JsonSerializer.Serialize(plan, BackupArchive.Json));

    /// <summary>確認票を別アカウント・別ZIP・期限外で使用しない</summary>
    public RestorePlan Read(string? value, string owner, string id)
    {
        try
        {
            var plan = value is null ? null : JsonSerializer.Deserialize<RestorePlan>(_protector.Unprotect(value), BackupArchive.Json);
            if (plan is null || plan.Owner != owner || plan.Transfer != id || plan.ExpiresAt <= clock.GetUtcNow().ToUnixTimeMilliseconds())
            {
                throw new InvalidDataException("復元内容をもう一度確認してください。");
            }

            return plan;
        }
        catch (CryptographicException error)
        {
            throw new InvalidDataException("復元内容の確認が無効です。もう一度確認してください。", error);
        }
    }

    /// <summary>確認後の記録変更を検出する。再計算できる予定索引・配信状態は対象外</summary>
    public async Task<string> SnapshotAsync(string owner, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var books = db.Notebooks.IgnoreQueryFilters().Where(x => x.UserId == owner);
        var entries = db.DiaryEntries.Where(x => books.Any(b => b.Id == x.NotebookId));
        var series = db.TaskSeries.Where(x => books.Any(b => b.Id == x.NotebookId));
        await HashAsync(hash, books.OrderBy(x => x.Id), token);
        await HashAsync(hash, entries.OrderBy(x => x.Id), token);
        await HashAsync(hash, db.DiaryRevisions.Where(x => entries.Any(e => e.Id == x.EntryId)).OrderBy(x => x.Id), token);
        await HashAsync(hash, db.DiaryImages.Where(x => x.UserId == owner).OrderBy(x => x.Id), token);
        await HashAsync(hash, series.OrderBy(x => x.Id), token);
        await HashAsync(hash, db.TaskSegments.Where(x => series.Any(s => s.Id == x.SeriesId)).OrderBy(x => x.Id), token);
        await HashAsync(hash, db.TaskRecords.Where(x => books.Any(b => b.Id == x.NotebookId)).OrderBy(x => x.Id), token);
        await HashAsync(hash, db.TaskPauses.Where(x => books.Any(b => b.Id == x.NotebookId)).OrderBy(x => x.Id), token);
        await HashAsync(hash, db.Anniversaries.Where(x => x.UserId == owner).Include(x => x.Notebooks.OrderBy(n => n.NotebookId)).OrderBy(x => x.Id), token);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>本文全体を集積せず、一件ずつハッシュへ取り込む</summary>
    private static async Task HashAsync<T>(IncrementalHash hash, IQueryable<T> rows, CancellationToken token) where T : class
    {
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(typeof(T).Name));
        await foreach (var row in rows.AsNoTracking().AsAsyncEnumerable().WithCancellation(token))
        {
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(row));
            hash.AppendData([0]);
        }
    }

    /// <summary>既存名を保持し、100文字以内の連番名を確保する</summary>
    private static string UniqueName(string original, HashSet<string> used)
    {
        var name = original;
        for (var number = 2; !used.Add(name); number++)
        {
            var suffix = "-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var length = Math.Min(original.Length, 100 - suffix.Length);
            if (length < original.Length && char.IsHighSurrogate(original[length - 1]))
            {
                length--;
            }

            name = original[..length] + suffix;
        }

        return name;
    }
}

/// <summary>利用者が確認した復元内容。保護されたトークン内だけで受け渡す</summary>
/// <param name="Transfer">検証済みZIPの操作ID</param>
/// <param name="Owner">復元先の本人</param>
/// <param name="Mode">追加・置き換え・Markdown上書き</param>
/// <param name="Targets">元の日記帳から既存日記帳への対応。空文字は新規作成</param>
/// <param name="Names">確認画面で示した復元先の名前</param>
/// <param name="Snapshot">確認時点の記録のハッシュ</param>
/// <param name="ExpiresAt">確認票の有効期限</param>
public sealed record RestorePlan(string Transfer, string Owner, string Mode, Dictionary<string, string> Targets,
    Dictionary<string, string> Names, string Snapshot, long ExpiresAt);
