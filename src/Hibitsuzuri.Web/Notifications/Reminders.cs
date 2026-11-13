using System.Globalization;
using System.Text.Json;

namespace Hibitsuzuri.Web.Notifications;

/// <summary>期限からの相対指定、またはサーバーの暦による日時指定</summary>
public sealed class Reminder
{
    /// <summary>同じ設定への二重通知を防ぐ識別子。サーバーで発行する</summary>
    public string Id { get; set; } = "";

    /// <summary>relativeまたはabsolute</summary>
    public string Kind { get; set; } = "relative";

    /// <summary>休日調整後の期限からさかのぼる日数</summary>
    public int DaysBefore
    {
        get; set;
    }

    /// <summary>相対指定の24時間表記</summary>
    public string Time { get; set; } = "09:00";

    /// <summary>日時指定のyyyy-MM-ddTHH:mm。端末側の時差は付けない</summary>
    public string At { get; set; } = "";
}

/// <summary>リマインダーの正規化・時刻解釈・過去時刻の検証を共有する</summary>
public static class Reminders
{
    /// <summary>保存済みの内容が壊れている場合は空設定で隠さない</summary>
    public static List<Reminder> Read(string json) => JsonSerializer.Deserialize<List<Reminder>>(json)
        ?? throw new InvalidDataException("通知設定を読み取れません。");

    /// <summary>設定内容が同じものだけIDを引き継ぐ。クライアントのIDは採用しない</summary>
    public static string? Prepare(IReadOnlyList<Reminder>? requested, string original, bool recurring, DateOnly? due,
        DateOnly? originalDue, TimeProvider clock, out string json)
    {
        json = original;
        requested ??= Read(original);

        if (requested.Count > 3)
        {
            return "リマインダーは3件まで設定できます。";
        }

        var old = Read(original);
        var normalized = new List<Reminder>();
        foreach (var input in requested)
        {
            if (input is null || input.Kind is not ("relative" or "absolute") || (recurring && input.Kind != "relative")
                || input.DaysBefore is < 0 or > 365 || (input.Kind == "relative" && (!TryTime(input.Time, out _) || due is null))
                || (input.Kind == "absolute" && !TryDateTime(input.At, out _)))
            {
                return "日時指定、または期限の0～365日前と時刻を指定してください。繰り返しの共通設定は期限を基準にします。";
            }

            var item = new Reminder
            {
                Kind = input.Kind,
                DaysBefore = input.Kind == "relative" ? input.DaysBefore : 0,
                Time = input.Kind == "relative" ? input.Time : "09:00",
                At = input.Kind == "absolute" ? input.At : ""
            };
            var key = Signature(item);
            if (normalized.Any(x => Signature(x) == key))
            {
                return "同じ通知設定が重複しています。";
            }

            var existing = old.FirstOrDefault(x => Signature(x) == key);
            item.Id = existing?.Id ?? Guid.NewGuid().ToString("N");
            var instant = Resolve(item, due, clock.LocalTimeZone);
            if (instant is null)
            {
                return "このタイムゾーンに存在しない時刻、または範囲外の日付です。通知時刻を変更してください。";
            }

            if ((existing is null || (item.Kind == "relative" && due != originalDue)) && instant <= clock.GetUtcNow().ToUnixTimeMilliseconds())
            {
                return "新しく設定する通知時刻は未来にしてください。既存の通知を変えない編集では、過去の設定も保持できます。";
            }

            normalized.Add(item);
        }

        json = JsonSerializer.Serialize(normalized);
        return null;
    }

    /// <summary>DSTの欠ける時刻は推測せず、重複する時刻は標準時側に固定する</summary>
    public static long? Resolve(Reminder reminder, DateOnly? due, TimeZoneInfo zone)
    {
        DateTime local;
        if (reminder.Kind == "absolute")
        {
            if (!TryDateTime(reminder.At, out local))
            {
                return null;
            }
        }
        else
        {
            if (due is null || due.Value.DayNumber < reminder.DaysBefore || !TryTime(reminder.Time, out var time))
            {
                return null;
            }

            local = due.Value.AddDays(-reminder.DaysBefore).ToDateTime(time, DateTimeKind.Unspecified);
        }

        if (zone.IsInvalidTime(local))
        {
            return null;
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone)).ToUnixTimeMilliseconds();
    }

    /// <summary>通知時刻が来た回を祝日訂正で動かさないための判定</summary>
    public static bool HasArrived(string json, DateOnly? due, TimeProvider clock) => Read(json)
        .Any(x => Resolve(x, due, clock.LocalTimeZone) is { } at && at <= clock.GetUtcNow().ToUnixTimeMilliseconds());

    /// <summary>ユーザーが入力した設定だけで比較し、IDや無関係な値を含めない</summary>
    private static string Signature(Reminder item) => item.Kind == "absolute" ? $"absolute:{item.At}" : $"relative:{item.DaysBefore}:{item.Time}";

    /// <summary>時刻だけの固定形式を受け付ける</summary>
    private static bool TryTime(string value, out TimeOnly time) => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>タイムゾーン付きの入力をサーバーの暦日と混同しない</summary>
    private static bool TryDateTime(string value, out DateTime local) => DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out local);
}
