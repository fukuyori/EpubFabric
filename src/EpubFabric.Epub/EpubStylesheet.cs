namespace EpubFabric.Epub;

/// <summary>
/// 12.6 縦書きCSSを含む基本スタイルシート。縦書きは html.vertical 指定時のみ適用される。
/// </summary>
public static class EpubStylesheet
{
    public const string Content = """
        html.vertical {
            writing-mode: vertical-rl;
            -epub-writing-mode: vertical-rl;
        }

        body {
            box-sizing: border-box;
            max-width: 42em;
            margin: 0 auto;
            padding: 4vw 5vw 8vw;
            font-family: "Yu Mincho", "Hiragino Mincho ProN", "Noto Serif CJK JP", serif;
            font-size: 1em;
            line-height: 1.95;
            text-align: justify;
            hanging-punctuation: allow-end;
            overflow-wrap: break-word;
        }

        html.vertical body {
            max-width: none;
            max-height: 42em;
            margin: auto;
        }

        article {
            orphans: 2;
            widows: 2;
        }

        .article-header {
            margin-block-end: 2.5em;
            padding-block-end: 1.25em;
            border-block-end: 1px solid currentColor;
            text-align: start;
        }

        .article-header h1,
        .article-header h2,
        .article-header h3 {
            margin: 0.25em 0 0.7em;
            font-size: 2em;
            line-height: 1.35;
            letter-spacing: 0.04em;
            text-wrap: balance;
        }

        .kicker {
            margin: 0 0 0.6em;
            font-family: "Yu Gothic", "Hiragino Sans", "Noto Sans CJK JP", sans-serif;
            font-size: 0.85em;
            font-weight: bold;
            letter-spacing: 0.12em;
        }

        .byline {
            margin: 0.4em 0 0;
            font-weight: bold;
            text-align: end;
        }

        .affiliation {
            margin: 0.15em 0 0;
            font-size: 0.82em;
            text-align: end;
            opacity: 0.8;
        }

        article > p {
            margin: 0;
            text-indent: 1em;
        }

        article > h2,
        article > h3 {
            margin-block: 2.1em 0.8em;
            line-height: 1.45;
            text-align: start;
            break-after: avoid;
        }

        article > h2 {
            padding-block-end: 0.25em;
            border-block-end: 1px solid currentColor;
            font-size: 1.35em;
        }

        article > h3 {
            font-size: 1.12em;
        }

        article > h2 + p,
        article > h3 + p,
        article > figure + p,
        article > aside + p {
            text-indent: 0;
        }

        .abstract {
            margin-block: 0 2.5em;
            padding: 1em 1.25em;
            border: 1px solid currentColor;
            background: rgba(128, 128, 128, 0.07);
            font-size: 0.92em;
        }

        .abstract h2 {
            margin: 0 0 0.5em;
            font-size: 1em;
            font-family: "Yu Gothic", "Hiragino Sans", "Noto Sans CJK JP", sans-serif;
            letter-spacing: 0.12em;
        }

        .abstract p {
            margin: 0;
        }

        figure,
        aside,
        table {
            break-inside: avoid;
        }

        /* 抽出した図版は元のページ解像度のまま（数千px幅）収録されるため、
           上限を与えないとビューポートを突き抜けて横スクロールが発生する。
           height: auto と併せることで縦横比を保ったまま画面幅に収める。 */
        img {
            max-width: 100%;
            height: auto;
        }

        /* 縦書き（html.vertical）では行が横に伸びるため、はみ出すのは高さ方向になる。 */
        html.vertical img {
            max-height: 34em;
            width: auto;
        }

        html.vertical figure {
            max-height: 36em;
            overflow: hidden;
        }

        figure {
            margin: 1em 0;
        }

        figure img {
            display: block;
            margin: 0 auto;
        }

        figcaption {
            font-size: 0.85em;
            line-height: 1.6;
            text-align: start;
        }

        /* コード例は折り返さずに保持するため、はみ出す場合は枠内でスクロールさせる。 */
        pre {
            overflow-x: auto;
        }

        aside {
            border: 1px solid currentColor;
            padding: 1em;
            margin: 1em;
        }

        /* 表紙ページ（1ページ目をテキスト化せず画像として収録した場合）。
           リーダーごとの既定余白を打ち消し、縦横比を保ったまま画面に収める。 */
        body.cover {
            margin: 0;
            padding: 0;
            text-align: center;
        }

        body.cover img {
            max-width: 100%;
            max-height: 100vh;
            object-fit: contain;
        }
        """;
}
