using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Calendar;
using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Images;
using Hibitsuzuri.Web.Storage;
using Hibitsuzuri.Web.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Backups;

/// <summary>検証済みデータを本人の確認した復元先へ一度だけ原子的に反映する</summary>
public sealed class BackupRestore(AppDbContext db, BackupService backups, ImageStore images, DiaryMarkdown markdown, AuthAudit audit, TimeProvider clock)
{
    /// <summary>ファイルを先に用意し、記録・通知再開境界・確定済み結果を同時にコミットする</summary>
    /// <remarks>所有者の認証状態の再確認とAuthWriteGate取得は呼び出し元で行う</remarks>
    public async Task ApplyAsync(string id, string owner, BackupInspection inspection, RestorePlan plan, HttpContext context, CancellationToken token)
    {
        var books = Map(inspection.Books.Keys);
        var entries = Map(inspection.Entries.Keys);
        var series = Map(inspection.Series.Keys);
        var segments = Map(inspection.Segments.Keys);
        var imageIds = Map(inspection.ImageReferences);
        var copied = new List<string>();
        var committed = false;
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        using var archive = new BackupArchive(backups.PathFor(id));

        if (inspection.IsMarkdown)
        {
            foreach (var (source, target) in plan.Targets.Where(x => x.Value != ""))
            {
                books[source] = target;
            }

            if (!inspection.IncludesImages)
            {
                // ZIPの所有者情報は信用しない。現在の本人が利用可能な元画像だけに再接続する。
                foreach (var image in await images.AvailableAsync(owner, inspection.ImageReferences))
                {
                    if (File.Exists(images.PathFor(image.Id)))
                    {
                        imageIds[image.Id] = image.Id;
                    }
                }
            }
        }

        try
        {
            foreach (var image in inspection.Images.Values)
            {
                var destination = images.PathFor(imageIds[image.Id]);
                copied.Add(destination);
                await archive.ExtractAsync(image.Id, destination, token);
            }

            // DBが失敗しても既存ファイルには触れない。中断時の新規孤立ファイルは定期回収できる。
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            if (plan.Mode == "replace")
            {
                // 実体ファイルはコミット前に削除しない。失敗時は元のDB参照と画像が両方残る。
                // 確定後の孤立ファイルは既存の画像定期回収に委ね、削除失敗を復元失敗と混同しない。
                await db.Anniversaries.Where(x => x.UserId == owner).ExecuteDeleteAsync(token);
                await db.Notebooks.IgnoreQueryFilters().Where(x => x.UserId == owner).ExecuteDeleteAsync(token);
                await db.DiaryImages.Where(x => x.UserId == owner).ExecuteDeleteAsync(token);
                await db.TaskMutations.Where(x => x.UserId == owner).ExecuteDeleteAsync(token);
                await db.PushDeliveries.Where(x => db.PushDevices.Any(d => d.Id == x.DeviceId && d.UserId == owner)).ExecuteDeleteAsync(token);
            }

            var position = await db.Notebooks.Where(x => x.UserId == owner).Select(x => (int?)x.Position).MaxAsync(token) ?? -1;
            var positions = inspection.Books.Values.OrderBy(x => x.Position).ThenBy(x => x.Id)
                .Select((book, index) => (book.Id, Position: position + index + 1)).ToDictionary(x => x.Id, x => x.Position);
            foreach (var (_, saved) in archive.Read<BackupNotebook>("books"))
            {
                if (inspection.IsMarkdown && plan.Targets[saved.Id] != "")
                {
                    continue;
                }

                var book = BackupMapping.Restore(saved);
                book.Position = positions[book.Id];
                book.Id = books[book.Id];
                book.Name = plan.Names[saved.Id];
                book.UserId = owner;
                book.Template = Rewrite(book.Template, imageIds);
                book.NotificationAfter = now;
                await InsertAsync(book, token);
            }

            await FlushAsync(token);
            foreach (var (_, saved) in archive.Read<BackupEntry>("entries"))
            {
                var entry = BackupMapping.Restore(saved);
                entry.Id = entries[entry.Id];
                entry.NotebookId = books[entry.NotebookId];
                entry.Content = Rewrite(entry.Content, imageIds);
                entry.PlainText = markdown.PlainText(entry.Content);
                entry.SearchText = DiarySearch.Normalize(entry.PlainText);
                if (inspection.IsMarkdown)
                {
                    var current = await db.DiaryEntries.SingleOrDefaultAsync(x => x.NotebookId == entry.NotebookId && x.EntryDate == entry.EntryDate, token);
                    if (current is not null)
                    {
                        // 旧版を別履歴へ退避しない合意。版を変えることで開いている編集タブからの保存は競合にする。
                        await db.DiaryRevisions.Where(x => x.EntryId == current.Id).ExecuteDeleteAsync(token);
                        current.Content = entry.Content;
                        current.PlainText = entry.PlainText;
                        current.SearchText = entry.SearchText;
                        current.UpdatedAt = now;
                        current.DeletedAt = null;
                        current.Version = Guid.NewGuid().ToString("N");
                        entries[saved.Id] = current.Id;
                        await FlushAsync(token);
                        continue;
                    }
                }

                await InsertAsync(entry, token);
            }

            await FlushAsync(token);
            foreach (var (_, saved) in archive.Read<BackupRevision>("revisions"))
            {
                var revision = BackupMapping.Restore(saved);
                revision.Id = NewId();
                revision.EntryId = entries[revision.EntryId];
                revision.Content = Rewrite(revision.Content, imageIds);
                await InsertAsync(revision, token);
            }

            foreach (var (_, saved) in archive.Read<BackupImage>("images"))
            {
                var image = BackupMapping.Restore(saved);
                image.Id = imageIds[image.Id];
                image.UserId = owner;
                image.NotebookId = books[image.NotebookId];
                await InsertAsync(image, token);
            }

            await FlushAsync(token);
            foreach (var entryId in entries.Values)
            {
                var entry = await db.DiaryEntries.AsNoTracking().SingleAsync(x => x.Id == entryId, token);
                await images.UpdateReferencesAsync(entry);
                db.ChangeTracker.Clear();
            }

            foreach (var (_, saved) in archive.Read<BackupTaskSeries>("series"))
            {
                var item = BackupMapping.Restore(saved);
                item.Id = series[item.Id];
                item.NotebookId = books[item.NotebookId];
                await InsertAsync(item, token);
            }

            await FlushAsync(token);
            foreach (var (_, saved) in archive.Read<BackupTaskSegment>("segments"))
            {
                var segment = BackupMapping.Restore(saved);
                segment.Id = segments[segment.Id];
                segment.SeriesId = series[segment.SeriesId];
                segment.SearchText = DiarySearch.Normalize(segment.Title + "\n" + segment.Memo);
                await InsertAsync(segment, token);
            }

            await FlushAsync(token);
            foreach (var (_, saved) in archive.Read<BackupTask>("tasks"))
            {
                var task = BackupMapping.Restore(saved);
                task.NotebookId = books[task.NotebookId];
                task.NotificationAfter = now;
                task.SearchText = DiarySearch.Normalize(task.Title + "\n" + task.Memo);
                if (task.SeriesId is { } oldSeries && task.SegmentId is { } oldSegment && task.ScheduledDate is { } date)
                {
                    task.SeriesId = series[oldSeries];
                    task.SegmentId = segments[oldSegment];
                    task.Id = TaskSchedule.Key(task.SegmentId, date);
                }
                else
                {
                    task.Id = NewId();
                }

                await InsertAsync(task, token);
            }

            foreach (var (_, saved) in archive.Read<BackupTaskSlot>("slots"))
            {
                var slot = BackupMapping.Restore(saved);
                slot.SegmentId = segments[slot.SegmentId];
                slot.Id = TaskSchedule.Key(slot.SegmentId, slot.ScheduledDate);
                slot.NotificationAfter = now;
                await InsertAsync(slot, token);
            }

            foreach (var (_, saved) in archive.Read<BackupTaskPause>("pauses"))
            {
                var pause = BackupMapping.Restore(saved);
                pause.Id = NewId();
                pause.NotebookId = books[pause.NotebookId];
                await InsertAsync(pause, token);
            }

            foreach (var (_, saved) in archive.Read<BackupAnniversary>("anniversaries"))
            {
                var anniversary = BackupMapping.Restore(saved);
                anniversary.Id = NewId();
                anniversary.UserId = owner;
                if (plan.Mode == "append" && anniversary.AllNotebooks)
                {
                    anniversary.AllNotebooks = false;
                    anniversary.Notebooks = inspection.Books.Keys.Select(book => new AnniversaryNotebook
                    {
                        AnniversaryId = anniversary.Id,
                        NotebookId = book
                    }).ToList();
                }

                foreach (var link in anniversary.Notebooks)
                {
                    link.AnniversaryId = anniversary.Id;
                    link.NotebookId = books[link.NotebookId];
                }

                await InsertAsync(anniversary, token);
            }

            await FlushAsync(token);
            if (!inspection.IsMarkdown)
            {
                // 画像コピー等で復元に時間がかかっても、処理中に過ぎた通知を後追いしない。
                var restoredBooks = books.Values.ToArray();
                var completedAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
                await db.Notebooks.Where(x => restoredBooks.Contains(x.Id)).ExecuteUpdateAsync(set => set.SetProperty(x => x.NotificationAfter, completedAt), token);
            }

            var transfer = await db.RestoreTransfers.SingleAsync(x => x.Id == id && x.UserId == owner, token);
            transfer.Completed = true;
            audit.Add(context, "backup.restore", owner);
            await db.SaveChangesAsync(token);

            // 確定開始後の通信切断で、コミット済みの画像を失敗時の回収へ回さない。
            await transaction.CommitAsync(CancellationToken.None);
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                foreach (var path in copied)
                {
                    File.Delete(path);
                }
            }
        }
    }

    /// <summary>元のIDを別アカウントの既存データへ結び付けない</summary>
    private static Dictionary<string, string> Map(IEnumerable<string> ids) => ids.ToDictionary(x => x, _ => NewId(), StringComparer.Ordinal);

    /// <summary>復元元の記録と衝突しない新しい識別子を発行する</summary>
    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>存在しない画像の参照も新しい未登録IDへ置き換える</summary>
    private string Rewrite(string content, IReadOnlyDictionary<string, string> ids) => markdown.RewriteImages(content, id => "attachment:" + ids[id]);

    /// <summary>一括復元中も追跡する本文の数を制限する</summary>
    private async Task InsertAsync<T>(T row, CancellationToken token) where T : class
    {
        db.Add(row);
        if (db.ChangeTracker.Entries().Count() >= 100)
        {
            await FlushAsync(token);
        }
    }

    /// <summary>外部キーの親を先に保存する。コミットは全体の最後だけで行う</summary>
    private async Task FlushAsync(CancellationToken token)
    {
        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();
    }
}
