namespace Hibitsuzuri.Web.Calendar;

/// <summary>公式データに明記された祝日・休日</summary>
public sealed class Holiday
{
    /// <summary>祝日・休日の暦日</summary>
    public DateOnly Date
    {
        get; set;
    }

    /// <summary>振替休日等も含む公式の名称</summary>
    public string Name { get; set; } = "";
}

/// <summary>祝日なしと未取得を区別する収録範囲と更新結果</summary>
public sealed class HolidayState
{
    /// <summary>アプリ全体で共有する唯一の更新状態</summary>
    public int Id { get; set; } = 1;

    /// <summary>全月を収録する最初の年</summary>
    public int FirstYear
    {
        get; set;
    }

    /// <summary>全月を収録する最後の年</summary>
    public int LastYear
    {
        get; set;
    }

    /// <summary>更新を試みたUTCミリ秒。再起動でも日次間隔を維持する</summary>
    public long? LastAttemptAt
    {
        get; set;
    }

    /// <summary>外部取得と検証が最後に成功したUTCミリ秒。同梱データだけなら未設定</summary>
    public long? LastSuccessAt
    {
        get; set;
    }

    /// <summary>管理者に表示する直近の失敗理由</summary>
    public string? Error
    {
        get; set;
    }

    /// <summary>採用した公式CSVの取得先</summary>
    public string SourceUrl { get; set; } = "";
}
