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

        figcaption {
            font-size: 0.85em;
        }

        aside {
            border: 1px solid currentColor;
            padding: 1em;
            margin: 1em;
        }
        """;
}
