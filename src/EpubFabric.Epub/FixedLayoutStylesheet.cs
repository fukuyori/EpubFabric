namespace EpubFabric.Epub;

/// <summary>
/// PDFページ画像と透明テキスト層を同じ固定座標上へ重ねるスタイル。
/// テキストは視覚的に透明だが、検索・選択・読み上げのためDOMには残す。
/// </summary>
public static class FixedLayoutStylesheet
{
    public const string Content = """
        html,
        body {
            width: 100%;
            height: 100%;
            margin: 0;
            padding: 0;
            overflow: hidden;
        }

        .page-container {
            position: relative;
            width: 100%;
            height: 100%;
            margin: 0;
            padding: 0;
            overflow: hidden;
        }

        .page-image {
            position: absolute;
            top: 0;
            left: 0;
            width: 100%;
            height: 100%;
            margin: 0;
            padding: 0;
            object-fit: fill;
            user-select: none;
            -webkit-user-select: none;
        }

        .text-layer {
            position: absolute;
            top: 0;
            left: 0;
            width: 100%;
            height: 100%;
            overflow: hidden;
            z-index: 1;
        }

        .positioned-text {
            position: absolute;
            display: block;
            margin: 0;
            padding: 0;
            border: 0;
            overflow: visible;
            color: transparent;
            -webkit-text-fill-color: transparent;
            white-space: pre;
            line-height: 1;
            font-family: sans-serif;
            transform-origin: top left;
            user-select: text;
            -webkit-user-select: text;
        }

        .positioned-text::selection {
            background: rgba(30, 110, 255, 0.35);
        }
        """;
}
