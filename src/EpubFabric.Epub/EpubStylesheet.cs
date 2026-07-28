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
            line-height: 1.9;
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
            max-height: 100vh;
            width: auto;
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
