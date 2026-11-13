using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax.Inlines;

namespace Hibitsuzuri.Web.Diary;

/// <summary>閲覧・プレビューで同じ安全なMarkdown規則を使う</summary>
public sealed class DiaryMarkdown
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .DisableHtml().UsePipeTables().UseTaskLists().Build();

    /// <summary>フォーム送信時のCRLF化によって、編集欄と保存側の文字数がずれないようLFに揃える</summary>
    public static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>生HTMLと任意属性の拡張を受け入れず、必要な構文だけ描画する</summary>
    public string Render(string text)
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer);
        _pipeline.Setup(renderer);
        renderer.ObjectRenderers.Replace<LinkInlineRenderer>(new SafeLinkRenderer());

        renderer.Render(Markdown.Parse(text, _pipeline));

        return writer.ToString();
    }

    /// <summary>リンクの許可スキームを固定し、外部画像による通信を発生させない</summary>
    private sealed class SafeLinkRenderer : HtmlObjectRenderer<LinkInline>
    {
        /// <summary>画像は管理画像の実装段階まで代替テキストに留める</summary>
        protected override void Write(HtmlRenderer renderer, LinkInline link)
        {
            var url = link.Url ?? "";
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
    }
}
