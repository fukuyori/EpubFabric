namespace EpubFabric.Pipeline;

/// <summary>ローカル外部解析バックエンドの起動、実行、出力読込に失敗した。</summary>
public sealed class ExternalBackendException : Exception
{
    public ExternalBackendException(string message)
        : base(message)
    {
    }

    public ExternalBackendException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
