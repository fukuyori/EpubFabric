using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EpubFabric.Core.Models;
using EpubFabric.Pipeline;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace EpubFabric_App;

/// <summary>
/// PDF→EPUB変換画面。変換処理はEpubFabric.Pipeline（CLIと共有）に委譲し、
/// この画面は入出力の選択・オプション・進捗表示だけを担当する。
/// </summary>
public sealed partial class MainPage : Page
{
    private readonly ObservableCollection<string> _logLines = [];
    private readonly ObservableCollection<ConversionItem> _files = [];
    private CancellationTokenSource? _cancellation;
    private string? _lastOutputPath;
    private EpubFabricProject? _lastProject;
    private OutputLayout _lastLayout;
    private bool _lastCoverPageAsImage;

    public MainPage()
    {
        InitializeComponent();
        LogList.ItemsSource = _logLines;
        FileList.ItemsSource = _files;
        VersionText.Text = $"v{AppVersion()}";

        // 起動引数でPDFが渡されていれば選択済みの状態で開始する。
        if (App.StartupPdfPath is { } startupPdf)
        {
            AddFiles([startupPdf]);
        }
    }

    /// <summary>
    /// 表示用のバージョン。ビルド時に埋め込まれる情報バージョンは
    /// 「0.2.3+&lt;コミットハッシュ&gt;」の形になるため、ハッシュ部分は落とす。
    /// </summary>
    private static string AppVersion()
    {
        var informational = typeof(MainPage).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus < 0 ? informational : informational[..plus];
        }

        var version = typeof(MainPage).Assembly.GetName().Version;
        return version is null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private void OnDragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        // 変換中はドロップを受け付けない。
        if (_cancellation is not null || !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "PDF を入力として開く";
    }

    private async void OnDrop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (_cancellation is not null || !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var pdfs = items.OfType<Windows.Storage.StorageFile>()
            .Where(f => f.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Path)
            .ToList();
        if (pdfs.Count == 0)
        {
            StatusText.Text = "PDF ファイルをドロップしてください。";
            return;
        }

        // 1件ずつ落として順番に積み上げられるよう、ドロップは常に追加として扱う。
        AddFiles(pdfs);
    }

    /// <summary>一覧へ追加する。同じファイルは重ねない。並びは追加した順を保つ。</summary>
    private void AddFiles(IReadOnlyList<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (_files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _files.Add(new ConversionItem(path));
            added++;
        }

        UpdateFileListState();
        StatusText.Text = added == 0
            ? "すでに一覧にあるファイルです。"
            : $"{_files.Count} 件を変換できます。";
    }

    private void UpdateFileListState()
    {
        FileCountText.Text = $"変換する PDF（{_files.Count} 件）";
        var idle = _cancellation is null;
        ConvertButton.IsEnabled = idle && _files.Count > 0;
        ClearInputButton.IsEnabled = idle && _files.Count > 0;
        RemoveInputButton.IsEnabled = idle && FileList.SelectedItems.Count > 0;
    }

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFileListState();

    private void OnRemoveInputClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        foreach (var item in FileList.SelectedItems.Cast<ConversionItem>().ToList())
        {
            _files.Remove(item);
        }

