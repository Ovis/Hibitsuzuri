using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Tasks;

/// <summary>所有者境界を含む予定・操作記録の統合検索</summary>
public sealed class TaskReader(AppDbContext db, TaskSchedule schedule, TimeProvider clock)
{
    /// <summary>通常タスクと個別操作済みの回を、未操作の予定と重複させずに読む</summary>
    public IQueryable<TaskRow> Rows(string owner)
    {
        var records = from task in db.TaskRecords.AsNoTracking()
                      join book in db.Notebooks on task.NotebookId equals book.Id
                      where book.UserId == owner && !task.Skipped
                      select new TaskRow
                      {
                          Id = task.Id,
                          NotebookId = book.Id,
                          BookName = book.Name,
                          Title = task.Title,
                          Memo = task.Memo,
                          SearchText = task.SearchText,
                          DueDate = task.DueDate,
                          CompletedAt = task.CompletedAt,
                          SeriesId = task.SeriesId,
                          SegmentId = task.SegmentId,
                          ScheduledDate = task.ScheduledDate,
                          Version = task.Version,
                          ReadOnly = book.TasksPausedOn != null,
                          ReminderJson = task.ReminderJson,
                          NotificationAfter = task.NotificationAfter > book.NotificationAfter ? task.NotificationAfter : book.NotificationAfter
                      };
        var today = schedule.Today;
        var slots = from slot in db.TaskSlots.AsNoTracking()
                    join segment in db.TaskSegments on slot.SegmentId equals segment.Id
                    join series in db.TaskSeries on segment.SeriesId equals series.Id
                    join book in db.Notebooks on series.NotebookId equals book.Id
                    where book.UserId == owner && slot.DueDate != null
                        && (segment.Cutoff == null || slot.ScheduledDate < segment.Cutoff)
                        && !db.TaskRecords.Any(x => x.Id == slot.Id)
                        && !db.TaskPauses.Any(x => x.NotebookId == book.Id && slot.DueDate >= x.From && slot.DueDate < x.Until)
                        && (book.TasksPausedOn == null || slot.DueDate < book.TasksPausedOn || slot.DueDate > today)
                    select new TaskRow
                    {
                        Id = slot.Id,
                        NotebookId = book.Id,
                        BookName = book.Name,
                        Title = segment.Title,
                        Memo = segment.Memo,
                        SearchText = segment.SearchText,
                        DueDate = slot.DueDate,
                        CompletedAt = null,
                        SeriesId = series.Id,
                        SegmentId = segment.Id,
                        ScheduledDate = slot.ScheduledDate,
                        Version = slot.Version,
                        ReadOnly = book.TasksPausedOn != null,
                        ReminderJson = segment.ReminderJson,
                        NotificationAfter = slot.NotificationAfter > book.NotificationAfter ? slot.NotificationAfter : book.NotificationAfter
                    };

        return records.Concat(slots);
    }

    /// <summary>計算が必要な範囲だけを確保する。所有していない日記帳には触れない</summary>
    public async Task PrepareAsync(string owner, string? bookId, DateOnly? from, DateOnly through, bool locked = false)
    {
        var books = await db.Notebooks.Where(x => x.UserId == owner && (bookId == null || x.Id == bookId)).Select(x => x.Id).ToArrayAsync();
        await schedule.EnsureAsync(books, from, through, locked);
    }

    /// <summary>履歴を完了日時で絞り、日記は期限なしも併せて読む</summary>
    public IQueryable<TaskRow> Filter(string owner, TaskFilter filter)
    {
        var query = Rows(owner).Where(x => filter.BookId == null || x.NotebookId == filter.BookId);
        var search = DiarySearch.Normalize(filter.Query.Trim());
        if (search.Length > 0)
        {
            query = query.Where(x => x.SearchText.Contains(search));
        }

        if (filter.Day is { } day)
        {
            return query.Where(x => x.DueDate == day || x.DueDate == null);
        }

        if (filter.Mode == "completed")
        {
            query = query.Where(x => x.CompletedAt != null);
            var today = schedule.Today;
            var month = new DateOnly(today.Year, today.Month, 1);
            var start = filter.Period switch
            {
                "month" => month,
                "previous" => month.AddMonths(-1),
                "custom" => filter.Start,
                "all" => null,
                _ => TaskSchedule.Offset(today, -29)
            };
            var end = filter.Period == "previous" ? month.AddDays(-1) : filter.Period == "custom" ? filter.End : null;
            if (start is { } first)
            {
                var minimum = LocalBoundary(first);
                query = query.Where(x => x.CompletedAt >= minimum);
            }

            if (end is { } last && last < DateOnly.MaxValue)
            {
                var maximum = LocalBoundary(last.AddDays(1));
                query = query.Where(x => x.CompletedAt < maximum);
            }
        }
        else
        {
            var horizon = filter.Through ?? TaskSchedule.Offset(schedule.Today, 30);
            query = query.Where(x => x.CompletedAt == null && (x.SeriesId == null || x.DueDate == null || x.DueDate <= horizon));
        }

        return query;
    }

    /// <summary>タブごとの件数を出した後で追加の期限条件を適用する</summary>
    public IQueryable<TaskRow> Deadlines(IQueryable<TaskRow> query, TaskFilter filter)
    {
        if (filter.Kind == "dated")
        {
            query = query.Where(x => x.DueDate != null);
        }
        else if (filter.Kind == "undated")
        {
            query = query.Where(x => x.DueDate == null);
        }

        var today = schedule.Today;
        return filter.Quick switch
        {
            "overdue" => query.Where(x => x.DueDate < today),
            "today" => query.Where(x => x.DueDate == today),
            _ => query
        };
    }

    /// <summary>ページをまたいで同順位の行が入れ替わらないようIDで確定する</summary>
    public static IOrderedQueryable<TaskRow> Order(IQueryable<TaskRow> query, bool completed) => completed
        ? query.OrderByDescending(x => x.CompletedAt).ThenBy(x => x.Id)
        : query.OrderBy(x => x.DueDate == null).ThenBy(x => x.DueDate).ThenBy(x => x.Id);

    /// <summary>ホストの暦日をUTCミリ秒の半開区間へ変換する</summary>
    private long LocalBoundary(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, clock.LocalTimeZone)).ToUnixTimeMilliseconds();
    }
}
