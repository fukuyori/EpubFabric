using System.Text;
using EpubFabric.Core.Models;
using EpubFabric.Document;
using EpubFabric.Epub;
using EpubFabric.Ocr;
using EpubFabric.Pdf;
using EpubFabric.Persistence;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    return args[0] switch
    {
        "convert" => await RunConvert(args),
        "analyze" => await RunAnalyze(args),
        "export" => RunExport(args),
        "info" => RunInfo(args),
        _ => Unknown(),
    };
}
catch (PdfLoadException ex)
{
    Console.Error.WriteLine($"エラー: {ex.Message}");
    return 1;
}
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine($"エラー: {ex.Message}");
    return 1;
}

static int Unknown()
{
    PrintUsage();
    return 1;
}

static async Task<int> RunConvert(string[] args)
{
    var (inputPath, options) = ParseOptions(args);
    if (inputPath is null || !RequireExistingFile(inputPath))
    {
        return 1;
    }

    var outputPath = options.GetValueOrDefault("--output") ?? Path.ChangeExtension(inputPath, ".epub");
    var dpi = ParseDpi(options);

    var workDirectory = Path.Combine(Path.GetTempPath(), $"epubfabric-{Guid.NewGuid():N}");
    var (project, pages) = await BuildProjectFromPdf(inputPath, workDirectory, dpi);

    var chapters = new DocumentBuilder().BuildChapters(pages, project.Title);
    var blocksById = pages.SelectMany(p => p.Blocks).ToDictionary(b => b.Id);
    new EpubPackageBuilder().Build(project, chapters, blocksById, outputPath);

    Console.WriteLine($"EPUBを生成しました: {outputPath}");
    return 0;
}

static async Task<int> RunAnalyze(string[] args)
{
    var (inputPath, options) = ParseOptions(args);
    if (inputPath is null || !RequireExistingFile(inputPath))
    {
        return 1;
    }

    if (!options.TryGetValue("--project", out var projectDirectory))
    {
        Console.Error.WriteLine("エラー: --project <book.efproj> を指定してください。");
        return 1;
    }

    var dpi = ParseDpi(options);
    var workDirectory = Path.Combine(Path.GetTempPath(), $"epubfabric-{Guid.NewGuid():N}");
    var (project, _) = await BuildProjectFromPdf(inputPath, workDirectory, dpi);

    new EfprojStore().Save(project, projectDirectory);

    Console.WriteLine($"プロジェクトを保存しました: {projectDirectory}");
    Console.WriteLine($"blocks/text 内のテキストファイルを編集して校正できます。校正後は次を実行してください:");
    Console.WriteLine($"  epubfabric export {projectDirectory} --format epub");
    return 0;
}

static int RunExport(string[] args)
{
    var (projectDirectory, options) = ParseOptions(args);
    if (projectDirectory is null)
    {
        Console.Error.WriteLine("エラー: プロジェクト（.efproj）を指定してください。");
        return 1;
    }

    if (!Directory.Exists(projectDirectory))
    {
        Console.Error.WriteLine($"エラー: プロジェクトフォルダーが見つかりません: {projectDirectory}");
        return 1;
    }

    var format = options.GetValueOrDefault("--format", "epub");
    if (format != "epub")
    {
        Console.Error.WriteLine($"エラー: 現時点では --format epub のみ対応しています（指定値: {format}）。");
        return 1;
    }

    var outputPath = options.GetValueOrDefault("--output")
        ?? Path.ChangeExtension(projectDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), ".epub");

    var project = new EfprojStore().Load(projectDirectory);
    var chapters = new DocumentBuilder().BuildChapters(project.Pages, project.Title);
    var blocksById = project.Pages.SelectMany(p => p.Blocks).ToDictionary(b => b.Id);

    var correctedCount = blocksById.Values.Count(b => b.IsManuallyEdited);
    if (correctedCount > 0)
    {
        Console.WriteLine($"{correctedCount} 件の校正済みブロックを反映します。");
    }

    new EpubPackageBuilder().Build(project, chapters, blocksById, outputPath);

    Console.WriteLine($"EPUBを生成しました: {outputPath}");
    return 0;
}

static int RunInfo(string[] args)
{
    var (inputPath, _) = ParseOptions(args);
    if (inputPath is null || !RequireExistingFile(inputPath))
    {
        return 1;
    }

    var fileSize = new FileInfo(inputPath).Length;
    var info = new PdfDocumentService().GetInfo(inputPath);
    var textPageCount = info.Pages.Count(p => p.HasText);

    Console.WriteLine($"ファイル: {inputPath}");
    Console.WriteLine($"ファイルサイズ: {fileSize:N0} bytes");
    Console.WriteLine($"PDFバージョン: {info.PdfVersion}");
    Console.WriteLine($"ページ数: {info.PageCount}");
    Console.WriteLine($"テキストレイヤー: {(info.HasTextLayer ? $"あり（{textPageCount}/{info.PageCount}ページ）" : "なし")}");

    if (info.Pages.Count > 0)
    {
        var first = info.Pages[0];
        Console.WriteLine($"1ページ目のサイズ: {first.WidthPoints} x {first.HeightPoints} pt");
    }

    return 0;
}

