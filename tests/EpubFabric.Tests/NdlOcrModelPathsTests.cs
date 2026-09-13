using EpubFabric.Pipeline;

namespace EpubFabric.Tests;

public sealed class NdlOcrModelPathsTests
{
    [Fact]
    public void Resolve_AcceptsOfficialRepositoryLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), $"epubfabric-ndlocr-{Guid.NewGuid():N}");
        var modelDirectory = Path.Combine(root, "src", "model");
        var configDirectory = Path.Combine(root, "src", "config");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(configDirectory);
        try
        {
            Touch(modelDirectory, "deim-s-1024x1024.onnx");
            Touch(modelDirectory, "parseq-ndl-24x256-30-tiny-189epoch-tegaki3-r8data-202604.onnx");
            Touch(modelDirectory, "parseq-ndl-24x384-50-tiny-300epoch-tegaki3-r8data-202604.onnx");
            Touch(modelDirectory, "parseq-ndl-24x768-100-tiny-153epoch-tegaki3-r8data-202604.onnx");
            Touch(configDirectory, "NDLmoji.yaml");

            var paths = NdlOcrModelPaths.Resolve(root);

            Assert.Equal(Path.Combine(modelDirectory, "deim-s-1024x1024.onnx"), paths.DetectorPath);
            Assert.Equal(Path.Combine(configDirectory, "NDLmoji.yaml"), paths.CharsetPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ReportsMissingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"epubfabric-ndlocr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var exception = Assert.Throws<DirectoryNotFoundException>(() => NdlOcrModelPaths.Resolve(root));
            Assert.Contains("deim-s-1024x1024.onnx", exception.Message);
            Assert.Contains("NDLmoji.yaml", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NativeService_NormalizesMissingModelFailureForPipelineFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"epubfabric-ndlocr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var service = new NativeNdlOcrService(new NativeNdlOcrPipelineOptions(root));

            var exception = await Assert.ThrowsAsync<ExternalBackendException>(() =>
                service.RecognizePageAsync("unused.png", root));

            Assert.Contains("内蔵ONNX版NDLOCR-Liteを利用できません", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Touch(string directory, string fileName) =>
        File.WriteAllBytes(Path.Combine(directory, fileName), []);
}
