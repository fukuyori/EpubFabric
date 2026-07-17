using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EpubFabric.Core.Models;

namespace EpubFabric.Evaluation;

/// <summary>
/// レポートに載せる1ページ分の素材。オーバーレイ画像パスはレポートフォルダーからの相対パス。
/// </summary>
public sealed record PageReportEntry(
    PageEvaluation Evaluation,
    string OverlayImageRelativePath,
    string FragmentHtml);

/// <summary>
/// ページ対照評価レポートを生成する。index.html（PDFページ画像+ブロック枠と
/// 生成EPUB断片の左右対照）と metrics.json（定量メトリクス）を書き出す。
/// </summary>
public sealed class EvaluationReportBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public void Write(string reportDirectory, string title, EvaluationSummary summary, IReadOnlyList<PageReportEntry> pages)
    {
        Directory.CreateDirectory(reportDirectory);

        File.WriteAllText(
            Path.Combine(reportDirectory, "metrics.json"),
            JsonSerializer.Serialize(summary, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        File.WriteAllText(
            Path.Combine(reportDirectory, "index.html"),
            BuildIndexHtml(title, summary, pages),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string BuildIndexHtml(string title, EvaluationSummary summary, IReadOnlyList<PageReportEntry> pages)
    {
        var encodedTitle = WebUtility.HtmlEncode(title);
        var html = new StringBuilder();

        html.Append($$"""
            <!DOCTYPE html>
            <html lang="ja">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{encodedTitle}} - レイアウト評価レポート</title>
            <style>
            :root { color-scheme: light dark; }
            body { font-family: "Yu Gothic UI", "Hiragino Sans", sans-serif; margin: 0; background: #f5f5f5; color: #222; }
            @media (prefers-color-scheme: dark) { body { background: #1c1c1e; color: #ddd; } .page, header { background: #2a2a2c !important; } .fragment { background: #1c1c1e !important; } }
            header { position: sticky; top: 0; background: #fff; border-bottom: 1px solid #8884; padding: 10px 16px; z-index: 10; }
            header h1 { font-size: 16px; margin: 0 0 6px; }
            .stats { display: flex; flex-wrap: wrap; gap: 12px; font-size: 13px; }
            .stats b { font-size: 15px; }
            .warn { color: #d32f2f; }
            .legend { display: flex; flex-wrap: wrap; gap: 8px; font-size: 12px; margin-top: 6px; }
            .legend span { display: inline-flex; align-items: center; gap: 4px; }
            .swatch { width: 12px; height: 12px; display: inline-block; border-radius: 2px; }
            .page { background: #fff; margin: 16px; border-radius: 8px; padding: 12px 16px; box-shadow: 0 1px 3px #0002; }
            .page h2 { font-size: 14px; margin: 0 0 8px; display: flex; flex-wrap: wrap; gap: 12px; align-items: baseline; }
            .page h2 .m { font-weight: normal; font-size: 12px; opacity: .8; }
            .row { display: flex; gap: 16px; align-items: flex-start; }
            .row > div { flex: 1 1 0; min-width: 0; }
            .row img { max-width: 100%; height: auto; border: 1px solid #8884; }
            .fragment { border: 1px solid #8884; border-radius: 4px; padding: 8px 12px; font-size: 13px; line-height: 1.7; overflow-x: auto; max-height: 85vh; overflow-y: auto; background: #fafafa; }
            .fragment h1 { font-size: 1.4em; } .fragment h2 { font-size: 1.2em; } .fragment h3 { font-size: 1.05em; }
            .fragment figure { margin: 8px 0; border: 1px dashed #8886; padding: 4px; }
            .fragment img { max-width: 100%; height: auto; }
            .fragment figcaption { font-size: .85em; opacity: .8; }
            .fragment aside { border-left: 3px solid #8e24aa; padding-left: 8px; }
            .fragment pre { background: #8881; padding: 8px; overflow-x: auto; }
            .empty { opacity: .6; font-size: 13px; }
            @media (max-width: 900px) { .row { flex-direction: column; } }
            </style>
            </head>
            <body>
            <header>
            <h1>{{encodedTitle}} - レイアウト評価レポート</h1>
            """);

        html.Append("<div class=\"stats\">");
        html.Append($"<span>ページ <b>{summary.PagesWithBlocks}/{summary.PageCount}</b> 解析済</span>");
        html.Append($"<span>テキスト網羅率 <b>{summary.TextCoverage:P1}</b></span>");
        html.Append($"<span>図版画像化 <b>{summary.FigureWithImageCount}/{summary.FigureCount}</b></span>");
        html.Append($"<span>見出し検出 <b>{summary.HeadingCount}</b> 件</span>");
        var dropped = summary.TextCharsDropped;
        html.Append($"<span class=\"{(dropped > 0 ? "warn" : "")}\">欠落文字 <b>{dropped:N0}</b></span>");
        html.Append($"<span class=\"{(summary.LowConfidenceIncludedCount > 0 ? "warn" : "")}\">低信頼ブロック混入 <b>{summary.LowConfidenceIncludedCount}</b> 件</span>");
        html.Append("</div>");

        html.Append("<div class=\"legend\">");
        foreach (var (type, hex) in BlockTypeColors.Hex)
        {
            html.Append($"<span><span class=\"swatch\" style=\"background:{hex}\"></span>{type}</span>");
        }
        html.Append("</div></header>");

        foreach (var page in pages)
        {
            var e = page.Evaluation;
            html.Append($"""
                <section class="page" id="page-{e.PageNumber}">
                <h2>ページ {e.PageNumber}
                <span class="m">ブロック {e.BlockCount} / 網羅 {e.TextCoverage:P0} / 図 {e.FigureWithImageCount}/{e.FigureCount} / 見出し {e.HeadingCount}{(e.TextCharsDropped > 0 ? $" / <span class=\"warn\">欠落 {e.TextCharsDropped}字</span>" : "")}</span>
                </h2>
                <div class="row">
                <div><img src="{page.OverlayImageRelativePath}" loading="lazy" alt="ページ {e.PageNumber}"></div>
                <div><div class="fragment">{(string.IsNullOrWhiteSpace(page.FragmentHtml) ? "<p class=\"empty\">（このページから出力されるブロックはありません）</p>" : page.FragmentHtml)}</div></div>
                </div>
                </section>
                """);
        }

        html.Append("</body></html>");
        return html.ToString();
    }
}
