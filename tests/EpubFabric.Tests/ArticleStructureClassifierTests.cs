using EpubFabric.Core.Models;
using EpubFabric.Layout;

namespace EpubFabric.Tests;

public sealed class ArticleStructureClassifierTests
{
    [Theory]
    [InlineData("中中")]
    [InlineData("56车")]
    [InlineData("小51.7")]
    [InlineData("24て子どもの地図は発達する")]
    public void Classify_DemotesShortOcrFragmentsFromHeadings(string headingText)
    {
        var page = Page(
            Block("body-1", BlockType.Body, "前段の本文。", 0, 0.10, 0.08),
            Block("false-heading", BlockType.Subheading, headingText, 1, 0.25, 0.04),
            Block("body-2", BlockType.Body, new string('本', 300), 2, 0.35, 0.50));

        var result = new ArticleStructureClassifier().Classify([page]);

        Assert.Equal(BlockType.Body, page.Blocks.Single(block => block.Id == "false-heading").Type);
        Assert.Equal(1, result.DemotedFalseHeadings);
    }

    [Fact]
    public void Classify_DemotesOcrFragmentEvenWhenInitiallyMarkedAsChapterTitle()
    {
        var page = Page(
            Block("false-title", BlockType.ChapterTitle, "56车", 0, 0.10, 0.04),
            Block("body", BlockType.Body, new string('本', 300), 1, 0.20, 0.50));

        var result = new ArticleStructureClassifier().Classify([page]);

        Assert.Equal(BlockType.Body, page.Blocks[0].Type);
        Assert.Equal(1, result.DemotedFalseHeadings);
    }

    [Fact]
    public void Classify_PromotesArticleTitleAndClassifiesHeaderMetadata()
    {
        var page = Page(
            Block("title", BlockType.SectionHeading, "科学を支援するAI：現状と課題", 0, 0.10, 0.05),
            Block("author", BlockType.Body, "相澤彰子", 1, 0.17, 0.025),
            Block("affiliation", BlockType.Subheading, "国立情報学研究所", 2, 0.20, 0.025),
            Block("abstract", BlockType.Body, new string('要', 100), 3, 0.25, 0.10, width: 0.8),
            Block("section", BlockType.Subheading, "論文の理解", 4, 0.40, 0.03),
            Block("body", BlockType.Body, new string('本', 300), 5, 0.45, 0.30));

        var result = new ArticleStructureClassifier().Classify([page]);

        Assert.Equal(1, result.ArticleTitles);
        Assert.Equal(BlockType.ChapterTitle, page.Blocks.Single(block => block.Id == "title").Type);
        Assert.Equal(BlockType.Author, page.Blocks.Single(block => block.Id == "author").Type);
        Assert.Equal(BlockType.Affiliation, page.Blocks.Single(block => block.Id == "affiliation").Type);
        Assert.Equal(BlockType.Abstract, page.Blocks.Single(block => block.Id == "abstract").Type);
        Assert.Equal(BlockType.Subheading, page.Blocks.Single(block => block.Id == "section").Type);
    }

    [Fact]
    public void Classify_DemotesHeadingThatContinuesAnUnfinishedParagraph()
    {
        var page = Page(
            Block("body-1", BlockType.Body, "日本人としては坂口志文さん、北川進さんの二人がノーベル賞を受賞し", 0, 0.10, 0.08),
            Block("false-heading", BlockType.Subheading, "自然科学三部門の受賞者は二十七人目", 1, 0.18, 0.03),
            Block("body-2", BlockType.Body, new string('本', 300), 2, 0.22, 0.40));

        var result = new ArticleStructureClassifier().Classify([page]);

        Assert.Equal(1, result.DemotedFalseHeadings);
        Assert.Equal(BlockType.Body, page.Blocks.Single(block => block.Id == "false-heading").Type);
    }

