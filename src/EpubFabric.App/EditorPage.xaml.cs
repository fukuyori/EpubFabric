using System;
using System.IO;
using System.Linq;
using EpubFabric.Core.Models;
using EpubFabric.Pipeline;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;
using Windows.UI;

namespace EpubFabric_App;

/// <summary>校正画面へ渡す変換結果一式。</summary>
public sealed record EditorNavigationArgs(EpubFabricProject Project, OutputLayout Layout);

/// <summary>
/// 11.1 メイン画面の3ペイン校正ビュー: 左=ページ一覧、中央=ページ画像+ブロック枠、
/// 右=選択ブロックの情報と編集。編集はメモリ上のPageBlockへ直接反映し、
/// 「EPUBを書き出す」で修正込みのEPUBを生成する。
/// </summary>
public sealed partial class EditorPage : Page
{
    private static readonly BlockType[] EditableBlockTypes = Enum.GetValues<BlockType>();

    private EpubFabricProject? _project;
    private OutputLayout _layout;
    private DocumentPage? _currentPage;
    private PageBlock? _selectedBlock;
    private Rectangle? _selectedRectangle;
    private bool _updatingPanel;

    public EditorPage()
    {
        InitializeComponent();
        BlockTypeCombo.ItemsSource = EditableBlockTypes.Select(BlockTypeLabel).ToList();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not EditorNavigationArgs args)
        {
            return;
        }

        _project = args.Project;
        _layout = args.Layout;
        TitleText.Text = _project.Title;

        PageList.ItemsSource = _project.Pages
            .OrderBy(p => p.PageNumber)
            .Select(p => $"ページ {p.PageNumber}（{p.Blocks.Count}ブロック）")
            .ToList();

        if (_project.Pages.Count > 0)
        {
            PageList.SelectedIndex = 0;
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private void OnPageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_project is null || PageList.SelectedIndex < 0)
        {
            return;
        }

