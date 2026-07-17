using EpubFabric.Core.Models;
using EpubFabric.Document;
using EpubFabric.Epub;
using EpubFabric.Pdf;

if (args.Length == 0 || args[0] != "convert")
{
    PrintUsage();
    return 1;
}

string? inputPath = null;
string? outputPath = null;
var dpi = 300;

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--output":
            outputPath = args[++i];
            break;
        case "--dpi":
            dpi = int.Parse(args[++i]);
            break;
        default:
            inputPath ??= args[i];
            break;
    }
}

if (inputPath is null)
{
    Console.Error.WriteLine("エラー: 入力PDFを指定してください。");
    PrintUsage();
    return 1;
}

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"エラー: ファイルが見つかりません: {inputPath}");
    return 1;
}

outputPath ??= Path.ChangeExtension(inputPath, ".epub");

try
{
    ConvertPdfToEpub(inputPath, outputPath, dpi);
    Console.WriteLine($"EPUBを生成しました: {outputPath}");
    return 0;
}
catch (PdfLoadException ex)
{
    Console.Error.WriteLine($"エラー: {ex.Message}");
    return 1;
}

static void ConvertPdfToEpub(string inputPath, string outputPath, int dpi)
{
    var pdfService = new PdfDocumentService();
    var info = pdfService.GetInfo(inputPath);

    if (!info.HasTextLayer)
    {
        // 第1段階はテキストレイヤー抽出のみ対応。スキャン画像のみのPDFはOCR実装後に対応する。
        Console.WriteLine("警告: テキストレイヤーが見つかりません。第1段階ではOCR未対応のため、本文が空のEPUBになります。");
    }

    var workDirectory = Path.Combine(Path.GetTempPath(), $"epubfabric-{Guid.NewGuid():N}");
    Directory.CreateDirectory(workDirectory);

    var pages = new List<DocumentPage>();

    for (var i = 0; i < info.PageCount; i++)
    {
        var pageNumber = i + 1;
        Console.WriteLine($"ページ {pageNumber}/{info.PageCount} を処理しています...");

        var imagePath = Path.Combine(workDirectory, $"page-original-{pageNumber:0000}.png");
        pdfService.RenderPageToPng(inputPath, pageNumber, imagePath, dpi);

        var text = pdfService.ExtractPageText(inputPath, pageNumber);
        var pageInfo = info.Pages[i];

        var page = new DocumentPage
        {
            PageNumber = pageNumber,
            OriginalImagePath = imagePath,
            ProcessedImagePath = imagePath,
            PreviewImagePath = imagePath,
            Width = pageInfo.WidthPoints,
            Height = pageInfo.HeightPoints,
            WritingMode = WritingMode.Horizontal,
            Status = PageProcessingStatus.OcrCompleted,
        };

        if (!string.IsNullOrWhiteSpace(text))
        {
            page.Blocks.Add(new PageBlock
            {
                Id = $"p{pageNumber:0000}-b0001",
                PageNumber = pageNumber,
                Bounds = new BoundingBox(0, 0, 1, 1),
                Type = BlockType.Body,
                OcrText = text,
                ReadingOrder = 0,
            });
        }

        pages.Add(page);
    }

    var title = Path.GetFileNameWithoutExtension(inputPath);
    var chapters = new DocumentBuilder().BuildChapters(pages, title);

    var project = new EpubFabricProject
    {
        Id = Guid.NewGuid(),
        Title = title,
        SourcePdfPath = inputPath,
        Pages = pages,
        Chapters = [.. chapters],
    };

    var blocksById = pages.SelectMany(p => p.Blocks).ToDictionary(b => b.Id);

    new EpubPackageBuilder().Build(project, chapters, blocksById, outputPath);
}

static void PrintUsage()
{
    Console.WriteLine("使い方: epubfabric convert <input.pdf> [--output <output.epub>] [--dpi <dpi>]");
}