static async Task<(EpubFabricProject Project, List<DocumentPage> Pages)> BuildProjectFromPdf(string inputPath, string workDirectory, int dpi)
{
    // 14章の既定しきい値（OCR信頼度0.85未満は要確認）。
    const double ReviewConfidenceThreshold = 0.85;

    var pdfService = new PdfDocumentService();
    var info = pdfService.GetInfo(inputPath);

    var pagesNeedingOcr = info.Pages.Count(p => !p.HasText);
    if (pagesNeedingOcr > 0)
    {
        Console.WriteLine($"{pagesNeedingOcr}/{info.PageCount} ページにテキストレイヤーがありません。OCR（PP-OCRv6多言語モデル）で認識します。");
    }

    Directory.CreateDirectory(workDirectory);

    PageOcrService? ocrService = null;
    var ocrUnavailable = false;

    try
    {
        var pages = new List<DocumentPage>();
        var reviewRequiredCount = 0;

        for (var i = 0; i < info.PageCount; i++)
        {
            var pageNumber = i + 1;
            Console.WriteLine($"ページ {pageNumber}/{info.PageCount} を処理しています...");

            var imagePath = Path.Combine(workDirectory, $"page-original-{pageNumber:0000}.png");
            pdfService.RenderPageToPng(inputPath, pageNumber, imagePath, dpi);

            var pageInfo = info.Pages[i];
            string? text = null;
            var confidence = 1.0; // テキストレイヤーからの直接抽出は信頼度1.0として扱う。

            if (pageInfo.HasText)
            {
                text = pdfService.ExtractPageText(inputPath, pageNumber);
            }
            else if (!ocrUnavailable)
            {
                try
                {
                    ocrService ??= new PageOcrService();
                    await ocrService.InitializeAsync(new OcrModelProvisioner());

                    var ocrResult = ocrService.RecognizePage(imagePath);
                    text = ocrResult.Text;
                    confidence = ocrResult.AverageConfidence;
                }
                catch (Exception ex) when (ex is OcrModelDownloadException or InvalidOperationException)
                {
                    // 16章「OCRモデルがない」: OCRなしで処理を継続する。
                    Console.WriteLine($"警告: OCRを利用できません（{ex.Message}）。以降もテキストなしで処理を続けます。");
                    ocrUnavailable = true;
                }
            }

            var requiresReview = text is not null && confidence < ReviewConfidenceThreshold;

            var page = new DocumentPage
            {
                PageNumber = pageNumber,
                OriginalImagePath = imagePath,
                ProcessedImagePath = imagePath,
                PreviewImagePath = imagePath,
                Width = pageInfo.WidthPoints,
                Height = pageInfo.HeightPoints,
                WritingMode = WritingMode.Horizontal,
                Status = text is not null ? PageProcessingStatus.OcrCompleted : PageProcessingStatus.Error,
            };

            if (!string.IsNullOrWhiteSpace(text))
            {
                if (requiresReview)
                {
                    reviewRequiredCount++;
                }

                page.Blocks.Add(new PageBlock
                {
                    Id = $"p{pageNumber:0000}-b0001",
                    PageNumber = pageNumber,
                    Bounds = new BoundingBox(0, 0, 1, 1),
                    Type = BlockType.Body,
                    OcrText = text,
                    OcrConfidence = confidence,
                    ReadingOrder = 0,
                    RequiresReview = requiresReview,
                });
            }

            pages.Add(page);
        }

        if (reviewRequiredCount > 0)
        {
            Console.WriteLine($"{reviewRequiredCount} ページがOCR信頼度{ReviewConfidenceThreshold:0.00}未満のため要確認です。");
        }

        var project = new EpubFabricProject
        {
            Id = Guid.NewGuid(),
            Title = Path.GetFileNameWithoutExtension(inputPath),
            SourcePdfPath = inputPath,
            Pages = pages,
        };

        return (project, pages);
    }
    finally
    {
        ocrService?.Dispose();
    }
}

static (string? PositionalArg, Dictionary<string, string> Options) ParseOptions(string[] args)
{
    string? positional = null;
    var options = new Dictionary<string, string>();

    for (var i = 1; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            options[args[i]] = i + 1 < args.Length ? args[++i] : string.Empty;
        }
        else
        {
            positional ??= args[i];
        }
    }

    return (positional, options);
}

static int ParseDpi(Dictionary<string, string> options) =>
    options.TryGetValue("--dpi", out var value) ? int.Parse(value) : 300;

static bool RequireExistingFile(string path)
{
    if (File.Exists(path))
    {
        return true;
    }

    Console.Error.WriteLine($"エラー: ファイルが見つかりません: {path}");
    return false;
}

static void PrintUsage()
{
    Console.WriteLine("使い方:");
    Console.WriteLine("  epubfabric info <input.pdf>");
    Console.WriteLine("  epubfabric convert <input.pdf> [--output <output.epub>] [--dpi <dpi>]");
    Console.WriteLine("  epubfabric analyze <input.pdf> --project <book.efproj> [--dpi <dpi>]");
    Console.WriteLine("  epubfabric export <book.efproj> --format epub [--output <output.epub>]");
}