        UpdateFileListState();
    }

    private void OnClearInputClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _files.Clear();
        UpdateFileListState();
        StatusText.Text = "PDF を追加すると変換を開始できます。";
    }

    private async void OnPickInputClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add(".pdf");
        InitializeWithMainWindow(picker);

        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
        {
            // まとめて選んだ場合、ピッカーが返す順は不定なので名前順で積む。
            AddFiles(files.Select(f => f.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList());
        }
    }

    private async void OnPickOutputClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithMainWindow(picker);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            OutputPathBox.Text = folder.Path;
        }
    }

    private void OnOllamaCheckChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        OllamaModelBox.IsEnabled = OllamaCheck.IsChecked == true;

    private async void OnConvertClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var inputs = _files.ToList();
        if (inputs.Count == 0)
        {
            StatusText.Text = "変換する PDF を追加してください。";
            return;
        }

        foreach (var item in inputs)
        {
            item.Status = "待機中";
            item.OutputPath = null;
        }

        var layout = LayoutCombo.SelectedIndex == 1 ? OutputLayout.Reflow : OutputLayout.Fixed;
        var dpi = double.IsNaN(DpiBox.Value) ? 300 : (int)DpiBox.Value;
        var maxPages = !double.IsNaN(MaxPagesBox.Value) && MaxPagesBox.Value > 0 ? (int)MaxPagesBox.Value : (int?)null;
        var writingMode = WritingModeCombo.SelectedIndex switch
        {
            1 => WritingModeSetting.Horizontal,
            2 => WritingModeSetting.Vertical,
            _ => WritingModeSetting.Auto,
        };
        var ollama = OllamaCheck.IsChecked == true
            ? new OllamaPipelineOptions("http://localhost:11434", OllamaModelBox.Text.Trim())
            : null;

        var outputSetting = OutputPathBox.Text;

        ConversionOptions OptionsFor(string inputPath) => new()
        {
            InputPath = inputPath,
            Dpi = dpi,
            PreserveAllTextLines = layout == OutputLayout.Fixed,
            EnhancePages = EnhanceCheck.IsChecked == true,
            ForceOcr = ForceOcrCheck.IsChecked == true,
            MaxPages = maxPages,
            WritingMode = writingMode,
            Ollama = ollama,
        };

        // 出力先: 指定フォルダー（未指定なら各PDFと同じ場所）に「入力名.epub」で出す。
        string OutputFor(string inputPath) =>
            string.IsNullOrWhiteSpace(outputSetting)
                ? Path.ChangeExtension(inputPath, ".epub")
                : Path.Combine(outputSetting, Path.GetFileNameWithoutExtension(inputPath) + ".epub");

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

        var coverPageAsImage = CoverImageCheck.IsChecked == true;
        var succeeded = 0;
        var failed = 0;

        try
        {
            var token = _cancellation.Token;

            for (var i = 0; i < inputs.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var item = inputs[i];
                var inputPath = item.Path;
                var outputPath = OutputFor(inputPath);

                if (!File.Exists(inputPath))
                {
                    item.Status = "見つかりません";
                    failed++;
                    AppendLog($"エラー: ファイルが見つかりません: {inputPath}");
                    continue;
                }

                item.Status = "変換中";
                FileList.ScrollIntoView(item);
                AppendLog($"［{i + 1}/{inputs.Count}］{item.Name}");
                StatusText.Text = inputs.Count == 1
                    ? "変換しています..."
                    : $"変換しています（{i + 1}/{inputs.Count}）: {item.Name}";

                ConvertProgressBar.IsIndeterminate = true;

                try
                {
                    var options = OptionsFor(inputPath);
                    var convertedProject = await Task.Run(
                        async () =>
                        {
                            var pipeline = new ConversionPipeline();
                            var (project, _) = await pipeline.BuildProjectAsync(options, progress, token);
                            token.ThrowIfCancellationRequested();
                            pipeline.BuildEpub(project, layout, outputPath, coverPageAsImage: coverPageAsImage);
                            return project;
                        },
                        token);

                    // 校正画面と「出力フォルダを開く」は、最後に成功した1件を対象にする。
                    _lastOutputPath = outputPath;
                    _lastProject = convertedProject;
                    _lastLayout = layout;
                    _lastCoverPageAsImage = coverPageAsImage;
                    OpenFolderButton.IsEnabled = true;
                    EditorButton.IsEnabled = true;
                    succeeded++;
                    item.Status = "完了";
                    item.OutputPath = outputPath;
                    AppendLog($"EPUBを生成しました: {outputPath}");
                }
                catch (OperationCanceledException)
                {
                    item.Status = "中止";
                    throw;
                }
                catch (Exception ex)
                {
                    // 1件の失敗で残りを止めない（まとめて処理する意味がなくなるため）。
                    failed++;
                    item.Status = "失敗";
                    AppendLog($"エラー: {item.Name}: {ex.Message}");
                }
            }

            ConvertProgressBar.Value = 100;
            StatusText.Text = inputs.Count == 1
                ? failed == 0 ? $"完了: {_lastOutputPath}" : "エラーで終了しました。"
                : $"完了: {succeeded} 件成功{(failed > 0 ? $" / {failed} 件失敗" : string.Empty)}";
        }
        catch (OperationCanceledException)
        {
            foreach (var remaining in inputs.Where(f => f.Status is "待機中" or "変換中"))
            {
                remaining.Status = "中止";
            }

            StatusText.Text = inputs.Count == 1
                ? "キャンセルしました。"
                : $"キャンセルしました（{succeeded} 件完了）。";
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

    private void OnOpenEditorClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_lastProject is not null)
        {
            Frame.Navigate(typeof(EditorPage), new EditorNavigationArgs(_lastProject, _lastLayout, _lastCoverPageAsImage));
        }
    }

    private void SetRunningState(bool running)
    {
        CancelButton.IsEnabled = running;
        PickInputButton.IsEnabled = !running;
        PickOutputButton.IsEnabled = !running;
        UpdateFileListState();
        LayoutCombo.IsEnabled = !running;
        WritingModeCombo.IsEnabled = !running;
        DpiBox.IsEnabled = !running;
        MaxPagesBox.IsEnabled = !running;
        EnhanceCheck.IsEnabled = !running;
        ForceOcrCheck.IsEnabled = !running;
        CoverImageCheck.IsEnabled = !running;
        OllamaCheck.IsEnabled = !running;
        OllamaModelBox.IsEnabled = !running && OllamaCheck.IsChecked == true;
    }

    /// <summary>複数ファイルの連続変換ではログが際限なく伸びるため、古い行から捨てる。</summary>
    private const int MaxLogLines = 5000;

    private ScrollViewer? _logScrollViewer;
    private bool _scrollPending;

    private void AppendLog(string message)
    {
        _logLines.Add(message);

        while (_logLines.Count > MaxLogLines)
        {
            _logLines.RemoveAt(0);
        }

        ScrollLogToEnd();
    }

    /// <summary>
    /// 常に最新の行が見えるようにログを末尾までスクロールする。
    /// ListView.ScrollIntoView は行を追加した直後だと表示用のコンテナがまだ作られておらず
    /// 効かないため、内側のScrollViewerを直接動かす。さらに、追加した行の高さが
    /// レイアウトに反映される前にスクロール量を読むと最終行が見切れるので、
    /// UpdateLayoutで確定させてから移動する。
    /// ログは1ページにつき数行届くため、保留中のスクロールは1つにまとめる。
    /// </summary>
    private void ScrollLogToEnd()
    {
        if (_scrollPending)
        {
            return;
        }

        _scrollPending = true;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _scrollPending = false;
            _logScrollViewer ??= FindScrollViewer(LogList);

            if (_logScrollViewer is null)
            {
                if (_logLines.Count > 0)
                {
                    LogList.ScrollIntoView(_logLines[^1]);
                }

                return;
            }

            LogList.UpdateLayout();
            _logScrollViewer.ChangeView(null, _logScrollViewer.ScrollableHeight, null, disableAnimation: true);
        });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            if (FindScrollViewer(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void InitializeWithMainWindow(object picker)
    {
        // アンパッケージ実行ではピッカーに親ウィンドウのHWNDを渡す必要がある。
        var window = App.MainAppWindow ?? throw new InvalidOperationException("メインウィンドウが初期化されていません。");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }
}
