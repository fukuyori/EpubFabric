# EpubFabric

PDF（スキャン原稿・テキスト層付きの両方）を、検索・選択・読み上げ可能な EPUB 3 に変換する Windows 向けツールです。

ページ画像で紙面の原型を保証しつつ、OCR・レイアウト解析・ローカル LLM（Ollama）による機械処理と人間の校正を組み合わせて文字情報を付加する、固定レイアウト EPUB 制作環境として設計しています（詳細は [docs/基本設計.md](docs/基本設計.md)）。

## 特徴

- **固定レイアウト EPUB**（既定）: PDF の 1 ページを EPUB の 1 ページとして収録。ページ画像の上に、座標付きの透明テキスト層を重ねるため、見た目は原本のまま検索・選択・読み上げができる
- **リフロー型 EPUB**: レイアウト解析（見出し・段組み・図版・キャプション検出）と段落統合で章構造を持つ EPUB を生成
- **OCR**: RapidOcrNet（PP-OCRv6 多言語 ONNX モデル）によるローカル OCR。日本語対応。モデルは初回実行時に自動ダウンロード
  - 傾き補正（deskew）: OCR 専用の補正画像で認識し、座標は元画像へ逆変換（表示画像は無加工）
  - 低信頼のゴミ行フィルタ: 表紙・飾りページの誤読が本文へ混入するのを防ぐ
- **多段組み対応**: ガター（段間の空白）の再帰検出により 2〜4 段組み・不等幅の段の読み順を正しく再現
- **紙面の高品質化**（`--enhance`）: 紙色のホワイトバランス正規化（黄ばみ・くすみ除去）と裏写り・地色ムラのスムーズステップ白化。幾何変換なしのためテキスト層座標に影響しない。表紙・全面写真ページは自動でスキップ
- **Ollama 連携**（任意）: ローカル LLM でブロック種別・見出しレベルを意味的に補正し、OCR 誤認識（例: 悄報→情報）を校正。校正は等長置換のみ・URL 保護などの多層ガード付きで、LLM の書き換え事故を適用前に排除
- **サイズ最適化**: ページ画像を JPEG 品質 85・長辺 2200px（変更可）へ再圧縮して収録。テキスト層の座標には影響しない
- **評価レポート**: ページ画像+検出ブロックと EPUB 断片を左右対照する HTML レポートで変換精度をチューニング可能

## 必要環境

