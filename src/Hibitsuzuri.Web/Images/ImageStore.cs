using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Hosting;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Images;

/// <summary>画像の所有者・本文参照・下書き期限を一元管理する</summary>
public sealed class ImageStore(AppDbContext database, DataDirectory data, DiaryMarkdown markdown, TimeProvider clock)
{
    /// <summary>ユーザー入力をパスへ混入させず保存先を決める</summary>
    public string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw new ArgumentException("画像IDが不正です。", nameof(id));
        }

        return Path.Combine(data.ImagesPath, id);
    }

    /// <summary>ゴミ箱を含む現行版と保持中の過去版が参照する画像を集める</summary>
    public async Task<HashSet<string>> SavedReferencesAsync(string? owner = null)
    {
        var ids = await database.DiaryImageReferences.Where(x => owner == null
            || database.DiaryImages.Any(i => i.Id == x.ImageId && i.UserId == owner)).Select(x => x.ImageId).Distinct().ToListAsync();

        var saved = ids.ToHashSet(StringComparer.Ordinal);
        var templates = await database.Notebooks.IgnoreQueryFilters().Where(x => owner == null || x.UserId == owner)
            .Select(x => new { x.UserId, x.Template }).ToListAsync();
        foreach (var group in templates.GroupBy(x => x.UserId))
        {
            var candidates = group.SelectMany(x => markdown.ImageIds(x.Template)).Distinct().ToArray();
            saved.UnionWith(await database.DiaryImages.Where(x => x.UserId == group.Key && candidates.Contains(x.Id)).Select(x => x.Id).ToListAsync());
        }

        return saved;
    }

    /// <summary>確定した現行版・過去5版と同じトランザクションで保持集合を更新する</summary>
    public async Task UpdateReferencesAsync(DiaryEntry entry)
    {
        var revisions = await database.DiaryRevisions.Where(x => x.EntryId == entry.Id).Select(x => x.Content).ToListAsync();
        var ids = revisions.Append(entry.Content).SelectMany(markdown.ImageIds).ToHashSet(StringComparer.Ordinal);
        // 画像機能導入前の本文に書かれた未登録IDを、保持対象として新規登録しない。
        var owner = await database.Notebooks.Where(x => x.Id == entry.NotebookId).Select(x => x.UserId).SingleAsync();
        var registered = await database.DiaryImages.Where(x => ids.Contains(x.Id) && x.UserId == owner).Select(x => x.Id).ToListAsync();
        ids.IntersectWith(registered);
        var references = await database.DiaryImageReferences.Where(x => x.EntryId == entry.Id).ToListAsync();
        database.DiaryImageReferences.RemoveRange(references.Where(x => !ids.Contains(x.ImageId)));
        database.DiaryImageReferences.AddRange(ids.Where(x => references.All(r => r.ImageId != x))
            .Select(x => new DiaryImageReference { EntryId = entry.Id, ImageId = x }));
        await database.SaveChangesAsync();
    }

    /// <summary>期限が切れた下書きだけの画像を、回収前でも利用不可にする</summary>
    public async Task<List<DiaryImage>> AvailableAsync(string owner, IReadOnlyCollection<string>? ids = null)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var wanted = ids?.ToArray();
        var templates = await database.Notebooks.IgnoreQueryFilters().Where(x => x.UserId == owner && x.Template != "")
            .Select(x => x.Template).ToListAsync();
        var templateImages = templates.SelectMany(markdown.ImageIds).ToHashSet(StringComparer.Ordinal);

        return await database.DiaryImages.AsNoTracking().Where(x => x.UserId == owner && (wanted == null || wanted.Contains(x.Id))
            && (database.DiaryImageReferences.Any(r => r.ImageId == x.Id) || templateImages.Contains(x.Id)
                || database.ImageDraftLeases.Any(l => l.ImageId == x.Id && l.ExpiresAt > now))).ToListAsync();
    }

    /// <summary>本人の有効な画像だけをHTMLへ含める</summary>
    public async Task<string> RenderAsync(string owner, string content)
    {
        var ids = markdown.ImageIds(content);
        var allowed = ids.Count == 0 ? [] : (await AvailableAsync(owner, ids)).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);

        return markdown.Render(content, allowed);
    }

    /// <summary>下書きに現在含まれる画像だけを30日間延長する。書き込みゲート内で呼ぶ</summary>
    public async Task<string[]> RenewAsync(string owner, string draftId, string content)
    {
        var ids = markdown.ImageIds(content);
        var available = (await AvailableAsync(owner, ids)).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var valid = ids.Where(available.Contains).ToHashSet(StringComparer.Ordinal);
        var leases = await database.ImageDraftLeases.Where(x => x.DraftId == draftId
            && database.DiaryImages.Any(i => i.Id == x.ImageId && i.UserId == owner)).ToListAsync();
        database.ImageDraftLeases.RemoveRange(leases.Where(x => !valid.Contains(x.ImageId)));

        var expires = clock.GetUtcNow().AddDays(30).ToUnixTimeMilliseconds();
        foreach (var id in valid)
        {
            var lease = leases.FirstOrDefault(x => x.ImageId == id);
            if (lease is null)
            {
                database.ImageDraftLeases.Add(new ImageDraftLease { ImageId = id, DraftId = draftId, ExpiresAt = expires });
            }
            else
            {
                lease.ExpiresAt = expires;
            }
        }

        await database.SaveChangesAsync();

        return ids.Where(x => !available.Contains(x)).ToArray();
    }

    /// <summary>本文・履歴・下書きのいずれにも参照されない画像と中断ファイルを回収する</summary>
    /// <remarks>アップロード・日記保存と同じAuthWriteGateを取得してから呼ぶ</remarks>
    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var saved = await SavedReferencesAsync();
        await database.ImageDraftLeases.Where(x => x.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        var leased = await database.ImageDraftLeases.Select(x => x.ImageId).Distinct().ToListAsync(cancellationToken);
        var images = await database.DiaryImages.ToListAsync(cancellationToken);
        var unused = images.Where(x => !saved.Contains(x.Id) && !leased.Contains(x.Id)).ToList();
        database.DiaryImages.RemoveRange(unused);
        await database.SaveChangesAsync(cancellationToken);

        // DBを先に確定する。ファイル削除に失敗しても次回は孤立ファイルとして再試行できる。
        var retained = images.Except(unused).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(data.ImagesPath))
        {
            var name = Path.GetFileName(file);
            if (!retained.Contains(name) && (Guid.TryParseExact(name, "N", out _) || name.StartsWith("work-", StringComparison.Ordinal)))
            {
                File.Delete(file);
            }
        }
    }
}

/// <summary>画像参照の変更と競合させず、起動時と毎時に孤立画像を回収する</summary>
public sealed class ImageMaintenance(IServiceScopeFactory scopes, AuthWriteGate gate, ILogger<ImageMaintenance> logger) : BackgroundService
{
    /// <summary>失敗を記録し、参照が残る画像を削除せず次の巡回で再試行する</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            await gate.Semaphore.WaitAsync(stoppingToken);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ImageStore>().CleanupAsync(stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogError((int)OperationalEvent.ImageCleanupFailed, error, "不要な画像の回収に失敗しました。");
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
