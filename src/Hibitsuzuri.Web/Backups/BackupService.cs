using System.Text;
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

/// <summary>認証情報を含まない個人バックアップとMarkdown出力を作成する</summary>
public sealed class BackupService(AppDbContext db, DataDirectory data, DiaryMarkdown markdown, ImageStore images,
    ImageWorker worker, TaskSchedule schedule, TimeProvider clock)
{
    /// <summary>公開領域と画像回収処理から分離した一時保存先</summary>
    public string DirectoryPath => Path.Combine(data.RootPath, "transfers");

    /// <summary>利用者入力をファイルパスへ混入させない</summary>
    public string PathFor(string id)
    {
        if (!BackupInspection.ValidId(id))
        {
            throw new InvalidDataException("操作IDが不正です。");
        }

        Directory.CreateDirectory(DirectoryPath);
        return Path.Combine(DirectoryPath, id + ".zip");
    }

    /// <summary>書き込みゲート内で、本文と添付の同じ時点のスナップショットを作る</summary>
    public async Task<string> ExportAsync(string owner, bool backup, string? bookId, CancellationToken cancellationToken)
    {
        var path = PathFor(Guid.NewGuid().ToString("N"));
        try
        {
            var books = await db.Notebooks.AsNoTracking().Where(x => x.UserId == owner && (backup || bookId == null || x.Id == bookId))
                .OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync(cancellationToken);
            if (!backup && bookId is not null && books.Count == 0)
            {
                throw new InvalidDataException("対象の日記帳がありません。");
            }

            var bookIds = books.Select(x => x.Id).ToArray();
            if (backup)
            {
                await schedule.EnsureAsync(bookIds, null, TaskSchedule.Offset(schedule.Today, 365), true);
            }

            using (var archive = new BackupArchive(path, true))
            {
                // 画像の元の日記帳が削除済みでも、含める本文からの参照があれば保持する。
                var needed = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var book in books)
                {
                    if (backup)
                    {
                        AddReferences(book.Template, book.Id, needed);
                        await archive.AddRecordAsync("books", BackupMapping.Export(book), cancellationToken);
                    }
                }

                var entries = db.DiaryEntries.AsNoTracking().Where(x => bookIds.Contains(x.NotebookId) && x.DeletedAt == null);
                await foreach (var entry in entries.AsAsyncEnumerable().WithCancellation(cancellationToken))
                {
                    AddReferences(entry.Content, entry.NotebookId, needed);
                    if (backup)
                    {
                        await archive.AddRecordAsync("entries", BackupMapping.Export(entry), cancellationToken);
                    }
                    else
                    {
                        var content = markdown.RewriteImages(entry.Content, id => $"../assets/{id}");
                        var header = $"---\ndate: {entry.EntryDate:yyyy-MM-dd}\n"
                            + $"created: {DateTimeOffset.FromUnixTimeMilliseconds(entry.CreatedAt):O}\n"
                            + $"updated: {DateTimeOffset.FromUnixTimeMilliseconds(entry.UpdatedAt):O}\n---\n\n";
                        using var input = new MemoryStream(Encoding.UTF8.GetBytes(header + content));
                        var book = books.Single(x => x.Id == entry.NotebookId);
                        await archive.AddAsync($"{FolderName(book)}/{entry.EntryDate:yyyy-MM-dd}.md", input, cancellationToken);
                    }

                    CheckCompressed(path);
                }

                if (backup)
                {
                    await foreach (var revision in db.DiaryRevisions.AsNoTracking().Where(x => entries.Any(e => e.Id == x.EntryId))
                        .AsAsyncEnumerable().WithCancellation(cancellationToken))
                    {
                        var book = await entries.Where(x => x.Id == revision.EntryId).Select(x => x.NotebookId).SingleAsync(cancellationToken);
                        AddReferences(revision.Content, book, needed);
                        await archive.AddRecordAsync("revisions", BackupMapping.Export(revision), cancellationToken);
                        CheckCompressed(path);
                    }

                    await ExportTasksAsync(archive, bookIds, owner, cancellationToken);
                }

                // 登録されていない過去の参照は本文に残す。ほかの利用者の画像は決して出力しない。
                foreach (var id in needed.Keys)
                {
                    var image = await db.DiaryImages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == owner, cancellationToken);
                    if (image is null)
                    {
                        continue;
                    }

                    if (backup)
                    {
                        if (!bookIds.Contains(image.NotebookId))
                        {
                            image.NotebookId = needed[id];
                        }

                        await archive.AddRecordAsync("images", BackupMapping.Export(image), cancellationToken);
                    }

                    await using var input = File.OpenRead(images.PathFor(id));
                    await archive.AddAsync("assets/" + id, input, cancellationToken);
                    CheckCompressed(path);
                }

                if (backup)
                {
                    await archive.FinishAsync(clock, cancellationToken);
                }
            }

            CheckCompressed(path);
            return path;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    /// <summary>名前が同じ・ファイル名に使えない文字がある場合も衝突しない日記帳フォルダー</summary>
    private static string FolderName(Notebook book)
    {
        var name = new string(book.Name.Take(40).Select(x => char.IsAsciiLetterOrDigit(x) || x is >= '\u3040' and <= '\u9fff' ? x : '_').ToArray());
        return name + "-" + book.Id;
    }

    /// <summary>参照された画像がどの日記帳へ属するかを保持する</summary>
    private void AddReferences(string content, string book, Dictionary<string, string> needed)
    {
        foreach (var id in markdown.ImageIds(content))
        {
            needed.TryAdd(id, book);
        }
    }

    /// <summary>中央ディレクトリを含む実ファイルにも2GB制限を適用する</summary>
    private static void CheckCompressed(string path)
    {
        if (new FileInfo(path).Length > BackupArchive.CompressedLimit)
        {
            throw new InvalidDataException("ZIPが圧縮後2GBを超えています。");
        }
    }

    /// <summary>計算し直せる未来の索引・配信履歴を除き、操作と到来済みの予定を保持する</summary>
    private async Task ExportTasksAsync(BackupArchive archive, string[] books, string owner, CancellationToken token)
    {
        var series = db.TaskSeries.AsNoTracking().Where(x => books.Contains(x.NotebookId));
        var segments = db.TaskSegments.AsNoTracking().Where(x => series.Any(s => s.Id == x.SeriesId));
        await ExportRowsAsync(archive, "series", series, BackupMapping.Export, token);
        await ExportRowsAsync(archive, "segments", segments, BackupMapping.Export, token);
        await ExportRowsAsync(archive, "tasks", db.TaskRecords.AsNoTracking().Where(x => books.Contains(x.NotebookId)), BackupMapping.Export, token);
        var reminders = await segments.ToDictionaryAsync(x => x.Id, x => x.ReminderJson, token);
        var operated = (await db.TaskRecords.Where(x => x.SeriesId != null && books.Contains(x.NotebookId)).Select(x => x.Id).ToListAsync(token)).ToHashSet();
        await foreach (var slot in db.TaskSlots.AsNoTracking().Where(x => segments.Any(s => s.Id == x.SegmentId)).AsAsyncEnumerable().WithCancellation(token))
        {
            // 計算済み月は毎回更新されないため、Frozen列だけでは作成後に到来した予定を判別できない。
            if (slot.Waiting || (!slot.Frozen && (slot.DueDate ?? slot.ScheduledDate) > schedule.Today && !operated.Contains(slot.Id)
                && !Reminders.HasArrived(reminders[slot.SegmentId], slot.DueDate, clock)))
            {
                continue;
            }

            await archive.AddRecordAsync("slots", BackupMapping.Export(slot), token);
        }

        await ExportRowsAsync(archive, "pauses", db.TaskPauses.AsNoTracking().Where(x => books.Contains(x.NotebookId)), BackupMapping.Export, token);

        foreach (var anniversary in await db.Anniversaries.AsNoTracking().Include(x => x.Notebooks).Where(x => x.UserId == owner).ToListAsync(token))
        {
            anniversary.Notebooks = anniversary.Notebooks.Where(x => books.Contains(x.NotebookId)).ToList();
            if (anniversary.AllNotebooks || anniversary.Notebooks.Count > 0)
            {
                await archive.AddRecordAsync("anniversaries", BackupMapping.Export(anniversary), token);
            }
        }
    }

    /// <summary>各テーブルのデータを保存用の型へ変換し、一件ずつ書き出す</summary>
    private static async Task ExportRowsAsync<T, TBackup>(BackupArchive archive, string group, IQueryable<T> query, Func<T, TBackup> convert, CancellationToken token)
    {
        await foreach (var row in query.AsAsyncEnumerable().WithCancellation(token))
        {
            await archive.AddRecordAsync(group, convert(row), token);
        }
    }

    /// <summary>履歴を含むZIPの参照と画像実体を、反映前に検証する</summary>
    public async Task<BackupInspection> InspectAsync(string id, bool validateImages, CancellationToken token)
    {
        using var archive = new BackupArchive(PathFor(id));
        await archive.VerifyAsync(token);
        var inspection = BackupInspection.Read(archive, markdown);
        if (validateImages)
        {
            foreach (var image in inspection.Images.Values)
            {
                var input = Path.Combine(DirectoryPath, id + ".input");
                var output = Path.Combine(DirectoryPath, id + ".output");
                try
                {
                    await archive.ExtractAsync(image.Id, input, token);
                    var actual = await worker.ConvertAsync(input, output, true, token, true);
                    if (actual.MediaType != image.MediaType || actual.Width != image.Width || actual.Height != image.Height
                        || new FileInfo(input).Length != image.Length)
                    {
                        throw new InvalidDataException("画像の情報と実体が一致しません。");
                    }
                }
                finally
                {
                    File.Delete(input);
                    File.Delete(output);
                }
            }
        }

        return inspection;
    }

    /// <summary>空の日記帳は残せるが、ゴミ箱や下書き画像も含め記録がある場合は復元しない</summary>
    public async Task<string?> TargetProblemAsync(string owner, BackupInspection inspection)
    {
        var books = db.Notebooks.IgnoreQueryFilters().Where(x => x.UserId == owner);
        if (await books.AnyAsync(x => x.DeletedAt != null || x.Template != "")
            || await db.DiaryEntries.AnyAsync(x => books.Any(b => b.Id == x.NotebookId))
            || await db.DiaryImages.AnyAsync(x => x.UserId == owner)
            || await db.TaskSeries.AnyAsync(x => books.Any(b => b.Id == x.NotebookId))
            || await db.TaskRecords.AnyAsync(x => books.Any(b => b.Id == x.NotebookId))
            || await db.TaskPauses.AnyAsync(x => books.Any(b => b.Id == x.NotebookId))
            || await db.Anniversaries.AnyAsync(x => x.UserId == owner))
        {
            return "復元先に記録があります。ゴミ箱・画像・タスク・記念日・テンプレートも空のアカウントで復元してください。既存の記録は削除しません。";
        }

        var names = inspection.Books.Values.Select(x => x.Name).ToArray();
        if (await books.AnyAsync(x => names.Contains(x.Name)))
        {
            return "復元元と同名の日記帳があります。復元先の空の日記帳を別の名前に変更してから、再度確認してください。";
        }

        return null;
    }
}
