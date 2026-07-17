namespace EpubFabric.Core.Models;

public sealed class EpubFabricSettings
{
    public PdfSettings Pdf { get; init; } = new();

    public OcrSettings Ocr { get; init; } = new();

    public OllamaSettings Ollama { get; init; } = new();

    public ReviewThresholds ReviewThresholds { get; init; } = new();

    public PersistenceSettings Persistence { get; init; } = new();
}

public sealed class PdfSettings
{
    public int Dpi { get; set; } = 300;

    public bool SplitFacingPages { get; set; }

    public bool CopySourcePdfIntoProject { get; set; } = true;
}

public sealed class OcrSettings
{
    public string ModelName { get; set; } = string.Empty;

    public string Language { get; set; } = "ja";

    public bool UseGpu { get; set; }
}

public sealed class OllamaSettings
{
    public bool Enabled { get; set; } = true;

    public string Endpoint { get; set; } = "http://localhost:11434";

    public string ModelName { get; set; } = string.Empty;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    public int MaxRetryCount { get; set; } = 3;

    public int PagesPerRequest { get; set; } = 1;

    public bool LowConfidencePagesOnly { get; set; }

    public int MaxImageResolution { get; set; } = 1600;

    public bool UseGpu { get; set; }
}

/// <summary>
/// 校正対象の自動判定しきい値（14章）。既定値は利用者が変更できる。
/// </summary>
public sealed class ReviewThresholds
{
    public double OcrConfidence { get; set; } = 0.85;

    public double LayoutConfidence { get; set; } = 0.80;

    public double OllamaClassificationConfidence { get; set; } = 0.80;
}

public sealed class PersistenceSettings
{
    public TimeSpan AutoSaveInterval { get; set; } = TimeSpan.FromSeconds(3);

    public long CacheSizeLimitBytes { get; set; } = 5L * 1024 * 1024 * 1024;
}
