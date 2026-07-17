using EpubFabric.Core.Models;

namespace EpubFabric.Pdf;

public sealed record PdfTextLayerAssessment(
    bool IsUsable,
    string Reason,
    int RawCharacterCount,
    int PositionedCharacterCount,
    double PositionCoverage);

/// <summary>
/// PDFに文字列が存在するかだけでなく、固定レイアウトの透明テキスト層として
/// 使用できる座標と文字品質があるかをページ単位で判定する。
/// </summary>
public sealed class PdfTextLayerQualityEvaluator
{
    private const double MinimumPositionCoverage = 0.5;
    private const double MaximumInvalidCharacterRatio = 0.02;
    private const double MaximumPrivateUseCharacterRatio = 0.2;
    private const double MaximumInvalidBoundsRatio = 0.1;

    public PdfTextLayerAssessment Assess(string rawText, IReadOnlyList<TextLine> lines)
    {
        var rawCharacterCount = CountNonWhitespace(rawText);
        var positionedCharacterCount = lines.Sum(line => CountNonWhitespace(line.Text));
        var coverage = rawCharacterCount == 0
            ? 0
            : Math.Min(1, (double)positionedCharacterCount / rawCharacterCount);

        if (rawCharacterCount == 0)
        {
            return Unusable("文字列がありません。");
        }

        if (lines.Count == 0 || positionedCharacterCount == 0)
        {
            return Unusable("文字座標を取得できません。");
        }

        if (coverage < MinimumPositionCoverage)
        {
            return Unusable($"座標付き文字の網羅率が低すぎます（{coverage:P0}）。");
        }

        var positionedText = string.Concat(lines.Select(line => line.Text));
        var invalidCharacterRatio = Ratio(CountInvalidCharacters(positionedText), positionedCharacterCount);
        if (invalidCharacterRatio > MaximumInvalidCharacterRatio)
        {
            return Unusable($"不正文字の割合が高すぎます（{invalidCharacterRatio:P1}）。");
        }

        var privateUseCharacterRatio = Ratio(positionedText.Count(IsPrivateUseCharacter), positionedCharacterCount);
        if (privateUseCharacterRatio > MaximumPrivateUseCharacterRatio)
        {
            return Unusable($"私用領域文字の割合が高すぎます（{privateUseCharacterRatio:P1}）。");
        }

        var invalidBoundsRatio = Ratio(lines.Count(line => !HasUsableBounds(line.Bounds)), lines.Count);
        if (invalidBoundsRatio > MaximumInvalidBoundsRatio)
        {
            return Unusable($"不正な文字座標の割合が高すぎます（{invalidBoundsRatio:P1}）。");
        }

        return new PdfTextLayerAssessment(
            IsUsable: true,
            Reason: "PDF文字レイヤーを使用できます。",
            RawCharacterCount: rawCharacterCount,
            PositionedCharacterCount: positionedCharacterCount,
            PositionCoverage: coverage);

        PdfTextLayerAssessment Unusable(string reason) => new(
            IsUsable: false,
            Reason: reason,
            RawCharacterCount: rawCharacterCount,
            PositionedCharacterCount: positionedCharacterCount,
            PositionCoverage: coverage);
    }

    private static int CountNonWhitespace(string text) => text.Count(c => !char.IsWhiteSpace(c));

    private static double Ratio(int numerator, int denominator) =>
        denominator == 0 ? 0 : (double)numerator / denominator;

    private static int CountInvalidCharacters(string text)
    {
        var count = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                    continue;
                }

                count++;
                continue;
            }

            if (char.IsLowSurrogate(c)
                || c == '\uFFFD'
                || char.IsControl(c) && !char.IsWhiteSpace(c)
                || c is >= '\uFDD0' and <= '\uFDEF'
                || c is '\uFFFE' or '\uFFFF')
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsPrivateUseCharacter(char c) => c is >= '\uE000' and <= '\uF8FF';

    private static bool HasUsableBounds(BoundingBox bounds) =>
        double.IsFinite(bounds.X)
        && double.IsFinite(bounds.Y)
        && double.IsFinite(bounds.Width)
        && double.IsFinite(bounds.Height)
        && bounds.X >= -0.01
        && bounds.Y >= -0.01
        && bounds.Width > 0
        && bounds.Height > 0
        && bounds.X + bounds.Width <= 1.01
        && bounds.Y + bounds.Height <= 1.01;
}
