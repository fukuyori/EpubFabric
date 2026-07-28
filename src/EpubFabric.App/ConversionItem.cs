using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace EpubFabric_App;

/// <summary>変換待ち行列の1件。一覧に表示し、処理の進み具合に応じて状態を書き換える。</summary>
public sealed class ConversionItem : INotifyPropertyChanged
{
    private string _status = "待機中";
    private string? _outputPath;

    public ConversionItem(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>一覧の副題。同名ファイルを見分けられるようフォルダーを出す。</summary>
    public string Location => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            Notify();
        }
    }

    /// <summary>変換に成功した場合の出力先。失敗・未処理ならnull。</summary>
    public string? OutputPath
    {
        get => _outputPath;
        set
        {
            _outputPath = value;
            Notify();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
