using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EpubFabric.Pipeline;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace EpubFabric_App;

/// <summary>
/// PDF→EPUB変換画面。変換処理はEpubFabric.Pipeline（CLIと共有）に委譲し、
/// この画面は入出力の選択・オプション・進捗表示だけを担当する。
/// </summary>
public sealed partial class MainPage : Page
{
    private readonly ObservableCollection<string> _logLines = [];
    private CancellationTokenSource? _cancellation;
    private string? _lastOutputPath;

    public MainPage()
    {
        InitializeComponent();
        LogList.ItemsSource = _logLines;
    }

    private async void OnPickInputClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add(".pdf");
        InitializeWithMainWindow(picker);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        InputPathBox.Text = file.Path;
        if (string.IsNullOrWhiteSpace(OutputPathBox.Text))
        {
            OutputPathBox.Text = Path.ChangeExtension(file.Path, ".epub");
        }

        ConvertButton.IsEnabled = true;
        StatusText.Text = "変換を開始できます。";
    }

    private async void OnPickOutputClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = string.IsNullOrWhiteSpace(InputPathBox.Text)
                ? "book"
                : Path.GetFileNameWithoutExtension(InputPathBox.Text),
        };
        picker.FileTypeChoices.Add("EPUB", [".epub"]);
        InitializeWithMainWindow(picker);

        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            OutputPathBox.Text = file.Path;
        }
    }

    private void OnOllamaCheckChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        OllamaModelBox.IsEnabled = OllamaCheck.IsChecked == true;

    private async void OnConvertClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var inputPath = InputPathBox.Text;
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            StatusText.Text = "入力 PDF が見つかりません。";
            return;
        }

        var outputPath = string.IsNullOrWhiteSpace(OutputPathBox.Text)
            ? Path.ChangeExtension(inputPath, ".epub")
            : OutputPathBox.Text;
        var layout = LayoutCombo.SelectedIndex == 1 ? OutputLayout.Reflow : OutputLayout.Fixed;
        var options = new ConversionOptions
        {
            InputPath = inputPath,
            Dpi = double.IsNaN(DpiBox.Value) ? 300 : (int)DpiBox.Value,
            PreserveAllTextLines = layout == OutputLayout.Fixed,
            EnhancePages = EnhanceCheck.IsChecked == true,
            WritingMode = WritingModeCombo.SelectedIndex switch
            {
                1 => WritingModeSetting.Horizontal,
                2 => WritingModeSetting.Vertical,
                _ => WritingModeSetting.Auto,
            },
            Ollama = OllamaCheck.IsChecked == true
                ? new OllamaPipelineOptions("http://localhost:11434", OllamaModelBox.Text.Trim())
                : null,
        };

        _cancellation = new CancellationTokenSource();
        SetRunningState(true);
        _logLines.Clear();
        ConvertProgressBar.Value = 0;
        ConvertProgressBar.IsIndeterminate = true;
        StatusText.Text = "変換しています...";

        // Progress<T>はUIスレッドで生成するとコールバックがUIスレッドへ戻るため、
        // ログ・進捗バーの更新をそのまま行える。
        var progress = new Progress<ConversionProgress>(p =>
        {
            AppendLog(p.Message);
            if (p.PageCount > 0 && p.PageNumber > 0)
            {
                ConvertProgressBar.IsIndeterminate = false;
                ConvertProgressBar.Value = 100.0 * p.PageNumber / p.PageCount;
            }
        });

        try
        {
            var token = _cancellation.Token;
            await Task.Run(
                async () =>
                {
                    var pipeline = new ConversionPipeline();
                    var (project, _) = await pipeline.BuildProjectAsync(options, progress, token);
                    token.ThrowIfCancellationRequested();
                    pipeline.BuildEpub(project, layout, outputPath);
                },
                token);

            _lastOutputPath = outputPath;
            OpenFolderButton.IsEnabled = true;
            ConvertProgressBar.Value = 100;
            StatusText.Text = $"完了: {outputPath}";
            AppendLog($"EPUBを生成しました: {outputPath}");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "キャンセルしました。";
            AppendLog("変換をキャンセルしました。");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"エラー: {ex.Message}";
            AppendLog($"エラー: {ex.Message}");
        }
        finally
        {
            ConvertProgressBar.IsIndeterminate = false;
            SetRunningState(false);
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    private void OnCancelClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        CancelButton.IsEnabled = false;
    }

    private void OnOpenFolderClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_lastOutputPath is not null && File.Exists(_lastOutputPath))
        {
            Process.Start("explorer.exe", $"/select,\"{_lastOutputPath}\"");
        }
    }

    private void SetRunningState(bool running)
    {
        ConvertButton.IsEnabled = !running && !string.IsNullOrWhiteSpace(InputPathBox.Text);
        CancelButton.IsEnabled = running;
        PickInputButton.IsEnabled = !running;
        PickOutputButton.IsEnabled = !running;
        LayoutCombo.IsEnabled = !running;
        WritingModeCombo.IsEnabled = !running;
        DpiBox.IsEnabled = !running;
        EnhanceCheck.IsEnabled = !running;
        OllamaCheck.IsEnabled = !running;
        OllamaModelBox.IsEnabled = !running && OllamaCheck.IsChecked == true;
    }

    private void AppendLog(string message)
    {
        _logLines.Add(message);
        if (_logLines.Count > 0)
        {
            LogList.ScrollIntoView(_logLines[^1]);
        }
    }

    private static void InitializeWithMainWindow(object picker)
    {
        // アンパッケージ実行ではピッカーに親ウィンドウのHWNDを渡す必要がある。
        var window = App.MainAppWindow ?? throw new InvalidOperationException("メインウィンドウが初期化されていません。");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }
}