        _currentPage = _project.Pages.OrderBy(p => p.PageNumber).ElementAt(PageList.SelectedIndex);
        _selectedBlock = null;
        _selectedRectangle = null;
        BlockPanel.Visibility = Visibility.Collapsed;
        LoadPage(_currentPage);
    }

    private void LoadPage(DocumentPage page)
    {
        // キャンバスはPDFポイント座標系。Viewboxがウィンドウに合わせて等比スケールする。
        var canvasWidth = Math.Max(1, page.Width);
        var canvasHeight = Math.Max(1, page.Height);
        PageCanvasHost.Width = canvasWidth;
        PageCanvasHost.Height = canvasHeight;

        var imagePath = File.Exists(page.ProcessedImagePath) ? page.ProcessedImagePath : page.OriginalImagePath;
        PageImage.Source = File.Exists(imagePath) ? new BitmapImage(new Uri(imagePath)) : null;

        OverlayCanvas.Children.Clear();
        foreach (var block in page.Blocks.OrderBy(b => b.ReadingOrder))
        {
            var rect = new Rectangle
            {
                Width = Math.Max(1, block.Bounds.Width * canvasWidth),
                Height = Math.Max(1, block.Bounds.Height * canvasHeight),
                Stroke = new SolidColorBrush(ColorFor(block)),
                StrokeThickness = 1.2,
                Fill = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
                Tag = block,
            };
            Canvas.SetLeft(rect, block.Bounds.X * canvasWidth);
            Canvas.SetTop(rect, block.Bounds.Y * canvasHeight);
            rect.Tapped += OnBlockRectangleTapped;
            OverlayCanvas.Children.Add(rect);
        }
    }

    private void OnBlockRectangleTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not Rectangle rect || rect.Tag is not PageBlock block)
        {
            return;
        }

        if (_selectedRectangle is not null)
        {
            _selectedRectangle.StrokeThickness = 1.2;
        }

        _selectedRectangle = rect;
        rect.StrokeThickness = 3.5;
        _selectedBlock = block;
        ShowBlock(block);
        e.Handled = true;
    }

    private void ShowBlock(PageBlock block)
    {
        _updatingPanel = true;
        try
        {
            BlockPanel.Visibility = Visibility.Visible;
            BlockIdText.Text = block.Id;
            BlockMetaText.Text =
                $"読み順: {block.ReadingOrder} / OCR信頼度: {block.OcrConfidence:0.00} / 取得元: {block.TextSource}"
                + (block.RequiresReview ? " / 要確認" : string.Empty)
                + (block.IsManuallyEdited ? " / 手動修正済み" : string.Empty);

            BlockTypeCombo.SelectedIndex = Array.IndexOf(EditableBlockTypes, block.Type);
            HeadingLevelBox.Value = block.HeadingLevel ?? 0;
            ExcludedCheck.IsChecked = block.IsExcluded;
            BlockTextBox.Text = block.CorrectedText ?? block.OcrText;
        }
        finally
        {
            _updatingPanel = false;
        }
    }

    private void OnBlockTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPanel || _selectedBlock is null || BlockTypeCombo.SelectedIndex < 0)
        {
            return;
        }

        _selectedBlock.Type = EditableBlockTypes[BlockTypeCombo.SelectedIndex];
        _selectedBlock.IsManuallyEdited = true;
        if (_selectedRectangle is not null)
        {
            _selectedRectangle.Stroke = new SolidColorBrush(ColorFor(_selectedBlock));
        }
    }

    private void OnHeadingLevelChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingPanel || _selectedBlock is null || double.IsNaN(args.NewValue))
        {
            return;
        }

        var level = (int)args.NewValue;
        _selectedBlock.HeadingLevel = level == 0 ? null : level;
        _selectedBlock.IsManuallyEdited = true;
    }

    private void OnExcludedChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingPanel || _selectedBlock is null)
        {
            return;
        }

        _selectedBlock.IsExcluded = ExcludedCheck.IsChecked == true;
        _selectedBlock.IsManuallyEdited = true;
    }

    private void OnApplyTextClick(object sender, RoutedEventArgs e)
    {
        if (_selectedBlock is null)
        {
            return;
        }

        var text = BlockTextBox.Text;
        // OCR結果そのままなら修正扱いにしない（9.9: OCR結果と手動修正を別保持）。
        _selectedBlock.CorrectedText = text == _selectedBlock.OcrText ? null : text;
        _selectedBlock.IsManuallyEdited = _selectedBlock.CorrectedText is not null;
        _selectedBlock.RequiresReview = false;
        ShowBlock(_selectedBlock);
        StatusText.Text = "テキスト修正を適用しました。";
    }

    private void OnRevertTextClick(object sender, RoutedEventArgs e)
    {
        if (_selectedBlock is null)
        {
            return;
        }

        _selectedBlock.CorrectedText = null;
        ShowBlock(_selectedBlock);
        StatusText.Text = "OCR結果に戻しました。";
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_project is null)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = _project.Title,
        };
        picker.FileTypeChoices.Add("EPUB", [".epub"]);
        var window = App.MainAppWindow!;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            ExportButton.IsEnabled = false;
            StatusText.Text = "EPUBを書き出しています...";
            var project = _project;
            var layout = _layout;
            await System.Threading.Tasks.Task.Run(() => new ConversionPipeline().BuildEpub(project, layout, file.Path));
            StatusText.Text = $"書き出しました: {file.Path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"エラー: {ex.Message}";
        }
        finally
        {
            ExportButton.IsEnabled = true;
        }
    }

    private static Color ColorFor(PageBlock block)
    {
        var hex = BlockTypeColors.HexFor(block.Type);
        return Color.FromArgb(
            255,
            Convert.ToByte(hex.Substring(1, 2), 16),
            Convert.ToByte(hex.Substring(3, 2), 16),
            Convert.ToByte(hex.Substring(5, 2), 16));
    }

    private static string BlockTypeLabel(BlockType type) => type switch
    {
        BlockType.ChapterTitle => "章タイトル",
        BlockType.SectionHeading => "節見出し",
        BlockType.Subheading => "小見出し",
        BlockType.Body => "本文",
        BlockType.Figure => "図",
        BlockType.Caption => "キャプション",
        BlockType.Aside => "囲み記事",
        BlockType.PullQuote => "引用・強調",
        BlockType.Table => "表",
        BlockType.Footnote => "脚注",
        BlockType.Code => "コード",
        BlockType.Header => "柱",
        BlockType.Footer => "フッター",
        BlockType.PageNumber => "ノンブル",
        BlockType.Decorative => "装飾",
        _ => "不明",
    };
}
