using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace Hibitsuzuri.Web.Calendar;

/// <summary>e-Govのメタデータから公式CSVを解決し、採用前に全体を検証する</summary>
public sealed class HolidaySource(HttpClient client)
{
    public const string MetadataUrl = "https://data.e-gov.go.jp/data/api/action/package_show?id=cao_20190522_0002";

    /// <summary>取得先と内容を一組として返す。取得失敗を空の祝日に置き換えない</summary>
    public async Task<(string Url, IReadOnlyList<Holiday> Holidays)> FetchAsync(CancellationToken cancellationToken)
    {
        using var metadata = JsonDocument.Parse(await ReadAsync(new Uri(MetadataUrl), cancellationToken));
        if (!metadata.RootElement.GetProperty("success").GetBoolean())
        {
            throw new InvalidDataException("e-Govのメタデータ取得に失敗しました。");
        }

        var urls = metadata.RootElement.GetProperty("result").GetProperty("resources").EnumerateArray()
            .Where(x => string.Equals(x.GetProperty("format").GetString(), "CSV", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.GetProperty("url").GetString()).Distinct().ToArray();
        if (urls.Length != 1 || !Uri.TryCreate(urls[0], UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0
            || uri.Host is not ("www8.cao.go.jp" or "www.cao.go.jp"))
        {
            throw new InvalidDataException("公式の祝日CSVを一意に特定できませんでした。");
        }

        var bytes = await ReadAsync(uri, cancellationToken);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

        return (uri.AbsoluteUri, Parse(encoding.GetString(bytes)));
    }

    /// <summary>Shift-JISを復号したCSVを検証する。途中まで読めても一部だけ採用しない</summary>
    public static IReadOnlyList<Holiday> Parse(string csv)
    {
        using var parser = new TextFieldParser(new StringReader(csv));
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        var header = parser.ReadFields();
        if (header is not ["国民の祝日・休日月日", "国民の祝日・休日名称"])
        {
            throw new InvalidDataException("祝日CSVの見出しを確認できませんでした。");
        }

        var holidays = new List<Holiday>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields is not [var date, var name]
                || !DateOnly.TryParseExact(date, "yyyy/M/d", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                || string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl))
            {
                throw new InvalidDataException("祝日CSVに不正な日付または名称があります。");
            }

            holidays.Add(new Holiday { Date = day, Name = name });
        }

        Validate(holidays);
        return holidays;
    }

    /// <summary>重複・年の欠落・途中で切れた収録年を拒否する</summary>
    public static void Validate(IReadOnlyList<Holiday> holidays)
    {
        if (holidays.Count == 0 || holidays.Select(x => x.Date).Distinct().Count() != holidays.Count)
        {
            throw new InvalidDataException("祝日データが空、または日付が重複しています。");
        }

        var years = holidays.GroupBy(x => x.Date.Year).OrderBy(x => x.Key).ToArray();
        // 1955年等の公式履歴は年9日。現在の祝日数を過去の年へ強制しない。
        if (years.Length != years[^1].Key - years[0].Key + 1
            || years.Any(year => year.Count() is < 9 or > 30
                || !year.Any(x => x.Date.Month == 1 && x.Date.Day == 1)
                || !year.Any(x => x.Date.Month == 11 && x.Date.Day == 23)))
        {
            throw new InvalidDataException("祝日データの収録年または年間の件数が不完全です。");
        }
    }

    /// <summary>応答本文の増大を制限する。リダイレクト先を無条件に信用しない</summary>
    private async Task<byte[]> ReadAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + read > 1024 * 1024)
            {
                throw new InvalidDataException("祝日データの応答サイズが上限を超えました。");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}
