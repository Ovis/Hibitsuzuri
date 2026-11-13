using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Notifications;
using Hibitsuzuri.Web.Storage;
using Hibitsuzuri.Web.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>所有者と版を検証し、タスクの閲覧・各回・系列の変更を扱う</summary>
[ServiceFilter(typeof(AuthOperationFilter))]
public sealed class TasksController(AppDbContext db, TaskReader reader, TaskSchedule schedule, TimeProvider clock) : Controller
{
    /// <summary>管理者も本人の日記帳だけに限定する</summary>
    private string Owner => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException();

    /// <summary>最後に読んだ日記帳を初期選択する</summary>
    [HttpGet("/tasks")]
    public async Task<IActionResult> Index(string? bookId)
    {
        var books = await db.Notebooks.Where(x => x.UserId == Owner).OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync();
        var selected = Request.Query.ContainsKey("bookId") ? bookId : Request.Cookies[$"Hibitsuzuri.Book.{Owner}"];
        if (!string.IsNullOrEmpty(selected) && books.All(x => x.Id != selected))
        {
            return NotFound();
        }

        return View(new TaskPage(books, selected, schedule.Today));
    }

    /// <summary>件数はDBで集計し、系列内の各回は展開されたときだけ50件ずつ返す</summary>
    [HttpGet("/api/tasks")]
    public async Task<IActionResult> List([FromQuery] TaskFilter filter)
    {
        filter.BookId = string.IsNullOrEmpty(filter.BookId) ? null : filter.BookId;
        if (!await OwnBookAsync(filter.BookId))
        {
            return NotFound();
        }

        if (!ModelState.IsValid || filter.Page < 1 || filter.Page > 100000
            || filter.Mode is not ("pending" or "completed") || filter.Kind is not ("all" or "dated" or "undated")
            || filter.Quick is not ("all" or "today" or "overdue") || filter.Period is not ("30" or "month" or "previous" or "custom" or "all")
            || filter.Query.Length > 200 || (filter.Mode == "completed" && filter.Period == "custom" && (filter.Start is null || filter.End is null || filter.Start > filter.End))
            || (filter.Mode == "pending" && filter.Through < schedule.Today))
        {
            return BadRequest(new
            {
                message = "検索条件と期間を確認してください。"
            });
        }

        if (filter.Mode != "completed" || filter.Day is not null)
        {
            var from = filter.Day is { } selected ? new DateOnly(selected.Year, selected.Month, 1) : (DateOnly?)null;
            var through = filter.Day is { } selectedDay
                ? new DateOnly(selectedDay.Year, selectedDay.Month, DateTime.DaysInMonth(selectedDay.Year, selectedDay.Month))
                : filter.Through ?? TaskSchedule.Offset(schedule.Today, 30);
            await reader.PrepareAsync(Owner, filter.BookId, from, through);
        }
        var all = reader.Filter(Owner, filter);
        var counts = new
        {
            all = await all.CountAsync(),
            dated = await all.CountAsync(x => x.DueDate != null),
            undated = await all.CountAsync(x => x.DueDate == null)
        };
        var filtered = reader.Deadlines(all, filter);
        var matched = await filtered.CountAsync();
        var today = schedule.Today;
        var rows = filter.Day is not null || (filter.Mode == "completed" && filter.SeriesId is null) ? filtered : filter.SeriesId is null
            ? filtered.Where(x => x.SeriesId == null) : filtered.Where(x => x.SeriesId == filter.SeriesId);
        var total = await rows.CountAsync();
        var items = await TaskReader.Order(rows, filter.Mode == "completed").Skip((filter.Page - 1) * 50).Take(50).ToListAsync();
        var pageSeries = items.Select(x => x.SeriesId).Where(x => x != null).ToArray();
        var groups = filter.SeriesId is null && filter.Day is null
            ? await filtered.Where(x => x.SeriesId != null && (filter.Mode != "completed" || pageSeries.Contains(x.SeriesId))).GroupBy(x => x.SeriesId).Select(g => new
            {
                id = g.Key,
                count = g.Count(),
                overdue = g.Count(x => x.CompletedAt == null && x.DueDate < today),
                next = g.Min(x => x.DueDate >= today ? x.DueDate : null),
                first = g.Min(x => x.DueDate),
                title = g.OrderByDescending(x => x.ScheduledDate).Select(x => x.Title).First(),
                bookName = g.Select(x => x.BookName).First()
            }).ToListAsync() : [];
        var waiting = await (from slot in db.TaskSlots
                             join segment in db.TaskSegments on slot.SegmentId equals segment.Id
                             join series in db.TaskSeries on segment.SeriesId equals series.Id
                             join book in db.Notebooks on series.NotebookId equals book.Id
                             where book.UserId == Owner && (filter.BookId == null || book.Id == filter.BookId) && slot.Waiting
                                 && (segment.Cutoff == null || slot.ScheduledDate < segment.Cutoff)
                             select new
                             {
                                 id = slot.Id,
                                 title = segment.Title,
                                 segmentId = segment.Id
                             }).GroupBy(x => new { x.segmentId, x.title }).Select(g => new { id = g.Min(x => x.id), title = g.Key.title }).ToListAsync();

        var pendingDays = filter.Day is { } date
            ? await reader.Rows(Owner).Where(x => x.NotebookId == filter.BookId && x.CompletedAt == null && x.DueDate != null
                && x.DueDate.Value.Year == date.Year && x.DueDate.Value.Month == date.Month).Select(x => x.DueDate).Distinct().ToListAsync()
            : [];

        return Json(new
        {
            counts,
            matched,
            groups,
            items,
            total,
            page = filter.Page,
            waiting,
            pendingDays,
            today
        });
    }

