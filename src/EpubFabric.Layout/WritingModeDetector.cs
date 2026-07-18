using EpubFabric.Core.Models;

namespace EpubFabric.Layout;

/// <summary>
/// テキスト行の形状から、ページの書字方向（縦書き/横書き）を自動判定する。
/// 縦書きの行はOCRで縦長ボックス、横書きの行は横長ボックスになることを利用し、
/// 行数ではなく文字数で重み付けして本文の向きを多数決する（図中の短いラベルや
/// ノンブルに引っ張られないため）。
/// </summary>
public static class WritingModeDetector
{
    /// <summary>この縦横比を超えたら縦長/横長の行とみなす。正方形に近いブロックは判定に使わない。</summary>
    private const double AspectRatioThreshold = 1.5;

    /// <summary>判定に使う行の最小文字数。1文字のブロックは形状と向きが対応しない。</summary>
    private const int MinTextLength = 2;

    public static WritingMode DetectPageMode(IReadOnlyList<TextLine> lines)
    {
        var verticalWeight = 0;
        var horizontalWeight = 0;

        foreach (var line in lines)
        {
            var textLength = line.Text.Count(c => !char.IsWhiteSpace(c));
            if (textLength < MinTextLength)
            {
                continue;
            }

            if (line.Bounds.Height > line.Bounds.Width * AspectRatioThreshold)
            {
                verticalWeight += textLength;
            }
            else if (line.Bounds.Width > line.Bounds.Height * AspectRatioThreshold)
            {
                horizontalWeight += textLength;
            }
        }

        return verticalWeight > horizontalWeight ? WritingMode.Vertical : WritingMode.Horizontal;
    }

    /// <summary>
    /// 書籍全体の綴じ方向を決める。テキストを持つページの書字方向の多数決で、
    /// 縦書きページが多ければ縦書き（右綴じ）とする。
    /// </summary>
    public static WritingMode DetectDocumentMode(IReadOnlyList<WritingMode> pageModes)
    {
        var verticalCount = pageModes.Count(m => m == WritingMode.Vertical);
        return verticalCount > pageModes.Count - verticalCount ? WritingMode.Vertical : WritingMode.Horizontal;
    }
}
