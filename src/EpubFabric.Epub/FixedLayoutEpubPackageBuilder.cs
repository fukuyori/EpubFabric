using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using EpubFabric.Core.Models;

namespace EpubFabric.Epub;

/// <summary>
/// PDFの1ページをEPUBの1ページとして収録する固定レイアウトEPUB 3パッケージ生成器。
/// ページ画像が表示を保証し、ブロック文字列は透明テキスト層として付加される。
/// </summary>
public sealed class FixedLayoutEpubPackageBuilder
{
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace EpubOps = "http://www.idpf.org/2007/ops";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly FixedLayoutXhtmlGenerator _xhtmlGenerator = new();

    public void Build(EpubFabricProject project, string outputEpubPath)
    {
        var pages = project.Pages.OrderBy(p => p.PageNumber).ToList();
        if (pages.Count == 0)
        {
            throw new InvalidOperationException("固定レイアウトEPUBには1ページ以上必要です。");
        }

        var directory = Path.GetDirectoryName(outputEpubPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Delete(outputEpubPath);

        var pageResources = pages
            .Select((page, index) => new PageResource(
                page,
                XhtmlFileName(index),
                ImageFileName(index, page.OriginalImagePath)))
            .ToList();

        using var zip = ZipFile.Open(outputEpubPath, ZipArchiveMode.Create);

        var mimetypeEntry = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var writer = new StreamWriter(mimetypeEntry.Open(), Utf8NoBom))
        {
            writer.Write("application/epub+zip");
        }

        WriteXml(zip, "META-INF/container.xml", BuildContainerXml());
        WriteXml(zip, "EPUB/package.opf", BuildPackageOpf(project, pageResources));
        WriteXml(zip, "EPUB/nav.xhtml", BuildNavXhtml(project.Title, pageResources));
        WriteText(zip, "EPUB/styles/fixed-layout.css", FixedLayoutStylesheet.Content);

        foreach (var resource in pageResources)
        {
            WriteXml(
                zip,
                $"EPUB/text/{resource.XhtmlFileName}",
                _xhtmlGenerator.GeneratePage(resource.Page, resource.ImageFileName, project.Language));

            zip.CreateEntryFromFile(
                resource.Page.OriginalImagePath,
                $"EPUB/images/{resource.ImageFileName}",
                CompressionLevel.Optimal);
        }
    }

    private static string XhtmlFileName(int index) => $"page-{index + 1:0000}.xhtml";

    private static string ImageFileName(int index, string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".webp")
        {
            throw new NotSupportedException($"固定レイアウトEPUBへ収録できないページ画像形式です: {extension}");
        }

        return $"page-{index + 1:0000}{extension}";
    }

    private static string ImageMediaType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "image/png",
    };

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

    private static XDocument BuildPackageOpf(EpubFabricProject project, IReadOnlyList<PageResource> pages)
    {
        var manifestItems = new List<XElement>
        {
            new(Opf + "item", new XAttribute("id", "nav"), new XAttribute("href", "nav.xhtml"), new XAttribute("media-type", "application/xhtml+xml"), new XAttribute("properties", "nav")),
            new(Opf + "item", new XAttribute("id", "css"), new XAttribute("href", "styles/fixed-layout.css"), new XAttribute("media-type", "text/css")),
        };
        var spineItems = new List<XElement>();

        for (var i = 0; i < pages.Count; i++)
        {
            var pageId = $"page-{i + 1:0000}";
            var imageId = $"page-image-{i + 1:0000}";

            manifestItems.Add(new XElement(
                Opf + "item",
                new XAttribute("id", pageId),
                new XAttribute("href", $"text/{pages[i].XhtmlFileName}"),
                new XAttribute("media-type", "application/xhtml+xml")));
            manifestItems.Add(new XElement(
                Opf + "item",
                new XAttribute("id", imageId),
                new XAttribute("href", $"images/{pages[i].ImageFileName}"),
                new XAttribute("media-type", ImageMediaType(pages[i].ImageFileName))));
            spineItems.Add(new XElement(Opf + "itemref", new XAttribute("idref", pageId)));
        }

        var metadata = new XElement(
            Opf + "metadata",
            new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XElement(Dc + "identifier", new XAttribute("id", "pub-id"), $"urn:uuid:{project.Id}"),
            new XElement(Dc + "title", XmlTextSanitizer.Sanitize(project.Title)),
            new XElement(Dc + "language", project.Language),
            new XElement(Opf + "meta", new XAttribute("property", "rendition:layout"), "pre-paginated"),
            new XElement(Opf + "meta", new XAttribute("property", "rendition:orientation"), "auto"),
            new XElement(Opf + "meta", new XAttribute("property", "rendition:spread"), "auto"));

        if (!string.IsNullOrWhiteSpace(project.Author))
        {
            metadata.Add(new XElement(Dc + "creator", XmlTextSanitizer.Sanitize(project.Author)));
        }

        if (!string.IsNullOrWhiteSpace(project.Publisher))
        {
            metadata.Add(new XElement(Dc + "publisher", XmlTextSanitizer.Sanitize(project.Publisher)));
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
                new XElement(
                    Opf + "spine",
                    new XAttribute("page-progression-direction", "ltr"),
                    spineItems)));
    }

    private static XDocument BuildNavXhtml(string title, IReadOnlyList<PageResource> pages)
    {
        var safeTitle = XmlTextSanitizer.Sanitize(title);
        var pageLinks = pages.Select((page, index) => new XElement(
            Xhtml + "li",
            new XElement(
                Xhtml + "a",
                new XAttribute("href", $"text/{page.XhtmlFileName}"),
                $"Page {page.Page.PageNumber}"))).ToList();

        var html = new XElement(
            Xhtml + "html",
            new XAttribute(XNamespace.Xmlns + "epub", EpubOps),
            new XElement(Xhtml + "head", new XElement(Xhtml + "title", safeTitle)),
            new XElement(
                Xhtml + "body",
                new XElement(
                    Xhtml + "nav",
                    new XAttribute(EpubOps + "type", "toc"),
                    new XAttribute("id", "toc"),
                    new XElement(Xhtml + "h1", safeTitle),
                    new XElement(
                        Xhtml + "ol",
                        new XElement(
                            Xhtml + "li",
                            new XElement(
                                Xhtml + "a",
                                new XAttribute("href", $"text/{pages[0].XhtmlFileName}"),
                                safeTitle)))),
                new XElement(
                    Xhtml + "nav",
                    new XAttribute(EpubOps + "type", "page-list"),
                    new XAttribute("id", "page-list"),
                    new XElement(Xhtml + "h2", "Pages"),
                    new XElement(Xhtml + "ol", pageLinks))));

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

    private sealed record PageResource(
        DocumentPage Page,
        string XhtmlFileName,
        string ImageFileName);
}
