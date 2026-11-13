using Hibitsuzuri.Web.Diary;

namespace Hibitsuzuri.Tests;

/// <summary>本文とプレビューで使うMarkdownの表現と通信・スクリプト境界</summary>
public sealed class DiaryMarkdownTests
{
    /// <summary>表・コード・チェックリスト・引用を必要な拡張だけで描画する</summary>
    [Test]
    public void SupportsAgreedMarkdownSyntax()
    {
        var html = new DiaryMarkdown().Render("# 見出し\n\n> 引用\n\n- [x] 済\n\n| 項目 | 値 |\n| --- | --- |\n| 本 | 1 |\n\n```text\n<script>\n```\n\n**太字** *斜体*");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("<h1>見出し</h1>"));
            Assert.That(html, Does.Contain("<table>"));
            Assert.That(html, Does.Contain("disabled"));
            Assert.That(html, Does.Contain("&lt;script&gt;"));
            Assert.That(html, Does.Contain("<strong>太字</strong>"));
        });
    }

    /// <summary>生HTML・危険なリンク・外部画像を能動的な要素にしない</summary>
    [TestCase("[リンク](javascript:alert%281%29)")]
    [TestCase("[リンク](data:text/html,test)")]
    [TestCase("[リンク](vbscript:msgbox%281%29)")]
    [TestCase("[リンク](javascript&#x3a;alert%281%29)")]
    [TestCase("<script>alert(1)</script>\n<img src=x onerror=alert(1)>")]
    [TestCase("![画像](https://tracker.example/image.gif)")]
    public void RejectsActiveHtmlAndUnmanagedImages(string input)
    {
        var html = new DiaryMarkdown().Render(input);

        Assert.That(html, Does.Not.Contain("<script").And.Not.Contain("<img").And.Not.Contain("href="));
    }

    /// <summary>許可リンクは別タブに開き、参照元と親画面へのアクセスを渡さない</summary>
    [Test]
    public void SafeLinksOpenWithoutOpenerOrReferrer()
    {
        var html = new DiaryMarkdown().Render("[リンク](https://example.com/?q=test&x=1)");

        Assert.That(html, Does.Contain("target=\"_blank\" rel=\"noopener noreferrer\"").And.Contain("<a href=\"https://example.com/"));
    }
}
