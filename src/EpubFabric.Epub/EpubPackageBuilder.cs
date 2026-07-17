using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using EpubFabric.Core.Models;
using EpubFabric.Document;

namespace EpubFabric.Epub;

/// <summary>
/// 12章 EPUB出力：中間文書モデルからリフロー型EPUB 3パッケージを生成する。
/// </summary>
public sealed class EpubPackageBuilder
{
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace EpubOps = "http://www.idpf.org/2007/ops";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly EpubXhtmlGenerator _xhtmlGenerator = new();

    public void Build(
        EpubFabricProject project,
        IReadOnlyList<DocumentChapter> chapters,
        IReadOnlyDictionary<string, PageBlock> blocksById,
        string outputEpubPath)
    {
        var directory = Path.GetDirectoryName(outputEpubPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Delete(outputEpubPath);

        using var zip = ZipFile.Open(outputEpubPath, ZipArchiveMode.Create);

        // mimetypeは無圧縮でZIP先頭に配置する（12.7）。
        var mimetypeEntry = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var writer = new StreamWriter(mimetypeEntry.Open(), Utf8NoBom))
        {
            writer.Write("application/epub+zip");
        }

        WriteXml(zip, "META-INF/container.xml", BuildContainerXml());
        WriteXml(zip, "EPUB/package.opf", BuildPackageOpf(project, chapters));
        WriteXml(zip, "EPUB/nav.xhtml", BuildNavXhtml(project.Title, chapters));
        WriteText(zip, "EPUB/styles/book.css", EpubStylesheet.Content);

        for (var i = 0; i < chapters.Count; i++)
        {
            var chapterXhtml = _xhtmlGenerator.GenerateChapter(chapters[i], blocksById);
            WriteXml(zip, $"EPUB/text/{ChapterFileName(i)}", chapterXhtml);
        }
    }

    private static string ChapterFileName(int index) => $"chapter-{index + 1:000}.xhtml";

    private static XDocument BuildContainerXml()
    {
        XNamespace ns = "urn:oasis:names:tc:opendocument:xmlns:container";

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(
                ns + "container",
                new XAttribute("version", "1.0"),
                new XElement(
                    ns + "rootfiles",
                    new XElement(
                        ns + "rootfile",
                        new XAttribute("full-path", "EPUB/package.opf"),
                        new XAttribute("media-type", "application/oebps-package+xml")))));
    }

    private static XDocument BuildPackageOpf(EpubFabricProject project, IReadOnlyList<DocumentChapter> chapters)
    {
        var manifestItems = new List<XElement>
        {
            new(Opf + "item", new XAttribute("id", "nav"), new XAttribute("href", "nav.xhtml"), new XAttribute("media-type", "application/xhtml+xml"), new XAttribute("properties", "nav")),
            new(Opf + "item", new XAttribute("id", "css"), new XAttribute("href", "styles/book.css"), new XAttribute("media-type", "text/css")),
        };

        var spineItems = new List<XElement>();

        for (var i = 0; i < chapters.Count; i++)
        {
            var id = $"chapter-{i + 1:000}";
            manifestItems.Add(new XElement(
                Opf + "item",
                new XAttribute("id", id),
                new XAttribute("href", $"text/{ChapterFileName(i)}"),
                new XAttribute("media-type", "application/xhtml+xml")));
            spineItems.Add(new XElement(Opf + "itemref", new XAttribute("idref", id)));
        }

        var metadata = new XElement(
            Opf + "metadata",
            new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XElement(Dc + "identifier", new XAttribute("id", "pub-id"), $"urn:uuid:{project.Id}"),
            new XElement(Dc + "title", project.Title),
            new XElement(Dc + "language", project.Language));

        if (!string.IsNullOrWhiteSpace(project.Author))
        {
            metadata.Add(new XElement(Dc + "creator", project.Author));
        }

        if (!string.IsNullOrWhiteSpace(project.Publisher))
        {
            metadata.Add(new XElement(Dc + "publisher", project.Publisher));
        }

        metadata.Add(new XElement(
            Opf + "meta",
            new XAttribute("property", "dcterms:modified"),
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")));

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(
                Opf + "package",
                new XAttribute("version", "3.0"),
                new XAttribute("unique-identifier", "pub-id"),
                new XAttribute(XNamespace.Xml + "lang", project.Language),
                metadata,
                new XElement(Opf + "manifest", manifestItems),
                new XElement(Opf + "spine", spineItems)));
    }

    private static XDocument BuildNavXhtml(string title, IReadOnlyList<DocumentChapter> chapters)
    {
        var listItems = chapters.Select((chapter, i) => new XElement(
            Xhtml + "li",
            new XElement(Xhtml + "a", new XAttribute("href", $"text/{ChapterFileName(i)}"), chapter.Title)));

        var nav = new XElement(
            Xhtml + "nav",
            new XAttribute(EpubOps + "type", "toc"),
            new XAttribute("id", "toc"),
            new XElement(Xhtml + "h1", title),
            new XElement(Xhtml + "ol", listItems));

        var html = new XElement(
            Xhtml + "html",
            new XAttribute(XNamespace.Xmlns + "epub", EpubOps),
            new XElement(Xhtml + "head", new XElement(Xhtml + "title", title)),
            new XElement(Xhtml + "body", nav));

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), html);
    }

    private static void WriteXml(ZipArchive zip, string entryName, XDocument document)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Utf8NoBom);
        document.Save(writer);
    }

    private static void WriteText(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), Utf8NoBom);
        writer.Write(content);
    }
}
