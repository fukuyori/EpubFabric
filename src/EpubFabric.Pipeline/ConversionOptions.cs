namespace EpubFabric.Pipeline;

/// <summary>出力レイアウト。固定レイアウト（ページ画像+透明テキスト層）が既定。</summary>
public enum OutputLayout
{
    Fixed,
    Reflow,
}

/// <summary>書字方向の指定。Autoは行の形状からページ単位に自動判定する。</summary>
public enum WritingModeSetting
{
    Auto,
    Horizontal,
    Vertical,
}

/// <summary>Ollama連携の設定。nullなら意味分類・OCR校正を行わない。</summary>
public sealed record OllamaPipelineOptions(string Endpoint, string Model);

/// <summary>固定レイアウトEPUBへ収録するページ画像の再圧縮設定。</summary>
public sealed record PageImageEncodingOptions(int JpegQuality = 85, int MaxSideLength = 2200);

/// <summary>PDF→プロジェクト構築の設定一式。CLIとGUIの両方から使う。</summary>
public sealed record ConversionOptions
{
    public required string InputPath { get; init; }

    /// <summary>中間ファイル（ページ画像等）の出力先。nullなら一時ディレクトリを作る。</summary>
    public string? WorkDirectory { get; init; }

    public int Dpi { get; init; } = 300;

    /// <summary>trueで全テキスト行を座標のまま保持（固定レイアウト用）。falseでレイアウト解析+段落統合（リフロー用）。</summary>
    public bool PreserveAllTextLines { get; init; } = true;

    /// <summary>ページ画像の高品質化（紙色正規化・裏写り抑制）。</summary>
    public bool EnhancePages { get; init; }

    /// <summary>PDFのテキスト層を使わず、全ページをOCRで再認識する。
    /// 古いスキャンOCR由来の低精度なテキスト層を持つPDF向け（OCRmyPDFの--force-ocr相当）。</summary>
    public bool ForceOcr { get; init; }

    /// <summary>出版物の言語（BCP 47コード）。nullなら認識テキストの文字種から自動判定する。</summary>
    public string? Language { get; init; }

    /// <summary>変換するページ数の上限（先頭からこのページまで）。nullで全ページ。
    /// 長編の試し変換・設定調整用。</summary>
    public int? MaxPages { get; init; }

    /// <summary>書字方向。既定のAutoでは、行の縦横比からページ単位に自動判定し、
    /// 綴じ方向（page-progression-direction）は縦書きページの多数決で決める。</summary>
    public WritingModeSetting WritingMode { get; init; } = WritingModeSetting.Auto;

    public OllamaPipelineOptions? Ollama { get; init; }
}

/// <summary>変換の進捗通知。PageNumber=0は前後処理などページに紐づかないメッセージ。</summary>
public sealed record ConversionProgress(int PageNumber, int PageCount, string Message);
