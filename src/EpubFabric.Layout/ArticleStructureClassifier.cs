using System.Text.RegularExpressions;
using EpubFabric.Core.Models;

namespace EpubFabric.Layout;

/// <summary>
/// 雑誌・論文誌のページブロックを、記事タイトル・著者・所属・要旨へ構造化する。
/// 単ページの文字サイズだけでは本文中の太字断片を見出しと誤認しやすいため、ページ内の
/// 位置、前後の文章の連続性、著者・所属との並びを組み合わせて保守的に補正する。
/// </summary>
public sealed partial class ArticleStructureClassifier
{
    private const int MaxTitleLength = 80;

    public ArticleStructureResult Classify(IReadOnlyList<DocumentPage> pages)
    {
        var result = new MutableResult();

        foreach (var page in pages.OrderBy(page => page.PageNumber))
        {
            RemoveIssueFurniture(page, result);
            DemoteSentenceFragments(page, result);
            PromoteArticleHeader(page, result);
        }

        return new ArticleStructureResult(
            result.ArticleTitles,
            result.DemotedFalseHeadings,
            result.MetadataBlocks,
            result.ExcludedFurniture);
    }

    private static void RemoveIssueFurniture(DocumentPage page, MutableResult result)
    {
        var ordered = page.Blocks.OrderBy(block => block.ReadingOrder).ToList();

        for (var index = 0; index < ordered.Count; index++)
        {
            var block = ordered[index];
            if (block.IsExcluded || block.IsManuallyEdited)
            {
                continue;
            }

            var text = TextOf(block).Trim();
            if (!LooksLikeIssueFurniture(text))
            {
                continue;
            }

            ExcludeAsFurniture(block, result);

            // 誌名と号数が別ブロックになった場合も、隣接する短い誌名を一緒に落とす。
            foreach (var neighbour in ordered.Skip(Math.Max(0, index - 2)).Take(5))
            {
                if (!neighbour.IsExcluded
                    && !neighbour.IsManuallyEdited
                    && TextOf(neighbour).Trim().Length is > 0 and <= 20
                    && (IsAllLatinCapital(TextOf(neighbour)) || IsRepeatedPublicationName(TextOf(neighbour))))
                {
                    ExcludeAsFurniture(neighbour, result);
                }
            }
        }
    }

    private static void DemoteSentenceFragments(DocumentPage page, MutableResult result)
    {
        var ordered = page.Blocks
            .Where(block => !block.IsExcluded)
            .OrderBy(block => block.ReadingOrder)
            .ToList();

        for (var index = 0; index < ordered.Count; index++)
        {
            var block = ordered[index];
            if (block.IsManuallyEdited
                || block.Type is not (BlockType.ChapterTitle or BlockType.SectionHeading or BlockType.Subheading))
            {
                continue;
            }

            var text = TextOf(block).Trim();
            if (text.Length == 0 || LooksLikeKicker(text))
            {
                continue;
            }

            var looksLikeOcrFragment = LooksLikeOcrFragmentHeading(text);
            if (block.Type == BlockType.ChapterTitle && !looksLikeOcrFragment)
            {
                continue;
            }

            if (!looksLikeOcrFragment && IsNumberedHeading(text))
            {
                continue;
            }

            var tooLongForHeading = text.Length > 60;
            var previous = index > 0 ? ordered[index - 1] : null;
            var continuesPreviousSentence = previous is { Type: BlockType.Body }
                && !EndsSentence(TextOf(previous))
                && !LooksLikeKicker(TextOf(previous))
                && AreAdjacentInSameTextColumn(previous, block, page.WritingMode)
                && (text.Length > 5 || EndsSentence(text) || StartsLikeContinuation(text));

            if (!looksLikeOcrFragment && !tooLongForHeading && !continuesPreviousSentence)
            {
                continue;
            }

            block.Type = BlockType.Body;
            block.HeadingLevel = null;
            result.DemotedFalseHeadings++;
        }
    }

