using EpubFabric.Core.Models;
using CoreTextLine = EpubFabric.Core.Models.TextLine;

namespace EpubFabric.Ocr;

public sealed record OcrLineFilterResult(IReadOnlyList<CoreTextLine> Lines, int DroppedCount);

/// <summary>
/// 表紙・飾りページで発生する低信頼のOCRゴミ行（例: 装飾文字の誤読「Lin• 1v-n-t:」）が
/// そのまま本文化されるのを防ぐ。信頼度の足切りに加えて、判定が揺れる中間帯では
/// 文字種構成（記号だらけの行か）を併用し、正しい本文の誤削除を抑える。
/// </summary>
public sealed class OcrLineFilter
{
    private readonly double _minimumConfidence;
    private readonly double _reviewConfidence;
    private readonly double _minimumWordCharRatio;

    /// <param name="minimumConfidence">これ未満の行は無条件で破棄する。</param>
    /// <param name="reviewConfidence">これ未満（かつ足切り以上）の行は文字種構成で判定する。要確認しきい値と同じ0.85。</param>
    /// <param name="minimumWordCharRatio">中間帯の行で、英数字・かな・漢字が占める割合がこれ未満なら破棄する。</param>
    public OcrLineFilter(
        double minimumConfidence = 0.60,
        double reviewConfidence = 0.85,
        double minimumWordCharRatio = 0.5)
    {
        _minimumConfidence = minimumConfidence;
        _reviewConfidence = reviewConfidence;
        _minimumWordCharRatio = minimumWordCharRatio;
    }

    public OcrLineFilterResult Filter(IReadOnlyList<CoreTextLine> lines)
    {
        var kept = lines.Where(line => !ShouldDrop(line)).ToList();
        return new OcrLineFilterResult(kept, lines.Count - kept.Count);
    }

    /// <summary>数字ゴミとみなす数字連続列の最小長。ISBNや電話番号は区切り記号を含むため該当しない。</summary>
    private const int DigitNoiseMinRunLength = 16;

    private bool ShouldDrop(CoreTextLine line)
    {
        if (line.Source != TextSourceKind.Ocr)
        {
            return false;
        }

        if (line.Confidence < _minimumConfidence)
        {
            return true;
        }

        if (line.Confidence >= _reviewConfidence)
        {
            return false;
        }

        // 網点・ドット模様が長い数字列として誤認識されるゴミ（例: 05630900060000009000…）。
        // 数字はWordCharRatioを通過してしまうため、区切りのない長い数字連続列を別途弾く。
        return WordCharRatio(line.Text) < _minimumWordCharRatio || HasLongDigitRun(line.Text);
    }

    private static bool HasLongDigitRun(string text)
    {
        var run = 0;
        foreach (var ch in text)
        {
            if (char.IsAsciiDigit(ch))
            {
                if (++run >= DigitNoiseMinRunLength)
                {
                    return true;
                }
            }
            else if (!char.IsWhiteSpace(ch))
            {
                run = 0;
            }
        }

        return false;
    }

    /// <summary>空白を除く文字のうち、単語を構成しうる文字（Letter/Digit、CJK含む）の割合。</summary>
    private static double WordCharRatio(string text)
    {
        var total = 0;
        var wordChars = 0;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            total++;
            if (char.IsLetterOrDigit(ch))
            {
                wordChars++;
            }
        }

        return total == 0 ? 0.0 : (double)wordChars / total;
    }
}
