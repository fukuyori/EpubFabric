namespace EpubFabric.Ollama;

/// <summary>
/// 16章「Ollamaに接続できない」「Ollama応答不正」に対応する例外。
/// </summary>
public sealed class OllamaClassificationException : Exception
{
    public OllamaClassificationException(string message)
        : base(message)
    {
    }

    public OllamaClassificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