- Windows 10/11（x64）
- [.NET 10 SDK](https://dotnet.microsoft.com/)
- （任意）[Ollama](https://ollama.com/) — `--ollama` を使う場合。既定モデルは `gemma4:12b`

## ビルド

```powershell
git clone https://github.com/fukuyori/EpubFabric.git
cd EpubFabric
dotnet build
dotnet test
```

## 配布用実行ファイルの作成

```powershell
# 自己完結型（.NETランタイム同梱）を publish\EpubFabric.Cli\win-x64\ に出力
.\scripts\publish.ps1

# 単一EXEにまとめる場合
.\scripts\publish.ps1 -SingleFile

# テストを省略して急ぐ場合
.\scripts\publish.ps1 -SkipTests
```

出力された `epubfabric.exe` は .NET のインストールされていない Windows でもそのまま実行できます。

### インストーラー（Inno Setup）

[Inno Setup 6](https://jrsoftware.org/isinfo.php) がインストールされていれば、セットアップ EXE を作成できます:

```powershell
.\scripts\build-installer.ps1 -Version 1.0.0
# → publish\installer\EpubFabric-Setup-1.0.0.exe
```

インストーラーは日本語/英語対応で、管理者（Program Files）・ユーザー単位（%LocalAppData%\Programs）のどちらでもインストールできます。「PATH 環境変数に追加する」タスクを選ぶと、コマンドプロンプトからそのまま `epubfabric` を実行できます（アンインストール時に除去されます）。

## 使い方（CLI）

```powershell
# PDF情報の確認
dotnet run --project src\EpubFabric.Cli -- info input.pdf

# 固定レイアウトEPUB生成（既定）
dotnet run --project src\EpubFabric.Cli -- convert input.pdf --output book.epub

# リフロー型EPUB生成
dotnet run --project src\EpubFabric.Cli -- convert input.pdf --layout reflow

# スキャン紙面の高品質化（紙色正規化・裏写り抑制）を有効化
dotnet run --project src\EpubFabric.Cli -- convert input.pdf --enhance

# Ollamaによる見出し分類 + OCR校正を有効化
dotnet run --project src\EpubFabric.Cli -- convert input.pdf --ollama

# ページ画像の圧縮設定（既定: 品質85・長辺2200px、0で縮小なし）
dotnet run --project src\EpubFabric.Cli -- convert input.pdf --image-quality 90 --max-image-size 2600

# 変換精度の評価レポート（EPUBは生成しない）
dotnet run --project src\EpubFabric.Cli -- evaluate input.pdf --report report-dir
# → report-dir\index.html をブラウザで開く

# 解析結果をプロジェクトとして保存 → 手動校正 → EPUB書き出し
dotnet run --project src\EpubFabric.Cli -- analyze input.pdf --project book.efproj
dotnet run --project src\EpubFabric.Cli -- export book.efproj --format epub
```

主なオプション:

| オプション | 既定値 | 説明 |
|---|---|---|
| `--layout <fixed\|reflow>` | `fixed` | 出力レイアウト |
| `--dpi <dpi>` | `300` | ページラスタライズ解像度 |
| `--enhance` | 無効 | スキャン紙面の高品質化（紙色正規化・裏写り抑制） |
| `--image-quality <1-100>` | `85` | ページ画像のJPEG品質（固定レイアウト） |
| `--max-image-size <px>` | `2200` | ページ画像の長辺上限。`0`で縮小なし |
| `--ollama` | 無効 | Ollamaによる意味分類とOCR校正 |
| `--ollama-model <model>` | `gemma4:12b` | 使用モデル |
| `--ollama-endpoint <url>` | `http://localhost:11434` | Ollamaサーバー |

## プロジェクト構成

```
src/
  EpubFabric.Cli          コマンドライン（変換パイプラインのオーケストレーション）
  EpubFabric.App          Windows GUI（WinAppSDK）
  EpubFabric.Core         データモデル・設定
  EpubFabric.Pdf          PDF読み込み・ラスタライズ・テキスト層抽出（Docnet/PDFium）
  EpubFabric.Ocr          OCR（RapidOcrNet / PP-OCRv6）・ゴミ行フィルタ・モデル管理
  EpubFabric.Imaging      画像処理（OpenCvSharp）: 図版検出・OCR前処理（deskew）
  EpubFabric.Layout       レイアウト解析: 見出し・段組み（ColumnDetector）・段落統合
  EpubFabric.Ollama       Ollama連携: ブロック分類・OCR文字列校正
  EpubFabric.Document     文書構造化（章分割）
  EpubFabric.Epub         EPUB 3 パッケージ生成（固定レイアウト/リフロー）
  EpubFabric.Evaluation   変換精度の評価レポート生成
  EpubFabric.Persistence  プロジェクト（.efproj）の保存・読み込み
tests/
  EpubFabric.Tests        単体テスト（xUnit）
docs/
  基本設計.md              全体設計
  固定レイアウト開発方針.md  固定レイアウトの方針
```

## 変換パイプラインの概要

1. **ラスタライズ**: PDFium で各ページを PNG 化（既定 300dpi、白地合成）
2. **テキスト取得**: テキスト層があり品質基準を満たすページは PDF から文字座標を抽出。それ以外は OCR（前処理で傾き補正 → PP-OCRv6 → 信頼度・文字種によるゴミ行除去）
3. **レイアウト解析**（リフロー時）: 図版・囲み記事検出、見出し推定、段組み検出、段落統合
4. **Ollama 補正**（任意）: ブロック種別・見出しレベルの意味的補正、OCR 誤認識の校正
5. **EPUB 生成**: 固定レイアウトはページ画像+透明テキスト層、リフローは章構造の XHTML。ページ画像は再圧縮して収録

## ライセンス・謝辞

本プロジェクトは [GNU AGPLv3](LICENSE) で公開しています。

- OCR モデル: [RapidOCR](https://github.com/RapidAI/RapidOCR)（PP-OCRv6）/ [RapidOcrNet](https://github.com/BobLd/RapidOcrNet)
- PDF レンダリング: [Docnet](https://github.com/GowenGit/docnet)（PDFium）
- 画像処理: [OpenCvSharp](https://github.com/shimat/opencvsharp) / [SkiaSharp](https://github.com/mono/SkiaSharp)
- 紙面高品質化（`--enhance`）の手法は、登 大遊氏の [DN_SuperBook_PDF_Converter](https://github.com/dnobori/DN_SuperBook_PDF_Converter)（AGPLv3）の紙色統計補正のアイデアを参考に、OpenCV で独自実装したものです
