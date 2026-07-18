namespace EpubFabric.Pipeline;

/// <summary>出力レイアウト。固定レイアウト（ページ画像+透明テキスト層）が既定。</summary>
public enum OutputLayout
{
    Fixed,
    Reflow,
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

    public OllamaPipelineOptions? Ollama { get; init; }
}

/// <summary>変換の進捗通知。PageNumber=0は前後処理などページに紐づかないメッセージ。</summary>
public sealed record ConversionProgress(int PageNumber, int PageCount, string Message);
