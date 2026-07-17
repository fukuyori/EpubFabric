using EpubFabric.Ocr;

namespace EpubFabric.Tests;

public class OcrModelProvisionerTests
{
    // 向き分類モデルはEmbeddedResourceとして同梱されているため、ネットワークなしで検証できる。
    // det/rec/dict（PP-OCRv6多言語モデル）のダウンロードはネットワークが必要なため対象外。
    [Fact]
    public void EnsureClassifierModel_ExtractsEmbeddedResourceAndVerifiesHash()
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), $"epubfabric-ocr-cache-{Guid.NewGuid():N}");
        var provisioner = new OcrModelProvisioner(cacheDirectory: cacheDirectory);

        try
        {
            var path = provisioner.EnsureClassifierModel();

            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 0);

            // 2回目の呼び出しは既存ファイルのハッシュが一致するため再展開されない。
            var pathAgain = provisioner.EnsureClassifierModel();
            Assert.Equal(path, pathAgain);
        }
        finally
        {
            Directory.Delete(cacheDirectory, recursive: true);
        }
    }
}