    /// <summary>各回の最新版と、今回以降を変えても保持する操作済み件数を返す</summary>
    [HttpGet("/api/tasks/item")]
    public async Task<IActionResult> Item(string id)
    {
        if (!ModelState.IsValid || string.IsNullOrEmpty(id) || id.Length > 80)
        {
            return BadRequest(new
            {
                message = "タスクを選び直してください。"
            });
        }

        var row = await FindAsync(id, false);
        if (row is null)
        {
            return NotFound();
        }

        var segment = row.SegmentId is null ? null : await db.TaskSegments.FindAsync(row.SegmentId);
        var series = row.SeriesId is null ? null : await db.TaskSeries.FindAsync(row.SeriesId);
        var retained = await db.TaskRecords.CountAsync(x => x.SeriesId == row.SeriesId && row.SeriesId != null
            && x.ScheduledDate >= row.ScheduledDate && (x.CompletedAt != null || x.Edited) && !x.Skipped);

        return Json(new
        {
            item = row,
            rule = segment is null ? null : RepeatRule.Read(segment.RuleJson),
            startDate = segment?.StartDate,
            seriesVersion = series?.Version,
            retained,
            reminders = Reminders.Read(row.ReminderJson),
            commonReminders = segment is null ? null : Reminders.Read(segment.ReminderJson),
            notificationReview = row.NotificationAfter > 0 && Reminders.Read(row.ReminderJson)
                .Any(x => Reminders.Resolve(x, row.DueDate, clock.LocalTimeZone) is { } at && at <= row.NotificationAfter),
            timeZone = clock.LocalTimeZone.Id
        });
    }