    [Fact]
    public void Classify_ExcludesIssueLineAndAdjacentPublicationName()
    {
        var page = Page(
            Block("name", BlockType.Body, "KAGAKU", 0, 0.91, 0.02),
            Block("issue", BlockType.Body, "Jan.2026Vol.96No.1", 1, 0.94, 0.02),
            Block("body", BlockType.Body, new string('本', 300), 2, 0.20, 0.50));

        var result = new ArticleStructureClassifier().Classify([page]);

        Assert.Equal(2, result.ExcludedFurniture);
        Assert.All(page.Blocks.Take(2), block => Assert.True(block.IsExcluded));
    }

    [Fact]
    public void Classify_DoesNotTreatDiagramLabelsAsTitleAndAuthors()
    {
        var page = Page(
            Block("label", BlockType.Subheading, "データ整理", 0, 0.10, 0.03),
            Block("label-2", BlockType.Subheading, "文献調査", 1, 0.15, 0.03),
            Block("label-3", BlockType.Subheading, "実験計画", 2, 0.20, 0.03),
            Block("body", BlockType.Body, new string('本', 300), 3, 0.30, 0.50));

        var result = new ArticleStructureClassifier().Classify([page]);

        Assert.Equal(0, result.ArticleTitles);
        Assert.DoesNotContain(page.Blocks, block => block.Type == BlockType.Author);
    }

    [Fact]
    public void Classify_DoesNotTreatSentenceMentioningUniversityAsAffiliationEvidence()
    {
        var page = Page(
            Block("lead", BlockType.Body, "アファーマティブ・アクシ", 0, 0.08, 0.03),
            Block("fragment", BlockType.ChapterTitle, "ヨンの潜在的問題続報", 1, 0.12, 0.04),
            Block("sentence", BlockType.Body, "前号では、大学教授の女性比率について検討した", 2, 0.18, 0.04),
            Block("body", BlockType.Body, new string('本', 300), 3, 0.25, 0.50));

        var result = new ArticleStructureClassifier().Classify([page]);

        Assert.Equal(0, result.ArticleTitles);
        Assert.NotEqual(BlockType.Affiliation, page.Blocks.Single(block => block.Id == "sentence").Type);
    }

    [Fact]
    public void Classify_KickerAllowsArticleStartingBesidePreviousColumnContinuation()
    {
        var page = Page(
            Block("previous", BlockType.Body, "前の記事から続く本文断片", 0, 0.05, 0.04),
            Block("kicker", BlockType.Subheading, "重要文化財指定記念シンポジウム報告", 1, 0.10, 0.03),
            Block("title", BlockType.SectionHeading, "伊能図を未来へ", 2, 0.14, 0.04),
            Block("author", BlockType.Subheading, "夏目宗幸", 3, 0.20, 0.03),
            Block("body", BlockType.Body, new string('本', 300), 4, 0.26, 0.50));

        var result = new ArticleStructureClassifier().Classify([page]);

        Assert.Equal(1, result.ArticleTitles);
        Assert.Equal(BlockType.ChapterTitle, page.Blocks.Single(block => block.Id == "title").Type);
        Assert.Equal(BlockType.Kicker, page.Blocks.Single(block => block.Id == "kicker").Type);
    }

    private static DocumentPage Page(params PageBlock[] blocks)
    {
        var page = new DocumentPage
        {
            PageNumber = 1,
            OriginalImagePath = "page.png",
            ProcessedImagePath = "page.png",
            PreviewImagePath = "page.png",
            WritingMode = WritingMode.Horizontal,
        };
        page.Blocks.AddRange(blocks);
        return page;
    }

    private static PageBlock Block(
        string id,
        BlockType type,
        string text,
        int order,
        double y,
        double height,
        double width = 0.6) => new()
    {
        Id = id,
        PageNumber = 1,
        Bounds = new BoundingBox(0.1, y, width, height),
        Type = type,
        OcrText = text,
        ReadingOrder = order,
        HeadingLevel = type switch
        {
            BlockType.ChapterTitle => 1,
            BlockType.SectionHeading => 2,
            BlockType.Subheading => 3,
            _ => null,
        },
    };
}
