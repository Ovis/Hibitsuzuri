namespace Hibitsuzuri.Web.Calendar;

/// <summary>毎年表示する個人の記念日と表示先</summary>
public sealed class Anniversary
{
    /// <summary>変更しても維持する識別子</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>記念日を管理する本人</summary>
    public string UserId { get; set; } = "";

    /// <summary>カレンダーに表示する名前</summary>
    public string Title { get; set; } = "";

    /// <summary>元の月。振替後の月で上書きしない</summary>
    public int Month
    {
        get; set;
    }

    /// <summary>元の日。2月29日もそのまま保持する</summary>
    public int Day
    {
        get; set;
    }

    /// <summary>非うるう年の2月29日を skip・feb28・mar1 のいずれかで扱う</summary>
    public string LeapDayPolicy { get; set; } = "skip";

    /// <summary>今後作成するものも含め、本人の全日記帳に表示する</summary>
    public bool AllNotebooks { get; set; } = true;

    /// <summary>個別に選択した表示先</summary>
    public List<AnniversaryNotebook> Notebooks { get; set; } = [];

    /// <summary>別画面からの変更を検出する版</summary>
    public string Version { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>指定年の表示日を求め、表示しない年はnullを返す</summary>
    public DateOnly? InYear(int year)
    {
        if (Month != 2 || Day != 29 || DateTime.IsLeapYear(year))
        {
            return new DateOnly(year, Month, Day);
        }

        return LeapDayPolicy switch
        {
            "feb28" => new DateOnly(year, 2, 28),
            "mar1" => new DateOnly(year, 3, 1),
            _ => null
        };
    }
}

/// <summary>記念日を複数の日記帳へ関連付ける</summary>
public sealed class AnniversaryNotebook
{
    /// <summary>表示する記念日</summary>
    public string AnniversaryId { get; set; } = "";

    /// <summary>本人が選択した日記帳</summary>
    public string NotebookId { get; set; } = "";
}
