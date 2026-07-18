using EpubFabric.Core.Models;

namespace EpubFabric.Layout;

/// <summary>
/// 固定レイアウトEPUBの透明テキスト層用に、PDF/OCRの全テキスト行を
/// 再構成・図版除外・段落結合せず、その座標のままPageBlockへ変換する。
/// 読み順（DOM順＝選択・コピー・読み上げの順）は段組みを考慮し、
/// 横書きでは段ごとに上から下へ、縦書きでは段（横方向の帯）ごとに
/// 右の行から左へ並べる。
/// </summary>
public sealed class TextLayerBlockBuilder
{
    public List<PageBlock> Build(int pageNumber, IReadOnlyList<TextLine> lines, WritingMode writingMode = WritingMode.Horizontal)
    {
        var validLines = lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .ToList();

        // 縦書きは座標を転置（ページを時計回りに90°回した座標系）すると横書きと同じ問題になる:
        // 縦の行→横の行、横の段間の空白→縦のガター、右→左の行順→上→下の行順。
        // これにより段検出（ColumnDetector）をそのまま再利用できる。
        Func<TextLine, BoundingBox> boundsOf = writingMode == WritingMode.Vertical
            ? line => Transpose(line.Bounds)
            : line => line.Bounds;

        return ColumnDetector.DetectColumns(validLines, boundsOf)
            .SelectMany(column => column.OrderBy(line => boundsOf(line).Y).ThenBy(line => boundsOf(line).X))
            .Select((line, index) => new PageBlock
            {
                Id = $"p{pageNumber:0000}-b{index + 1:0000}",
                PageNumber = pageNumber,
                Bounds = line.Bounds,
                Type = BlockType.Body,
                OcrText = line.Text,
                OcrConfidence = line.Confidence,
                TextSource = line.Source,
                ReadingOrder = index,
                RequiresReview = line.Confidence < 0.85,
            })
            .ToList();
    }

    /// <summary>ページを時計回りに90°回した座標系へ変換する（右端が上端になる）。</summary>
    private static BoundingBox Transpose(BoundingBox b) =>
        new(b.Y, 1 - b.X - b.Width, b.Height, b.Width);
}