    /// <summary>再送を先に検証し、変更と結果を同じトランザクションで記録する</summary>
    [HttpPost("/api/tasks/change")]
    public async Task<IActionResult> Change([FromBody] TaskChange change)
    {
        if (!ModelState.IsValid || !Guid.TryParseExact(change.OperationId, "D", out _) || change.Action is not ("save" or "complete" or "delete")
            || change.Scope is not ("once" or "following"))
        {
            return BadRequest(new
            {
                message = "操作内容を確認してください。"
            });
        }

        var hash = AuthRules.Hash(JsonSerializer.Serialize(change));
        var prior = await db.TaskMutations.FindAsync(Owner, change.OperationId);
        if (prior is not null)
        {
            return prior.Hash == hash ? Content(prior.ResultJson, "application/json") : Conflict(new
            {
                message = "同じ操作IDで異なる内容を保存できません。"
            });
        }

        if (change.ContextDay < schedule.Today)
        {
            return Conflict(new
            {
                message = "過去の日記からタスクは変更できません。タスク一覧を開いてください。"
            });
        }

        var row = change.Id is null ? null : await FindAsync(change.Id, true);
        if (change.Id is not null && row is null)
        {
            return NotFound();
        }

        var bookId = row?.NotebookId ?? change.NotebookId;
        var book = await db.Notebooks.SingleOrDefaultAsync(x => x.Id == bookId && x.UserId == Owner);
        if (book is null || (change.ContextBook is not null && change.ContextBook != book.Id))
        {
            return NotFound();
        }

        if (book.TasksPausedOn is not null || (row is not null && row.Version != change.Version))
        {
            return Conflict(new
            {
                message = "タスクが変更されたか、日記帳のタスクが停止中です。再読み込みして確認してください。"
            });
        }

        change.Title = change.Title.Trim();
        change.Memo = DiaryMarkdown.NormalizeLineEndings(change.Memo);
        if (change.Action == "save" && (!AuthRules.ValidText(change.Title, 1, 200) || !AuthRules.ValidText(change.Memo, 0, 10000)
            || (change.Rule is not null && (change.StartDate is null || !change.Rule.Valid(change.StartDate.Value)))))
        {
            return BadRequest(new
            {
                message = "件名は1～200文字、メモは10,000文字以内で、繰り返し条件と開始日を確認してください。"
            });
        }

        if (row is null && change.Action != "save")
        {
            return BadRequest();
        }

        if (row?.Waiting == true && (change.Scope != "following" || change.Action == "complete"))
        {
            return BadRequest(new
            {
                message = "祝日データ待ちの予定は「今回以降」で設定を変更してください。"
            });
        }

        var series = row?.SeriesId is null ? null : await db.TaskSeries.SingleAsync(x => x.Id == row.SeriesId);
        if (series is not null && change.Scope == "following" && change.SeriesVersion != series.Version)
        {
            return Conflict(new
            {
                message = "系列の操作済み件数が変わりました。もう一度内容を確認してください。"
            });
        }

        if (series is not null && change.Scope == "once" && change.Rule is not null)
        {
            return BadRequest(new
            {
                message = "繰り返し条件は「今回以降」で変更してください。"
            });
        }

        if (series is not null && change.Scope == "following" && change.Action == "save" && change.Rule is not null && change.StartDate < row?.ScheduledDate)
        {
            return BadRequest(new
            {
                message = "新しい開始日は今回の元予定日以降にしてください。"
            });
        }

        if (change.Action == "save")
        {
            var original = row?.ReminderJson ?? "[]";
            var due = change.Rule is null ? change.DueDate : change.StartDate;
            var requested = change.Reminders ?? Reminders.Read(original);
            if (change.Rule is not null && requested.Count != 0)
            {
                var state = await db.HolidayStates.SingleAsync();
                var dates = await db.Holidays.Select(x => x.Date).ToListAsync();
                var holidays = new HolidayCalendar(state.FirstYear, state.LastYear, dates.ToHashSet());
                var start = change.StartDate.GetValueOrDefault();
                var first = RepeatCalculator.Generate(change.Rule, start, start, DateOnly.MaxValue, holidays)
                    .FirstOrDefault(x => x.Due != null || x.Waiting);
                due = first?.Due ?? first?.Scheduled;
                // 新しい区間へ過去の通知設定を複製して再配信しない。
                original = "[]";
            }

            var error = Reminders.Prepare(requested, original, change.Rule is not null, due, row?.DueDate, clock, out var reminderJson);
            if (error is not null)
            {
                return BadRequest(new
                {
                    message = error
                });
            }

            change.PreparedReminders = reminderJson;
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        var record = row is null ? null : await db.TaskRecords.SingleOrDefaultAsync(x => x.Id == row.Id);
        if (row is not null && record is null)
        {
            record = Snapshot(row);
            db.TaskRecords.Add(record);
        }

        if (series is not null)
        {
            series.Version = Guid.NewGuid().ToString("N");
        }

        if (series is not null && change.Scope == "following" && change.Action != "complete")
        {
            var boundary = row?.ScheduledDate ?? throw new InvalidOperationException();
            foreach (var segment in await db.TaskSegments.Where(x => x.SeriesId == series.Id && (x.Cutoff == null || x.Cutoff > boundary)).ToListAsync())
            {
                segment.Cutoff = boundary;
            }

            var untouched = await db.TaskRecords.Where(x => x.SeriesId == series.Id && x.ScheduledDate >= boundary
                && x.CompletedAt == null && !x.Edited && !x.Skipped).ToListAsync();
            db.TaskRecords.RemoveRange(untouched);
            // 今回を読み込むために作った未保存のスナップショットも、旧ルールの通常回として消す。
            if (record is not null && record.CompletedAt is null && !record.Edited && !record.Skipped)
            {
                db.TaskRecords.Remove(record);
            }

            if (change.Action == "save" && change.Rule is null)
            {
                // 通常タスクへ変換する今回だけは完了状態を引き継ぎ、旧系列での二重表示を避ける。
                if (record is not null && db.Entry(record).State != EntityState.Detached && db.Entry(record).State != EntityState.Deleted)
                {
                    record.Skipped = true;
                }

                record = NewRecord(book.Id, change, row?.CompletedAt);
                db.TaskRecords.Add(record);
            }
            else if (change.Action == "save")
            {
                AddSegment(series, change);
                record = null;
            }
        }
        else if (change.Action == "delete" && record is not null)
        {
            if (series is null)
            {
                db.TaskRecords.Remove(record);
            }
            else
            {
                record.Skipped = true;
            }
        }
        else if (change.Action == "complete" && record is not null)
        {
            // 二重クリックや再送で完了日時を更新しない。完了し直したときだけ新しい時刻を記録する。
            record.CompletedAt = change.Completed ? record.CompletedAt ?? clock.GetUtcNow().ToUnixTimeMilliseconds() : null;
            if (!change.Completed)
            {
                record.NotificationAfter = clock.GetUtcNow().ToUnixTimeMilliseconds();
            }
        }
        else if (change.Rule is not null)
        {
            series = new TaskSeries { NotebookId = book.Id };
            db.TaskSeries.Add(series);
            var segment = AddSegment(series, change);
            var original = record;
            record = null;
            if (original?.CompletedAt is not null)
            {
                var state = await db.HolidayStates.SingleAsync();
                var dates = await db.Holidays.Select(x => x.Date).ToListAsync();
                var holidays = new HolidayCalendar(state.FirstYear, state.LastYear, dates.ToHashSet());
                var first = RepeatCalculator.Generate(change.Rule, segment.StartDate, segment.StartDate, DateOnly.MaxValue, holidays)
                    .FirstOrDefault(x => x.Due != null || x.Waiting);
                if (first?.Due is { } due)
                {
                    record = NewRecord(book.Id, change, original.CompletedAt);
                    record.Id = TaskSchedule.Key(segment.Id, first.Scheduled);
                    record.SeriesId = series.Id;
                    record.SegmentId = segment.Id;
                    record.ScheduledDate = first.Scheduled;
                    record.DueDate = due;
                    db.TaskRecords.Add(record);
                }
                else
                {
                    // 初回が未確定・全回自動スキップの場合も、既存の完了履歴を消さない。
                    record = original;
                }
            }

            if (original is not null && original != record)
            {
                db.TaskRecords.Remove(original);
            }
        }
        else
        {
            if (record is null)
            {
                record = NewRecord(book.Id, change, null);
                db.TaskRecords.Add(record);
            }
            else
            {
                record.Title = change.Title;
                record.Memo = change.Memo;
                record.DueDate = change.DueDate;
                record.SearchText = DiarySearch.Normalize(change.Title + "\n" + change.Memo);
                record.Edited = true;
                record.ReminderJson = change.PreparedReminders;
            }
        }

        if (record is not null)
        {
            record.Version = Guid.NewGuid().ToString("N");
        }

        var result = JsonSerializer.Serialize(new
        {
            saved = true,
            id = record?.Id,
            version = record?.Version,
            seriesId = series?.Id
        });
        db.TaskMutations.Add(new TaskMutation { UserId = Owner, Id = change.OperationId, Hash = hash, ResultJson = result });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return Content(result, "application/json");
    }

    /// <summary>停止期間中に到来した未操作の回を、再開時にまとめて出さない</summary>
    [HttpPost("/tasks/pause/{bookId}")]
    public async Task<IActionResult> Pause(string bookId, string version, bool paused)
    {
        var book = await db.Notebooks.SingleOrDefaultAsync(x => x.Id == bookId && x.UserId == Owner);
        if (book is null)
        {
            return NotFound();
        }

        if (book.Version != version)
        {
            return Conflict("日記帳の状態が変わりました。再読み込みしてください。");
        }

        if (paused && book.TasksPausedOn is null)
        {
            book.TasksPausedOn = schedule.Today;
        }
        else if (!paused && book.TasksPausedOn is { } start)
        {
            if (start < schedule.Today)
            {
                db.TaskPauses.Add(new TaskPause { NotebookId = book.Id, From = start, Until = schedule.Today });
            }

            book.TasksPausedOn = null;
            book.NotificationAfter = clock.GetUtcNow().ToUnixTimeMilliseconds();
        }

        book.Version = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync();
        return Redirect($"/books/{book.Id}");
    }

    /// <summary>元予定日の索引が未作成でも、所有範囲を確認してからその月だけ計算する</summary>
    private async Task<TaskRow?> FindAsync(string id, bool locked)
    {
        var pieces = id.Split(':');
        if (pieces.Length == 2 && DateOnly.TryParseExact(pieces[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            var book = await (from segment in db.TaskSegments
                              join series in db.TaskSeries on segment.SeriesId equals series.Id
                              join notebook in db.Notebooks on series.NotebookId equals notebook.Id
                              where segment.Id == pieces[0] && notebook.UserId == Owner
                              select notebook.Id).SingleOrDefaultAsync();
            if (book is not null)
            {
                await reader.PrepareAsync(Owner, book, date, date, locked);
            }
        }

        var row = await reader.Rows(Owner).SingleOrDefaultAsync(x => x.Id == id);
        if (row is not null)
        {
            return row;
        }

        return await (from slot in db.TaskSlots
                      join segment in db.TaskSegments on slot.SegmentId equals segment.Id
                      join series in db.TaskSeries on segment.SeriesId equals series.Id
                      join book in db.Notebooks on series.NotebookId equals book.Id
                      where slot.Id == id && slot.Waiting && book.UserId == Owner && (segment.Cutoff == null || slot.ScheduledDate < segment.Cutoff)
                      select new TaskRow
                      {
                          Id = slot.Id,
                          NotebookId = book.Id,
                          BookName = book.Name,
                          Title = segment.Title,
                          Memo = segment.Memo,
                          SearchText = segment.SearchText,
                          SeriesId = series.Id,
                          SegmentId = segment.Id,
                          ScheduledDate = slot.ScheduledDate,
                          Version = slot.Version,
                          Waiting = true,
                          ReadOnly = book.TasksPausedOn != null,
                          ReminderJson = segment.ReminderJson,
                          NotificationAfter = slot.NotificationAfter > book.NotificationAfter ? slot.NotificationAfter : book.NotificationAfter
                      }).SingleOrDefaultAsync();
    }

    /// <summary>全日記帳を許可し、具体的なIDは本人のものに限定する</summary>
    private Task<bool> OwnBookAsync(string? id) => id is null ? Task.FromResult(true) : db.Notebooks.AnyAsync(x => x.Id == id && x.UserId == Owner);

    /// <summary>新ルールの区間を追加し、旧区間の操作を移さない</summary>
    private TaskSegment AddSegment(TaskSeries series, TaskChange change)
    {
        var segment = new TaskSegment
        {
            SeriesId = series.Id,
            Title = change.Title,
            Memo = change.Memo,
            SearchText = DiarySearch.Normalize(change.Title + "\n" + change.Memo),
            StartDate = change.StartDate ?? throw new InvalidOperationException(),
            RuleJson = JsonSerializer.Serialize(change.Rule),
            ReminderJson = change.PreparedReminders
        };
        db.TaskSegments.Add(segment);

        return segment;
    }

    /// <summary>各回の初回操作では表示中の内容を固定する</summary>
    private static TaskRecord Snapshot(TaskRow row) => new()
    {
        Id = row.Id,
        NotebookId = row.NotebookId,
        SeriesId = row.SeriesId,
        SegmentId = row.SegmentId,
        ScheduledDate = row.ScheduledDate,
        Title = row.Title,
        Memo = row.Memo,
        SearchText = row.SearchText,
        DueDate = row.DueDate,
        CompletedAt = row.CompletedAt,
        Version = row.Version,
        ReminderJson = row.ReminderJson,
        NotificationAfter = row.NotificationAfter
    };

    /// <summary>通常タスクは系列から独立した識別子を持つ</summary>
    private TaskRecord NewRecord(string bookId, TaskChange change, long? completedAt) => new()
    {
        NotebookId = bookId,
        Title = change.Title,
        Memo = change.Memo,
        DueDate = change.DueDate,
        SearchText = DiarySearch.Normalize(change.Title + "\n" + change.Memo),
        CompletedAt = completedAt,
        ReminderJson = change.PreparedReminders,
        NotificationAfter = clock.GetUtcNow().ToUnixTimeMilliseconds()
    };
}
