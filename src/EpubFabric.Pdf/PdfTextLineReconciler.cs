using System.Text;
using System.Text.RegularExpressions;
using EpubFabric.Core.Models;

namespace EpubFabric.Pdf;

/// <summary>
/// PDFium の生テキストが持つ語間・句読点を、文字座標から再構成した行へ戻す。
/// 古い PostScript 由来 PDF ではグリフの外接矩形と送り幅が一致せず、座標間隔だけで
/// 欧文スペースを推定すると "ofknowledge" や "T he" のような誤りが生じるため。
/// </summary>
internal static partial class PdfTextLineReconciler
{
    private const int MaxSameRowSegments = 4;

    public static IReadOnlyList<TextLine> Reconcile(IReadOnlyList<TextLine> positionedLines, string rawText)
    {
        if (positionedLines.Count == 0 || string.IsNullOrWhiteSpace(rawText))
        {
            return positionedLines;
        }

        var rawLines = rawText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(NormalizeRawLine)
            .Where(line => line.Length > 0)
            .ToList();
        if (rawLines.Count == 0)
        {
            return positionedLines;
        }

        var rawKeys = rawLines.Select(MatchKey).ToList();
        var rawLineUsed = new bool[rawLines.Count];
        var ordered = positionedLines.OrderBy(line => line.Bounds.Y).ThenBy(line => line.Bounds.X).ToList();
        var result = new List<TextLine>(ordered.Count);
        var nextRawIndex = 0;

        for (var i = 0; i < ordered.Count;)
        {
            var groupLength = FindMatchingSameRowGroup(ordered, i, rawKeys, rawLineUsed, nextRawIndex, out var rawIndex);
            if (groupLength > 0)
            {
                var group = ordered.GetRange(i, groupLength);
                result.Add(MergeSegments(group, rawLines[rawIndex]));
                rawLineUsed[rawIndex] = true;
                nextRawIndex = Math.Max(nextRawIndex, rawIndex + 1);
                i += groupLength;
                continue;
            }

            var line = ordered[i];
            rawIndex = FindRawLine(MatchKey(line.Text), rawKeys, rawLineUsed, nextRawIndex);
            if (rawIndex >= 0)
            {
                result.Add(line with { Text = rawLines[rawIndex] });
                rawLineUsed[rawIndex] = true;
                nextRawIndex = Math.Max(nextRawIndex, rawIndex + 1);
            }
            else
            {
                result.Add(line);
            }

            i++;
        }

        return result;
    }

    private static int FindMatchingSameRowGroup(
        IReadOnlyList<TextLine> lines,
        int start,
        IReadOnlyList<string> rawKeys,
        IReadOnlyList<bool> rawLineUsed,
        int nextRawIndex,
        out int rawIndex)
    {
        rawIndex = -1;
        var maxLength = Math.Min(MaxSameRowSegments, lines.Count - start);

        for (var length = maxLength; length >= 2; length--)
        {
            var group = lines.Skip(start).Take(length).ToList();
            if (!AreOnSameVisualRow(group))
            {
                continue;
            }

            var key = string.Concat(group.Select(line => MatchKey(line.Text)));
            var candidate = FindRawLine(key, rawKeys, rawLineUsed, nextRawIndex);
            if (candidate >= 0)
            {
                rawIndex = candidate;
                return length;
            }
        }

        return 0;
    }

    private static bool AreOnSameVisualRow(IReadOnlyList<TextLine> lines)
    {
        var top = lines.Max(line => line.Bounds.Y);
        var bottom = lines.Min(line => line.Bounds.Y + line.Bounds.Height);
        var minHeight = lines.Min(line => line.Bounds.Height);
        return minHeight > 0 && bottom - top >= minHeight * 0.5;
    }

    private static int FindRawLine(
        string key,
        IReadOnlyList<string> rawKeys,
        IReadOnlyList<bool> rawLineUsed,
        int nextRawIndex)
    {
        if (key.Length == 0)
        {
            return -1;
        }

        for (var i = nextRawIndex; i < rawKeys.Count; i++)
        {
            if (!rawLineUsed[i] && rawKeys[i] == key)
            {
                return i;
            }
        }

        // PDF の内部記録順と表示順が異なる段組み文書向けのフォールバック。
        for (var i = 0; i < nextRawIndex && i < rawKeys.Count; i++)
        {
            if (!rawLineUsed[i] && rawKeys[i] == key)
            {
                return i;
            }
        }

        return -1;
    }

    private static TextLine MergeSegments(IReadOnlyList<TextLine> segments, string text)
    {
        var left = segments.Min(line => line.Bounds.X);
        var top = segments.Min(line => line.Bounds.Y);
        var right = segments.Max(line => line.Bounds.X + line.Bounds.Width);
        var bottom = segments.Max(line => line.Bounds.Y + line.Bounds.Height);
        var source = segments.All(line => line.Source == segments[0].Source)
            ? segments[0].Source
            : TextSourceKind.Unknown;
        var densities = segments.Where(line => line.InkDensity is not null).Select(line => line.InkDensity!.Value).ToList();

        return new TextLine(
            new BoundingBox(left, top, right - left, bottom - top),
            text,
            segments.Min(line => line.Confidence),
            source,
            densities.Count > 0 ? densities.Average() : null);
    }

    private static string NormalizeRawLine(string line)
    {
        var normalized = WhitespacePattern().Replace(line.Trim(), " ");
        normalized = SpaceBeforePunctuationPattern().Replace(normalized, "$1");
        normalized = SpaceAfterOpeningPunctuationPattern().Replace(normalized, "$1");
        normalized = SpaceAroundEmDashPattern().Replace(normalized, " — ");
        normalized = SpaceAfterClosingDoubleQuotePattern().Replace(normalized, "$1 ");
        normalized = SpaceBeforeOpeningDoubleQuotePattern().Replace(normalized, " $1");
        normalized = SpaceAfterQuotedSingleTextPattern().Replace(normalized, "$1 ");
        return BrokenHyphenPattern().Replace(normalized, "-");
    }

    private static string MatchKey(string text)
    {
        var key = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                key.Append(char.ToLowerInvariant(character));
            }
        }

        return key.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"\s+([,.;:!?\)\]\}])")]
    private static partial Regex SpaceBeforePunctuationPattern();

    [GeneratedRegex(@"([\(\[\{])\s+")]
    private static partial Regex SpaceAfterOpeningPunctuationPattern();

    [GeneratedRegex(@"(?<=\p{L})-\s+(?=\p{Ll})")]
    private static partial Regex BrokenHyphenPattern();

    [GeneratedRegex(@"\s*—\s*")]
    private static partial Regex SpaceAroundEmDashPattern();

    [GeneratedRegex(@"([”])(?=\p{L})")]
    private static partial Regex SpaceAfterClosingDoubleQuotePattern();

    [GeneratedRegex(@"(?<=\p{L})([“])")]
    private static partial Regex SpaceBeforeOpeningDoubleQuotePattern();

    [GeneratedRegex(@"(‘[^’]*’)(?=\p{L})")]
    private static partial Regex SpaceAfterQuotedSingleTextPattern();
}
