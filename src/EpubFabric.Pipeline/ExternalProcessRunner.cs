using System.Diagnostics;

namespace EpubFabric.Pipeline;

internal static class ExternalProcessRunner
{
    public static async Task<string> RunPythonAsync(
        string pythonExecutable,
        string scriptPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pythonExecutable))
        {
            throw new ExternalBackendException("Python実行ファイルが指定されていません。");
        }

        var fullScriptPath = Path.GetFullPath(scriptPath);
        if (!File.Exists(fullScriptPath))
        {
            throw new ExternalBackendException($"外部バックエンドのスクリプトが見つかりません: {fullScriptPath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            WorkingDirectory = Path.GetDirectoryName(fullScriptPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(fullScriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ExternalBackendException("外部バックエンドを起動できませんでした。");
            }
        }
        catch (ExternalBackendException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ExternalBackendException($"外部バックエンドを起動できません: {ex.Message}", ex);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new ExternalBackendException($"外部バックエンドが{timeout.TotalSeconds:0}秒以内に完了しませんでした。");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new ExternalBackendException(
                $"外部バックエンドが終了コード{process.ExitCode}で失敗しました: {Limit(detail)}");
        }

        return output;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 元のタイムアウトまたはキャンセルを優先する。
        }
    }

    private static string Limit(string value)
    {
        const int maxLength = 2000;
        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }
}
