using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Hibitsuzuri.Web.Diary;

/// <summary>閲覧・プレビューで同じ安全なMarkdown規則を使う</summary>
public sealed class DiaryMarkdown
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .DisableHtml().UsePipeTables().UseTaskLists().UsePreciseSourceLocation().Build();

    /// <summary>画像URLの位置だけを書き換え、本文・コード・改行を保持する</summary>
    public string RewriteImages(string text, Func<string, string> destination) => RewriteImageUrls(text, ImageId, destination);

    /// <summary>書き出しZIPの画像参照だけを管理画像の参照へ戻す</summary>
    public string ImportImages(string text) => RewriteImageUrls(text,
        url => url is not null && url.StartsWith("../assets/", StringComparison.Ordinal) ? ImageId("attachment:" + url[10..]) : null,
        id => "attachment:" + id);

    /// <summary>Markdownの画像URLの範囲だけを置換し、コードや通常リンクを保持する</summary>
    private string RewriteImageUrls(string text, Func<string?, string?> identify, Func<string, string> destination)
    {
        var replacements = Markdown.Parse(text, _pipeline).Descendants<LinkInline>()
            .Where(x => x.IsImage && identify(x.Url) is not null)
            .Select(x => (Span: x.Reference?.UrlSpan ?? x.UrlSpan, Url: destination(identify(x.Url) ?? "")))
            .DistinctBy(x => x.Span.Start).OrderByDescending(x => x.Span.Start);
        var result = new System.Text.StringBuilder(text);
        foreach (var replacement in replacements)
        {
            var angled = text[replacement.Span.Start] == '<' && text[replacement.Span.End] == '>';
            result.Remove(replacement.Span.Start, replacement.Span.Length);
            result.Insert(replacement.Span.Start, angled ? "<" + replacement.Url + ">" : replacement.Url);
        }

        return result.ToString();
    }

    /// <summary>フォーム送信時のCRLF化によって、編集欄と保存側の文字数がずれないようLFに揃える</summary>
    public static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>Markdownの記号やリンク先を除いた表示本文を検索対象にする</summary>
    public string PlainText(string text) => Markdown.ToPlainText(text, _pipeline);

    /// <summary>コードブロックや通常リンクを除き、管理画像の参照だけを抽出する</summary>
    public HashSet<string> ImageIds(string text) => Markdown.Parse(text, _pipeline).Descendants<LinkInline>()
        .Where(x => x.IsImage && ImageId(x.Url) is not null).Select(x => ImageId(x.Url) ?? "").ToHashSet(StringComparer.Ordinal);

    /// <summary>ファイルパスや外部URLを受け入れず、サーバー発行IDに限定する</summary>
    private static string? ImageId(string? url) => url is not null && url.StartsWith("attachment:", StringComparison.Ordinal)
        && Guid.TryParseExact(url[11..], "N", out var id) ? id.ToString("N") : null;

    /// <summary>生HTMLと任意属性の拡張を受け入れず、必要な構文だけ描画する</summary>
    public string Render(string text, IReadOnlySet<string>? images = null)
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer);
        _pipeline.Setup(renderer);
        renderer.ObjectRenderers.Replace<LinkInlineRenderer>(new SafeLinkRenderer(images));

        renderer.Render(Markdown.Parse(text, _pipeline));

        return writer.ToString();
    }

    /// <summary>リンクの許可スキームを固定し、外部画像による通信を発生させない</summary>
    private sealed class SafeLinkRenderer(IReadOnlySet<string>? images) : HtmlObjectRenderer<LinkInline>
    {
        /// <summary>許可済みの管理画像だけを描画し、外部画像は代替テキストに留める</summary>
        protected override void Write(HtmlRenderer renderer, LinkInline link)
        {
            var url = link.Url ?? "";
            if (link.IsImage && ImageId(url) is { } id)
            {
                if (images?.Contains(id) == true)
                {
                    renderer.Write($"<button type=\"button\" class=\"image-trigger\" data-image-src=\"/images/{id}\" aria-label=\"画像を拡大\"><img src=\"/images/{id}\" alt=\"");
                    renderer.WriteEscape(ImageDescription(link));
                    renderer.Write("\" loading=\"lazy\" /></button>");
                }
                else
                {
                    renderer.Write("<span class=\"missing-image\">画像が見つかりません（バックアップに未収録、期限切れ、または削除済み）。必要な画像を再選択してください。</span>");
                    renderer.WriteChildren(link);
                }

                return;
            }

            if (link.IsImage || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http" or "mailto"))
            {
                renderer.WriteChildren(link);
                return;
            }

            renderer.Write("<a href=\"");
            renderer.WriteEscapeUrl(url);
            renderer.Write("\" target=\"_blank\" rel=\"noopener noreferrer\">");
            renderer.WriteChildren(link);
            renderer.Write("</a>");
        }

        /// <summary>装飾や入れ子の画像をHTMLにせず、alt属性に入れる説明文を取り出す</summary>
        private static string ImageDescription(LinkInline link)
        {
            using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            // 管理画像用レンダラーを再利用すると入れ子の画像も描画されるため、標準のテキスト描画を使う。
            var renderer = new HtmlRenderer(writer)
            {
                EnableHtmlForInline = false,
                EnableHtmlEscape = false
            };
            renderer.WriteChildren(link);

            return writer.ToString();
        }
    }
}