    private static void PromoteArticleHeader(DocumentPage page, MutableResult result)
    {
        var ordered = page.Blocks
            .Where(block => !block.IsExcluded && !string.IsNullOrWhiteSpace(TextOf(block)))
            .OrderBy(block => block.ReadingOrder)
            .ToList();
        if (ordered.Count == 0)
        {
            return;
        }

        var bodyCharacters = ordered
            .Where(block => block.Type == BlockType.Body)
            .Sum(block => TextOf(block).Length);
        if (bodyCharacters < 250)
        {
            return;
        }

        var headingCount = ordered.Count(IsHeading);
        var candidate = ordered
            .Where(block => IsHeading(block)
                && TextOf(block).Trim().Length is >= 4 and <= MaxTitleLength
                && ReadingBounds(block, page.WritingMode).Y < 0.42
                && !LooksLikeCaptionOrFurniture(TextOf(block))
                && !LooksLikeCitation(TextOf(block))
                && !LooksLikeNoisyOcr(TextOf(block))
                && !StartsLikeContinuation(TextOf(block))
                && !LooksLikeAuthor(
                    TextOf(block),
                    block.Type,
                    HasNearbyAffiliation(block, ordered)))
            .Select(block => new
            {
                Block = block,
                Score = ScoreTitleCandidate(block, ordered, page.WritingMode, headingCount),
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => ReadingBounds(item.Block, page.WritingMode).Y)
            .FirstOrDefault();

        if (candidate is null || candidate.Score < 6)
        {
            return;
        }

        var title = candidate.Block;
        MergeTitleFragments(title, page);
        title.Type = BlockType.ChapterTitle;
        title.HeadingLevel = 1;
        title.ClassificationConfidence = Math.Max(title.ClassificationConfidence, 0.9);
        result.ArticleTitles++;

        MarkKicker(title, ordered, result);
        MarkHeaderMetadata(title, page, ordered, result);
    }

    private static int ScoreTitleCandidate(
        PageBlock candidate,
        IReadOnlyList<PageBlock> ordered,
        WritingMode writingMode,
        int headingCount)
    {
        var score = candidate.Type switch
        {
            BlockType.ChapterTitle => 4,
            BlockType.SectionHeading => 3,
            BlockType.Subheading => 2,
            _ => 1,
        };

        var bounds = ReadingBounds(candidate, writingMode);
        score += bounds.Y < 0.2 ? 2 : bounds.Y < 0.32 ? 1 : 0;

        var index = IndexOf(ordered, candidate);
        var following = ordered.Skip(index + 1).Take(4).ToList();
        var hasAffiliation = following.Any(block => LooksLikeAffiliation(TextOf(block)));
        var hasAuthor = following.Any(block => LooksLikeAuthor(TextOf(block), block.Type, hasAffiliation));
        if (hasAffiliation)
        {
            score += hasAuthor ? 4 : 2;
        }

        var preceding = ordered.Take(index).TakeLast(3).ToList();
        var hasKicker = preceding.Any(block => LooksLikeKicker(TextOf(block)));
        if (hasKicker)
        {
            score += 3;
        }

        if (ArticleTitleMarkerPattern().IsMatch(TextOf(candidate)))
        {
            score++;
        }

        if (!hasKicker && preceding.Any(block => block.Type == BlockType.Body && !LooksLikeKicker(TextOf(block))))
        {
            score -= 2;
        }

        if (headingCount > 12)
        {
            score -= 4;
        }
        else if (headingCount > 8)
        {
            score -= 2;
        }

        return score;
    }

    private static void MergeTitleFragments(PageBlock title, DocumentPage page)
    {
        if (title.Type != BlockType.ChapterTitle)
        {
            return;
        }

        var titleBounds = ReadingBounds(title, page.WritingMode);
        var fragments = page.Blocks
            .Where(block => !block.IsExcluded
                && !ReferenceEquals(block, title)
                && block.Type == title.Type
                && TextOf(block).Trim().Length is >= 2 and <= MaxTitleLength
                && ReadingBounds(block, page.WritingMode).Y < 0.32
                && !LooksLikeAuthor(TextOf(block), block.Type, hasNearbyAffiliation: false))
            .Where(block =>
            {
                var bounds = ReadingBounds(block, page.WritingMode);
                return Math.Abs(bounds.Y - titleBounds.Y) < 0.12
                    || Math.Abs(bounds.Y - (titleBounds.Y + titleBounds.Height)) < 0.055;
            })
            .Append(title)
            .Distinct()
            .ToList();

        fragments = OrderTitleFragments(fragments, page.WritingMode);

        if (fragments.Count <= 1 || fragments.Sum(block => TextOf(block).Length) > MaxTitleLength)
        {
            return;
        }

        var mergedText = string.Concat(fragments.Select(block => TextOf(block).Trim()));
        if (title.CorrectedText is null)
        {
            title.OcrText = mergedText;
        }
        else
        {
            title.CorrectedText = mergedText;
        }
        title.Bounds = fragments.Select(block => block.Bounds).Aggregate(Union);
        title.ReadingOrder = fragments.Min(block => block.ReadingOrder);

        foreach (var fragment in fragments.Where(block => !ReferenceEquals(block, title)))
        {
            fragment.IsExcluded = true;
        }
    }

    private static void MarkKicker(PageBlock title, IReadOnlyList<PageBlock> ordered, MutableResult result)
    {
        var index = IndexOf(ordered, title);
        if (index <= 0)
        {
            return;
        }

        foreach (var block in ordered.Take(index).TakeLast(2))
        {
            if (!block.IsExcluded && !block.IsManuallyEdited && LooksLikeKicker(TextOf(block)))
            {
                block.Type = BlockType.Kicker;
                block.HeadingLevel = null;
                result.MetadataBlocks++;
            }
        }
    }

    private static void MarkHeaderMetadata(
        PageBlock title,
        DocumentPage page,
        IReadOnlyList<PageBlock> ordered,
        MutableResult result)
    {
        var index = IndexOf(ordered, title);
        var following = ordered.Skip(index + 1).Where(block => !block.IsExcluded).Take(6).ToList();
        var hasAffiliation = following.Any(block => LooksLikeAffiliation(TextOf(block)));

        for (var followingIndex = 0; followingIndex < following.Count; followingIndex++)
        {
            var block = following[followingIndex];
            if (block.IsManuallyEdited || block.Type == BlockType.ChapterTitle)
            {
                continue;
            }

            var text = TextOf(block).Trim();
            if (LooksLikeAffiliation(text))
            {
                block.Type = BlockType.Affiliation;
                block.HeadingLevel = null;
                result.MetadataBlocks++;
                continue;
            }

            if (LooksLikeAuthor(text, block.Type, hasAffiliation)
                || (followingIndex == 0 && JapaneseNamePattern().IsMatch(text)))
            {
                block.Type = BlockType.Author;
                block.HeadingLevel = null;
                result.MetadataBlocks++;
                continue;
            }

            var bounds = ReadingBounds(block, page.WritingMode);
            if (block.Type == BlockType.Body
                && text.Length is >= 80 and <= 800
                && bounds.Width >= 0.65
                && bounds.Y < 0.55)
            {
                block.Type = BlockType.Abstract;
                result.MetadataBlocks++;
            }
        }

        // 対談・論文のリードは2段本文より先に紙面全幅で置かれる一方、段組みの仮読み順では
        // 著者欄より後へ回ることがある。記事開始ページの全幅本文を座標から拾い直す。
        var abstractBlock = page.Blocks
            .Where(block => !block.IsExcluded && !block.IsManuallyEdited && block.Type == BlockType.Body)
            .Where(block =>
            {
                var bounds = ReadingBounds(block, page.WritingMode);
                var length = TextOf(block).Trim().Length;
                return length is >= 80 and <= 800 && bounds.Width >= 0.65 && bounds.Y < 0.55;
            })
            .OrderBy(block => ReadingBounds(block, page.WritingMode).Y)
            .FirstOrDefault();
        if (abstractBlock is not null)
        {
            abstractBlock.Type = BlockType.Abstract;
            result.MetadataBlocks++;
        }
    }

    private static bool LooksLikeAuthor(string text, BlockType type, bool hasNearbyAffiliation)
    {
        var compact = string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
        if (compact.Length is < 2 or > 30
            || compact.Any(character => character is '。' or '、' or '，' or ',' or ':' or '：' or '!' or '！' or '?' or '？')
            || LooksLikeAffiliation(compact)
            || LooksLikeKicker(compact)
            || NonAuthorTermPattern().IsMatch(compact))
        {
            return false;
        }

        if (type is BlockType.SectionHeading or BlockType.Subheading)
        {
            return JapaneseNamePattern().IsMatch(compact);
        }

        return hasNearbyAffiliation && JapaneseNamePattern().IsMatch(compact);
    }

    private static bool LooksLikeAffiliation(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length is > 0 and <= 80
            && !SentenceLeadPattern().IsMatch(trimmed)
            && AffiliationPattern().IsMatch(trimmed);
    }

    private static bool LooksLikeKicker(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 40 && KickerPattern().IsMatch(trimmed);
    }

    private static bool LooksLikeCaptionOrFurniture(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0
            || LooksLikeIssueFurniture(trimmed)
            || CaptionPattern().IsMatch(trimmed)
            || trimmed.All(character => char.IsDigit(character) || char.IsWhiteSpace(character) || character is '-' or '/');
    }

    private static bool IsHeading(PageBlock block) =>
        block.Type is BlockType.ChapterTitle or BlockType.SectionHeading or BlockType.Subheading;

    private static bool IsNumberedHeading(string text) => NumberedHeadingPattern().IsMatch(text.Trim());

    private static bool LooksLikeOcrFragmentHeading(string text)
    {
        var compact = string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
        if (compact.Length == 0)
        {
            return false;
        }

        return RepeatedGlyphPattern().IsMatch(compact)
            || SuspiciousNumericPrefixPattern().IsMatch(compact)
            || (compact.Length <= 8
                && compact.Any(char.IsDigit)
                && !SingleDigitHeadingPattern().IsMatch(compact));
    }

    private static bool EndsSentence(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.Length > 0 && trimmed[^1] is '。' or '．' or '.' or '!' or '！' or '?' or '？'
            or ':' or '：' or ';' or '；' or '」' or '』' or ')' or '）' or ']' or '】';
    }

    private static bool StartsLikeContinuation(string text)
    {
        var first = text.TrimStart().FirstOrDefault();
        return first is >= 'ぁ' and <= 'ゖ' || char.IsAsciiLetterLower(first);
    }

    private static bool AreAdjacentInSameTextColumn(PageBlock previous, PageBlock current, WritingMode mode)
    {
        var a = ReadingBounds(previous, mode);
        var b = ReadingBounds(current, mode);
        var overlap = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        var narrower = Math.Min(a.Width, b.Width);
        var gap = b.Y - (a.Y + a.Height);
        return narrower > 0 && overlap / narrower >= 0.45 && gap is >= -0.015 and <= 0.04;
    }

    private static BoundingBox ReadingBounds(PageBlock block, WritingMode mode) => mode == WritingMode.Vertical
        ? new BoundingBox(block.Bounds.Y, 1 - block.Bounds.X - block.Bounds.Width, block.Bounds.Height, block.Bounds.Width)
        : block.Bounds;

    private static BoundingBox Union(BoundingBox a, BoundingBox b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var right = Math.Max(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new BoundingBox(x, y, right - x, bottom - y);
    }

    private static void ExcludeAsFurniture(PageBlock block, MutableResult result)
    {
        block.Type = BlockType.Footer;
        block.HeadingLevel = null;
        block.IsExcluded = true;
        result.ExcludedFurniture++;
    }

    private static string TextOf(PageBlock block) => block.CorrectedText ?? block.OcrText;

    private static bool LooksLikeIssueFurniture(string text)
    {
        var compact = string.Concat(text.Where(character => !char.IsWhiteSpace(character))).ToLowerInvariant();
        return (compact.Contains("vol", StringComparison.Ordinal)
                && compact.Contains("no", StringComparison.Ordinal)
                && YearPattern().IsMatch(compact))
            || (compact.Contains("kagaku", StringComparison.Ordinal)
                && (compact.Contains("vol", StringComparison.Ordinal) || compact.Contains("no", StringComparison.Ordinal)));
    }

    private static bool LooksLikeCitation(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("https://", StringComparison.OrdinalIgnoreCase)
            || CitationStartPattern().IsMatch(trimmed);
    }

    private static bool LooksLikeNoisyOcr(string text)
    {
        var trimmed = text.Trim();
        if (SuspiciousTitleStartPattern().IsMatch(trimmed))
        {
            return true;
        }

        var nonSpace = trimmed.Count(character => !char.IsWhiteSpace(character));
        var noisy = trimmed.Count(character => character is '`' or '~' or '^' or '|' or '\\' or '_' or '=' or '+' or '＋');
        return nonSpace > 0 && noisy >= 2 && (double)noisy / nonSpace >= 0.08;
    }

    private static bool HasNearbyAffiliation(PageBlock block, IReadOnlyList<PageBlock> ordered)
    {
        var index = IndexOf(ordered, block);
        return index >= 0 && ordered.Skip(index + 1).Take(4).Any(candidate => LooksLikeAffiliation(TextOf(candidate)));
    }

    private static List<PageBlock> OrderTitleFragments(List<PageBlock> fragments, WritingMode writingMode)
    {
        var remaining = fragments
            .OrderBy(block => ReadingBounds(block, writingMode).Y)
            .ThenBy(block => ReadingBounds(block, writingMode).X)
            .ToList();
        var rows = new List<List<PageBlock>>();

        foreach (var fragment in remaining)
        {
            var bounds = ReadingBounds(fragment, writingMode);
            var row = rows.FirstOrDefault(candidateRow => candidateRow.Any(candidate =>
            {
                var other = ReadingBounds(candidate, writingMode);
                var center = bounds.Y + bounds.Height / 2;
                var otherCenter = other.Y + other.Height / 2;
                return Math.Abs(center - otherCenter) <= Math.Min(bounds.Height, other.Height) * 0.35;
            }));

            if (row is null)
            {
                rows.Add([fragment]);
            }
            else
            {
                row.Add(fragment);
            }
        }

        return rows
            .OrderBy(row => row.Min(block => ReadingBounds(block, writingMode).Y))
            .SelectMany(row => row.OrderBy(block => ReadingBounds(block, writingMode).X))
            .ToList();
    }

    private static int IndexOf(IReadOnlyList<PageBlock> blocks, PageBlock target)
    {
        for (var index = 0; index < blocks.Count; index++)
        {
            if (ReferenceEquals(blocks[index], target))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsAllLatinCapital(string text)
    {
        var letters = text.Where(char.IsLetter).ToList();
        return letters.Count >= 3 && letters.All(character => !char.IsAsciiLetter(character) || char.IsAsciiLetterUpper(character));
    }

    private static bool IsRepeatedPublicationName(string text) =>
        text.Trim() is "科学" or "地理";

    [GeneratedRegex(@"(?:19|20)\d{2}")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"^(?:\[?\d{1,3}[\]\)．.ー—-]|注\s*[*0-9０-９])")]
    private static partial Regex CitationStartPattern();

    [GeneratedRegex(@"(?:大学|研究所|研究機構|研究科|学部|大学院|センター|博物館|高等学校|中学校|小学校|University|Institute|Laboratory|Center)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AffiliationPattern();

    [GeneratedRegex(@"^(?:前号|本稿|本文|ここでは|それでは|では[、，]|.*について[、，])")]
    private static partial Regex SentenceLeadPattern();

    [GeneratedRegex(@"^(?:[A-Z]{2,}[一-龯ぁ-ゖァ-ヺ]|[|`~_^+=＋]{2,})")]
    private static partial Regex SuspiciousTitleStartPattern();

    [GeneratedRegex(@"^(?:[一-龯々]{2,8}|[一-龯々]{2,8}[ぁ-ゖ]{2,12}|[一-龯々]{2,8}(?:・|\s)[一-龯々]{2,8})$")]
    private static partial Regex JapaneseNamePattern();

    [GeneratedRegex(@"(?:研究|科学|地理|論文|問題|方法|結果|考察|課題|社会|教育|技術|開発|未来|現在|現状|理解|背景|展望|序論|結論|確実性|について|調査|整理|計画)")]
    private static partial Regex NonAuthorTermPattern();

    [GeneratedRegex(@"^(?:[0-9０-９]+(?:\.[0-9０-９]+)*|第[0-9０-９一二三四五六七八九十百]+(?:章|節|項|話|回)|[IVXⅠⅡⅢⅣⅤⅥⅦⅧⅨⅩ]+[.．、]?)\s*\S+")]
    private static partial Regex NumberedHeadingPattern();

    [GeneratedRegex(@"^(?<glyph>\S)\k<glyph>{1,3}$")]
    private static partial Regex RepeatedGlyphPattern();

    [GeneratedRegex(@"^[0-9０-９]{2,}[^0-9０-９.．]")]
    private static partial Regex SuspiciousNumericPrefixPattern();

    [GeneratedRegex(@"^[0-9０-９](?:[.．、)）]|\s)*[^0-9０-９].{1,}$")]
    private static partial Regex SingleDigitHeadingPattern();

    [GeneratedRegex(@"(?:^|[〈<《【\[])(?:特集|連載|解説|対談|論壇|フォーラム|コラム|報告|論説|座談会)|(?:特集|連載|報告|コラム)$")]
    private static partial Regex KickerPattern();

    [GeneratedRegex(@"(?:第[0-9０-９一二三四五六七八九十百]+(?:話|回)|[①-⑳]|序論|はじめに|おわりに|結論)")]
    private static partial Regex ArticleTitleMarkerPattern();

    [GeneratedRegex(@"^(?:図|表|写真|口絵|Fig\.?|Table\s)[ 0-9０-９一二三四五六七八九十百IVXⅠⅡⅢⅣⅤⅥⅦⅧⅨⅩ\-—・.．:：]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CaptionPattern();

    private sealed class MutableResult
    {
        public int ArticleTitles { get; set; }
        public int DemotedFalseHeadings { get; set; }
        public int MetadataBlocks { get; set; }
        public int ExcludedFurniture { get; set; }
    }

}

public sealed record ArticleStructureResult(
    int ArticleTitles,
    int DemotedFalseHeadings,
    int MetadataBlocks,
    int ExcludedFurniture);
