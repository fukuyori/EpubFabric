using EpubFabric.Core.Models;

namespace EpubFabric.Layout;

/// <summary>
/// 固定レイアウトEPUBの透明テキスト層用に、PDF/OCRの全テキスト行を
/// 再構成・図版除外・段落結合せず、その座標のままPageBlockへ変換する。
/// </summary>
public sealed class TextLayerBlockBuilder
{
    public List<PageBlock> Build(int pageNumber, IReadOnlyList<TextLine> lines)
    {
        return lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .OrderBy(line => line.Bounds.Y)
            .ThenBy(line => line.Bounds.X)
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
}
