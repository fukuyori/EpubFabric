using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace EpubFabric_App;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private bool _closeConfirmed;
    private bool _closeConfirmationOpen;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        ResizeToPreferredSize();

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
        AppWindow.Closing += OnAppWindowClosing;
    }

    /// <summary>
    /// 変換中の誤操作で処理を失わないよう、終了前に確認する。
    /// 一度終了が確認された後のClosingイベントは、そのまま通す。
    /// </summary>
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || RootFrame.Content is not MainPage { IsConverting: true } mainPage)
        {
            return;
        }

        // Closingイベントはダイアログの応答を待てないため、いったん終了を取り消す。
        args.Cancel = true;
        if (_closeConfirmationOpen)
        {
            return;
        }

        _closeConfirmationOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootFrame.XamlRoot,
                Title = "変換を中止して終了しますか？",
                Content = "PDF の変換中です。終了すると現在の変換は中止されます。",
                PrimaryButtonText = "終了する",
                CloseButtonText = "変換を続ける",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                mainPage.CancelConversion();
                _closeConfirmed = true;
                Close();
            }
        }
        finally
        {
            _closeConfirmationOpen = false;
        }
    }

    /// <summary>変換画面のオプション行が折り返さずに収まる幅。</summary>
    private const int PreferredWidth = 1180;

    private const int PreferredHeight = 820;

    /// <summary>
    /// 既定のウィンドウサイズは画面いっぱいに近く広すぎるため、内容に見合う大きさで
    /// 画面中央に開く。AppWindowは物理ピクセル単位なので、拡大率を掛けて指定する。
    /// 小さな画面でははみ出さないよう作業領域に収める。
    /// </summary>
    private void ResizeToPreferredSize()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(handle) / 96.0;

        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(PreferredWidth * scale), work.Width);
        var height = Math.Min((int)(PreferredHeight * scale), work.Height);

        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2,
            width,
            height));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
