using EpubFabric.Pipeline;

namespace EpubFabric.Tests;

public sealed class NdlOcrModelPathsTests
{
    private const string DetectorFileName = "deim-s-1024x1024-261124.onnx";

    [Fact]
    public void Resolve_AcceptsOfficialRepositoryLayout()
    {
        var root = NewTempDirectory();
        var modelDirectory = Path.Combine(root, "src", "model");
        var configDirectory = Path.Combine(root, "src", "config");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(configDirectory);
        try
        {
            foreach (var fileName in NdlOcrModelPaths.RequiredFileNames)
            {
                Touch(fileName.EndsWith(".yaml", StringComparison.Ordinal) ? configDirectory : modelDirectory, fileName);
            }

            var paths = NdlOcrModelPaths.Resolve(root);

            Assert.Equal(Path.Combine(modelDirectory, DetectorFileName), paths.DetectorPath);
            Assert.Equal(Path.Combine(configDirectory, "NDLmoji.yaml"), paths.CharsetPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_AcceptsBundledFlatLayout()
    {
        var root = NewTempDirectory();
        try
        {
            foreach (var fileName in NdlOcrModelPaths.RequiredFileNames)
            {
                Touch(root, fileName);
            }

            var paths = NdlOcrModelPaths.Resolve(root);

            Assert.Equal(Path.Combine(root, DetectorFileName), paths.DetectorPath);
            Assert.Equal(Path.Combine(root, "NDLmoji.yaml"), paths.CharsetPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ReportsMissingFiles()
    {
        var root = NewTempDirectory();
        try
        {
            var exception = Assert.Throws<DirectoryNotFoundException>(() => NdlOcrModelPaths.Resolve(root));
            Assert.Contains(DetectorFileName, exception.Message);
            Assert.Contains("NDLmoji.yaml", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindBundledDirectory_FindsModelsNextToExecutable()
    {
        var root = NewTempDirectory();
        var models = Path.Combine(root, "models", "ndlocr");
        Directory.CreateDirectory(models);
        try
        {
            Touch(models, DetectorFileName);

            Assert.Equal(Path.GetFullPath(models), NdlOcrModelPaths.FindBundledDirectory(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindBundledDirectory_FindsModelsInParentForCliSubdirectory()
    {
        // インストーラーはCLIを {app}\cli、モデルを {app}\models\ndlocr へ置く。
        var root = NewTempDirectory();
        var cli = Path.Combine(root, "cli");
        var models = Path.Combine(root, "models", "ndlocr");
        Directory.CreateDirectory(cli);
        Directory.CreateDirectory(models);
        try
        {
            Touch(models, DetectorFileName);

            Assert.Equal(Path.GetFullPath(models), NdlOcrModelPaths.FindBundledDirectory(cli));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindBundledDirectory_ReturnsNullWhenAbsent()
    {
        var root = NewTempDirectory();
        try
        {
            Assert.Null(NdlOcrModelPaths.FindBundledDirectory(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NativeService_NormalizesMissingModelFailureForPipelineFallback()
    {
        var root = NewTempDirectory();
        try
        {
            using var service = new NativeNdlOcrService(new NativeNdlOcrPipelineOptions(root));

            var exception = Assert.Throws<ExternalBackendException>(service.Initialize);

            Assert.Contains("内蔵ONNX版NDLOCR-Liteを利用できません", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"epubfabric-ndlocr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Touch(string directory, string fileName) =>
        File.WriteAllBytes(Path.Combine(directory, fileName), []);
}
