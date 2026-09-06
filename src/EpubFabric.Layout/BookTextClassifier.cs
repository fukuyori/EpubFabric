using EpubFabric.Core.Models;

namespace EpubFabric.Layout;

/// <summary>
/// 文字列そのものに含まれる書籍構造の手掛かりを判定する。
/// 現在は英語横書きで誤判定を抑えやすい番号付き見出しと定型見出しだけを扱う。
/// </summary>
internal static class BookTextClassifier
{
    private const int MaxHeadingTextLength = 120;
    private const int MaxNumberedHeadingTailWords = 8;

    private static readonly HashSet<string> CommonHeadingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "contents",
        "preface",
        "foreword",
        "introduction",
        "welcome",
        "glossary",
        "bibliography",
        "index",
        "appendix",
        "acknowledgments",
        "acknowledgements",
        "references",
    };

    public static BlockType? ClassifyHeading(string text)
    {
        if (LooksLikeExplicitChapterHeading(text))
        {
            return BlockType.ChapterTitle;
        }

        if (TryGetNumberedHeadingDepth(text, out var depth))
        {
            return depth switch
            {
                1 => BlockType.ChapterTitle,
                2 => BlockType.SectionHeading,
                _ => BlockType.Subheading,
            };
        }

        return IsCommonBookHeading(text) ? BlockType.ChapterTitle : null;
    }

    private static bool LooksLikeExplicitChapterHeading(string text)
    {
        var words = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || text.Length > MaxHeadingTextLength)
        {
            return false;
        }

        if (words[0].Equals("chapter", StringComparison.OrdinalIgnoreCase))
        {
            var numberToken = words[1];
            return words.Length <= 10
                && numberToken.EndsWith(".", StringComparison.Ordinal)
                && numberToken[..^1].Length > 0
                && numberToken[..^1].All(char.IsAsciiDigit)
                && !text.TrimEnd().EndsWith(".", StringComparison.Ordinal);
        }

        if (!words[0].Equals("part", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (words.Length > 10 || text.TrimEnd().EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var partNumber = words[1].TrimEnd('.', ':');
        return partNumber.Length > 0
            && partNumber.All(character => char.IsAsciiDigit(character)
                || character is 'I' or 'V' or 'X' or 'L' or 'C' or 'D' or 'M'
                or 'i' or 'v' or 'x' or 'l' or 'c' or 'd' or 'm');
    }

    public static bool LooksLikeFootnote(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed[0] is '*' or '†' or '‡' or '§')
        {
            return true;
        }

        if (!char.IsAsciiDigit(trimmed[0]))
        {
            return false;
        }

        for (var i = 1; i < trimmed.Length; i++)
        {
            if (char.IsAsciiDigit(trimmed[i]))
            {
                continue;
            }

            return trimmed[i] is '.' or ')' or ']' or ':' || char.IsWhiteSpace(trimmed[i]);
        }

        return false;
    }

    private static bool TryGetNumberedHeadingDepth(string text, out int depth)
    {
        depth = 0;
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxHeadingTextLength)
        {
            return false;
        }

        var index = 0;
        while (true)
        {
            var digitsStart = index;
            while (index < trimmed.Length && char.IsAsciiDigit(trimmed[index]))
            {
                index++;
            }

            if (index == digitsStart)
            {
                return false;
            }

            depth++;
            if (index < trimmed.Length && trimmed[index] == '.')
            {
                if (index + 1 < trimmed.Length && char.IsAsciiDigit(trimmed[index + 1]))
                {
                    index++;
                    continue;
                }

                index++;
            }

            break;
        }

        var prefixEnd = index;
        while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index]))
        {
            index++;
        }

        if (index == prefixEnd || index == trimmed.Length || !char.IsAsciiLetterUpper(trimmed[index]))
        {
            return false;
        }

        var tail = trimmed[index..];
        var words = tail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > MaxNumberedHeadingTailWords)
        {
            return false;
        }

        if (words.Length >= 2 && words[^1].All(char.IsAsciiDigit))
        {
            return false;
        }

        if (tail.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var word in words.Take(words.Length - 1))
        {
            if (word.Contains(';') || word.EndsWith('.') || word.EndsWith(','))
            {
                return false;
            }
        }

        depth = Math.Clamp(depth, 1, 6);
        return true;
    }

    private static bool IsCommonBookHeading(string text)
    {
        var words = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > 6)
        {
            return false;
        }

        var first = words[0].TrimEnd('!', ':', '.', ',');
        if (!CommonHeadingWords.Contains(first))
        {
            return false;
        }

        return !words.Any(word => word.Length > 0 && word.All(char.IsAsciiDigit));
    }
}
