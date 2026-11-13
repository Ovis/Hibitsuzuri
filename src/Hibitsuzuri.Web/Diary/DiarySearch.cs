using System.Net;
using System.Text;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Diary;

/// <summary>表示本文に対する部分一致と安全な強調表示</summary>
public static class DiarySearch
{
    /// <summary>英数字だけ幅と大小文字を同一視し、仮名の違いは維持する</summary>
    public static string Normalize(string text)
    {
        return string.Create(text.Length, text, (result, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                if (c is >= '０' and <= '９' or >= 'Ａ' and <= 'Ｚ' or >= 'ａ' and <= 'ｚ')
                {
                    c = (char)(c - 0xfee0);
                }

                result[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });
    }

    /// <summary>一致位置の前後だけを抜き出し、本文をエスケープして強調する</summary>
    public static string Snippet(string text, string keyword)
    {
        var normalized = Normalize(text);
        var query = Normalize(keyword);
        var match = normalized.IndexOf(query, StringComparison.Ordinal);
        var start = Math.Max(0, match - 45);
        var end = Math.Min(text.Length, Math.Max(start + 160, match + query.Length));
        if (start > 0 && char.IsLowSurrogate(text[start]))
        {
            start--;
        }

        if (end < text.Length && char.IsLowSurrogate(text[end]))
        {
            end++;
        }

        var result = new StringBuilder(start > 0 ? "…" : "");
        for (var position = start; position < end;)
        {
            var next = query.Length == 0 ? -1 : normalized.IndexOf(query, position, StringComparison.Ordinal);
            if (next < 0 || next + query.Length > end)
            {
                result.Append(WebUtility.HtmlEncode(text[position..end]));
                break;
            }

            result.Append(WebUtility.HtmlEncode(text[position..next]));
            result.Append("<mark>").Append(WebUtility.HtmlEncode(text.Substring(next, query.Length))).Append("</mark>");
            position = next + query.Length;
        }

        return result.Append(end < text.Length ? "…" : "").ToString();
    }

    /// <summary>導入前の本文も移行後の初回起動で検索可能にする</summary>
    public static async Task BackfillAsync(AppDbContext database, CancellationToken cancellationToken)
    {
        var markdown = new DiaryMarkdown();
        while (true)
        {
            var batch = await database.DiaryEntries.Where(x => x.SearchText == null).Take(100).ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                return;
            }

            foreach (var entry in batch)
            {
                entry.PlainText = markdown.PlainText(entry.Content);
                entry.SearchText = Normalize(entry.PlainText);
            }

            await database.SaveChangesAsync(cancellationToken);
            database.ChangeTracker.Clear();
        }
    }
}

/// <summary>検索条件と日付の新しい順の結果</summary>
public sealed class SearchPage
{
    /// <summary>入力した部分一致語</summary>
    public string Keyword { get; init; } = "";

    /// <summary>未指定なら本人の全日記帳</summary>
    public string? BookId
    {
        get; init;
    }

    /// <summary>検索範囲の先頭日を含む</summary>
    public DateOnly? Start
    {
        get; init;
    }

    /// <summary>検索範囲の末尾日を含む</summary>
    public DateOnly? End
    {
        get; init;
    }

    /// <summary>現在のページ番号</summary>
    public int Page
    {
        get; init;
    }

    /// <summary>次のページに結果があるか</summary>
    public bool HasNext
    {
        get; init;
    }

    /// <summary>入力条件の検証結果</summary>
    public string? Error
    {
        get; init;
    }

    /// <summary>対象として選べる本人の日記帳</summary>
    public IReadOnlyList<Notebook> Books { get; init; } = [];

    /// <summary>所有者と検索条件を満たす現在の日記</summary>
    public IReadOnlyList<DiaryEntry> Entries { get; init; } = [];
}

/// <summary>選択した日記帳の同月同日の振り返り</summary>
public sealed class DiaryYears
{
    /// <summary>所有者を確認済みの日記帳</summary>
    public required Notebook Book
    {
        get; init;
    }

    /// <summary>比較する月日</summary>
    public DateOnly Date
    {
        get; init;
    }

    /// <summary>当年も含む全年度の本文を新しい順で表示する</summary>
    public IReadOnlyList<DiaryEntry> Entries { get; init; } = [];
}
