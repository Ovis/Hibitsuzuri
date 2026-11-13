using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Calendar;

/// <summary>月表示とミニカレンダーに共通する表示材料</summary>
public sealed class CalendarMonth
{
    /// <summary>表示対象の日記帳</summary>
    public required Notebook Book
    {
        get; init;
    }

    /// <summary>切り替え可能な本人の日記帳</summary>
    public IReadOnlyList<Notebook> Books { get; init; } = [];

    /// <summary>表示月の先頭日</summary>
    public DateOnly First
    {
        get; init;
    }

    /// <summary>選択中の日</summary>
    public DateOnly Selected
    {
        get; init;
    }

    /// <summary>サーバーの暦日</summary>
    public DateOnly Today
    {
        get; init;
    }

    /// <summary>祝日情報を収録済みの年か</summary>
    public bool HolidaysKnown
    {
        get; init;
    }

    /// <summary>祝日名を日付で参照する</summary>
    public IReadOnlyDictionary<DateOnly, string> Holidays { get; init; } = new Dictionary<DateOnly, string>();

    /// <summary>ゴミ箱を除く日記のある日</summary>
    public IReadOnlySet<DateOnly> Entries { get; init; } = new HashSet<DateOnly>();

    /// <summary>期限のある未完了タスクの日。件数や件名は月表示へ載せない</summary>
    public IReadOnlySet<DateOnly> PendingTasks { get; init; } = new HashSet<DateOnly>();

    /// <summary>表示先と閏日の規則を適用した記念日</summary>
    public ILookup<DateOnly, string> Anniversaries { get; init; } = Array.Empty<(DateOnly Date, string Title)>().ToLookup(x => x.Date, x => x.Title);
}

/// <summary>本人の月単位の記録だけを集め、両方のカレンダーで共有する</summary>
public sealed class CalendarReader(AppDbContext database, TimeProvider clock, Hibitsuzuri.Web.Tasks.TaskReader tasks)
{
    /// <summary>日記帳の所有者を確認して表示材料を返す</summary>
    public async Task<CalendarMonth?> ReadAsync(string owner, string? bookId, DateOnly selected)
    {
        var books = await database.Notebooks.Where(x => x.UserId == owner).OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync();
        var book = bookId is null ? books.FirstOrDefault() : books.FirstOrDefault(x => x.Id == bookId);
        if (book is null)
        {
            return null;
        }

        var first = new DateOnly(selected.Year, selected.Month, 1);
        var last = new DateOnly(selected.Year, selected.Month, DateTime.DaysInMonth(selected.Year, selected.Month));
        await tasks.PrepareAsync(owner, book.Id, first, last);
        var pendingTasks = await tasks.Rows(owner).Where(x => x.NotebookId == book.Id && x.CompletedAt == null && x.DueDate >= first && x.DueDate <= last)
            .Select(x => x.DueDate.GetValueOrDefault()).Distinct().ToListAsync();

        var state = await database.HolidayStates.SingleAsync();
        var holidays = await database.Holidays.Where(x => x.Date >= first && x.Date <= last).ToDictionaryAsync(x => x.Date, x => x.Name);
        var entries = await database.DiaryEntries.Where(x => x.NotebookId == book.Id && x.DeletedAt == null && x.EntryDate >= first && x.EntryDate <= last)
            .Select(x => x.EntryDate).ToListAsync();
        var anniversaries = await database.Anniversaries.Where(x => x.UserId == owner && (x.AllNotebooks || x.Notebooks.Any(n => n.NotebookId == book.Id)))
            .OrderBy(x => x.Title).ToListAsync();

        return new CalendarMonth
        {
            Book = book,
            Books = books,
            First = first,
            Selected = selected,
            Today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime),
            HolidaysKnown = selected.Year >= state.FirstYear && selected.Year <= state.LastYear,
            Holidays = holidays,
            Entries = entries.ToHashSet(),
            PendingTasks = pendingTasks.ToHashSet(),
            Anniversaries = anniversaries.Select(x => (Date: x.InYear(selected.Year), x.Title))
                .Where(x => x.Date >= first && x.Date <= last).ToLookup(x => x.Date.GetValueOrDefault(), x => x.Title)
        };
    }
}

/// <summary>記念日一覧と編集時の選択肢</summary>
public sealed class AnniversaryPage
{
    /// <summary>本人の記念日一覧</summary>
    public IReadOnlyList<Anniversary> Items { get; init; } = [];

    /// <summary>選択できる日記帳</summary>
    public IReadOnlyList<Notebook> Books { get; init; } = [];

    /// <summary>新規作成または変更対象</summary>
    public Anniversary Editing { get; init; } = new();

    /// <summary>新規作成時は版照合を要求しない</summary>
    public bool IsNew
    {
        get; init;
    }
}
