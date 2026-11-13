using System.Text.Json;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Calendar;
using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Images;
using Hibitsuzuri.Web.Notifications;
using Hibitsuzuri.Web.Storage;
using Hibitsuzuri.Web.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Backups;

/// <summary>検証済みデータを新しいIDへ割り当て、一度だけ原子的に反映する</summary>
public sealed class BackupRestore(AppDbContext db, BackupService backups, ImageStore images, DiaryMarkdown markdown, AuthAudit audit, TimeProvider clock)
{
    /// <summary>ファイルを先に用意し、記録・通知停止・確定済み結果を同時にコミットする</summary>
    /// <remarks>所有者の認証状態の再確認とAuthWriteGate取得は呼び出し元で行う</remarks>
    public async Task ApplyAsync(string id, string owner, BackupInspection inspection, HttpContext context, CancellationToken token)
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
            var position = await db.Notebooks.Where(x => x.UserId == owner).Select(x => (int?)x.Position).MaxAsync(token) ?? -1;
            var positions = inspection.Books.Values.OrderBy(x => x.Position).ThenBy(x => x.Id)
                .Select((book, index) => (book.Id, Position: position + index + 1)).ToDictionary(x => x.Id, x => x.Position);
            foreach (var (_, book) in archive.Read<Notebook>("books"))
            {
                book.Position = positions[book.Id];
                book.Id = books[book.Id];
                book.UserId = owner;
                book.Version = NewId();
                book.Template = Rewrite(book.Template, imageIds);
                book.NotificationAfter = now;
                await InsertAsync(book, token);
            }

            await FlushAsync(token);
            foreach (var (_, entry) in archive.Read<DiaryEntry>("entries"))
            {
                entry.Id = entries[entry.Id];
                entry.NotebookId = books[entry.NotebookId];
                entry.Version = NewId();
                entry.Content = Rewrite(entry.Content, imageIds);
                entry.PlainText = markdown.PlainText(entry.Content);
                entry.SearchText = DiarySearch.Normalize(entry.PlainText);
                await InsertAsync(entry, token);
            }

            await FlushAsync(token);
            foreach (var (_, revision) in archive.Read<DiaryRevision>("revisions"))
            {
                revision.Id = NewId();
                revision.EntryId = entries[revision.EntryId];
                revision.Content = Rewrite(revision.Content, imageIds);
                await InsertAsync(revision, token);
            }

            foreach (var (_, image) in archive.Read<DiaryImage>("images"))
            {
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

            foreach (var (_, item) in archive.Read<TaskSeries>("series"))
            {
                item.Id = series[item.Id];
                item.NotebookId = books[item.NotebookId];
                item.Version = NewId();
                await InsertAsync(item, token);
            }

            await FlushAsync(token);
            foreach (var (_, segment) in archive.Read<TaskSegment>("segments"))
            {
                segment.Id = segments[segment.Id];
                segment.SeriesId = series[segment.SeriesId];
                segment.Version = NewId();
                segment.SearchText = DiarySearch.Normalize(segment.Title + "\n" + segment.Memo);
                segment.ReminderJson = RemapReminders(segment.ReminderJson);
                await InsertAsync(segment, token);
            }

            await FlushAsync(token);
            foreach (var (_, task) in archive.Read<TaskRecord>("tasks"))
            {
                task.NotebookId = books[task.NotebookId];
                task.Version = NewId();
                task.NotificationAfter = now;
                task.SearchText = DiarySearch.Normalize(task.Title + "\n" + task.Memo);
                task.ReminderJson = RemapReminders(task.ReminderJson);
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

            foreach (var (_, slot) in archive.Read<TaskSlot>("slots"))
            {
                slot.SegmentId = segments[slot.SegmentId];
                slot.Id = TaskSchedule.Key(slot.SegmentId, slot.ScheduledDate);
                slot.Version = NewId();
                slot.NotificationAfter = now;
                await InsertAsync(slot, token);
            }

            foreach (var (_, pause) in archive.Read<TaskPause>("pauses"))
            {
                pause.Id = NewId();
                pause.NotebookId = books[pause.NotebookId];
                await InsertAsync(pause, token);
            }

            foreach (var (_, anniversary) in archive.Read<Anniversary>("anniversaries"))
            {
                anniversary.Id = NewId();
                anniversary.UserId = owner;
                anniversary.Version = NewId();
                foreach (var link in anniversary.Notebooks)
                {
                    link.AnniversaryId = anniversary.Id;
                    link.NotebookId = books[link.NotebookId];
                }

                await InsertAsync(anniversary, token);
            }

            await FlushAsync(token);
            await PushSubscriptions.DisableAsync(db.PushDevices.Where(x => x.UserId == owner));
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

    /// <summary>復元ごとに新しい排他トークンと所有範囲を作る</summary>
    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>存在しない画像の参照も新しい未登録IDへ置き換える</summary>
    private string Rewrite(string content, IReadOnlyDictionary<string, string> ids) => markdown.RewriteImages(content, id => "attachment:" + ids[id]);

    /// <summary>配信履歴から独立した通知設定として復元する</summary>
    private static string RemapReminders(string json)
    {
        var reminders = Reminders.Read(json);
        foreach (var reminder in reminders)
        {
            reminder.Id = NewId();
        }

        return JsonSerializer.Serialize(reminders);
    }

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
