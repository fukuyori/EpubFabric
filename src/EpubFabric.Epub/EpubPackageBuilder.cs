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
    private const string CoverXhtmlFileName = "cover.xhtml";

    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace EpubOps = "http://www.idpf.org/2007/ops";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly EpubXhtmlGenerator _xhtmlGenerator = new();
    private readonly PageImageTranscoder? _coverTranscoder;

    /// <param name="coverImage">
    /// 1ページ目をテキスト化せず表紙画像として収録する場合の再圧縮設定。nullで表紙を作らない。
    /// 表紙はOCRしても意味のある本文にならず（装飾文字・大見出しの誤読が本文へ混入する）、
    /// 紙面をそのまま見せる方が実用的なため、画像として収録できるようにしている。
    /// </param>
    public EpubPackageBuilder(PageImageTranscoder? coverImage = null)
    {
        _coverTranscoder = coverImage;
    }

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

        var images = blocksById.Values
            .Where(b => b.Type == BlockType.Figure && b.ExtractedImagePath is not null)
            .Select(b => b.ExtractedImagePath!)
            .Distinct()
            .ToList();

        var cover = PrepareCover(project);

        using var zip = ZipFile.Open(outputEpubPath, ZipArchiveMode.Create);

        // mimetypeは無圧縮でZIP先頭に配置する（12.7）。
        var mimetypeEntry = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var writer = new StreamWriter(mimetypeEntry.Open(), Utf8NoBom))
        {
            writer.Write("application/epub+zip");
        }

        WriteXml(zip, "META-INF/container.xml", BuildContainerXml());
        WriteXml(zip, "EPUB/package.opf", BuildPackageOpf(project, chapters, images, cover));
        WriteXml(zip, "EPUB/nav.xhtml", BuildNavXhtml(project.Title, chapters, blocksById, cover));
        WriteText(zip, "EPUB/styles/book.css", EpubStylesheet.Content);

        if (cover is not null)
        {
            WriteXml(zip, $"EPUB/text/{CoverXhtmlFileName}", BuildCoverXhtml(project, cover.FileName));

            if (cover.Bytes is not null)
            {
                var entry = zip.CreateEntry($"EPUB/images/{cover.FileName}", CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(cover.Bytes);
            }
            else
            {
                zip.CreateEntryFromFile(cover.SourcePath, $"EPUB/images/{cover.FileName}", CompressionLevel.Optimal);
            }
        }

        for (var i = 0; i < chapters.Count; i++)
        {
            var chapterXhtml = _xhtmlGenerator.GenerateChapter(chapters[i], blocksById);
            WriteXml(zip, $"EPUB/text/{ChapterFileName(i)}", chapterXhtml);
        }

        foreach (var imagePath in images)
        {
            zip.CreateEntryFromFile(imagePath, $"EPUB/images/{Path.GetFileName(imagePath)}", CompressionLevel.Optimal);
        }
    }

    /// <summary>1ページ目のページ画像を表紙として整える。無効時・画像が無い場合はnull。</summary>
    private PreparedPageImage? PrepareCover(EpubFabricProject project)
    {
        if (_coverTranscoder is null)
        {
            return null;
        }

        var firstPage = project.Pages.OrderBy(p => p.PageNumber).FirstOrDefault();
        if (firstPage is null)
        {
            return null;
        }

        var sourcePath = PageImageTranscoder.DisplayImagePathOf(firstPage);
        return string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)
            ? null
            : _coverTranscoder.Prepare(sourcePath, "cover");
    }

    /// <summary>
    /// 表紙ページのXHTML。リーダーごとの余白・拡大縮小の差を避けるため、画像は
    /// ビューポートに収まる範囲で最大化する（縦横比は保つ）。
    /// </summary>
    private static XDocument BuildCoverXhtml(EpubFabricProject project, string imageFileName)
    {
        var title = XmlTextSanitizer.Sanitize(project.Title);

        var html = new XElement(
            Xhtml + "html",
            new XAttribute(XNamespace.Xmlns + "epub", EpubOps),
            new XAttribute(XNamespace.Xml + "lang", project.Language),
            new XElement(
                Xhtml + "head",
                new XElement(Xhtml + "title", title),
                new XElement(Xhtml + "link", new XAttribute("rel", "stylesheet"), new XAttribute("type", "text/css"), new XAttribute("href", "../styles/book.css"))),
            new XElement(
                Xhtml + "body",
                new XAttribute("class", "cover"),
                new XElement(
                    Xhtml + "section",
                    new XAttribute(EpubOps + "type", "cover"),
                    new XElement(
                        Xhtml + "img",
                        new XAttribute("src", $"../images/{imageFileName}"),
                        new XAttribute("alt", title)))));

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), html);
    }

    private static string ChapterFileName(int index) => $"chapter-{index + 1:000}.xhtml";

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

    private static XDocument BuildPackageOpf(
        EpubFabricProject project,
        IReadOnlyList<DocumentChapter> chapters,
        IReadOnlyList<string> images,
        PreparedPageImage? cover)
    {
        var manifestItems = new List<XElement>
        {
            new(Opf + "item", new XAttribute("id", "nav"), new XAttribute("href", "nav.xhtml"), new XAttribute("media-type", "application/xhtml+xml"), new XAttribute("properties", "nav")),
            new(Opf + "item", new XAttribute("id", "css"), new XAttribute("href", "styles/book.css"), new XAttribute("media-type", "text/css")),
        };

        if (cover is not null)
        {
            manifestItems.Add(new XElement(
                Opf + "item",
                new XAttribute("id", "cover-image"),
                new XAttribute("href", $"images/{cover.FileName}"),
                new XAttribute("media-type", PageImageTranscoder.MediaTypeOf(cover.FileName)),
                new XAttribute("properties", "cover-image")));
            manifestItems.Add(new XElement(
                Opf + "item",
                new XAttribute("id", "cover"),
                new XAttribute("href", $"text/{CoverXhtmlFileName}"),
                new XAttribute("media-type", "application/xhtml+xml")));
        }

        for (var i = 0; i < images.Count; i++)
        {
            var fileName = Path.GetFileName(images[i]);
            manifestItems.Add(new XElement(
                Opf + "item",
                new XAttribute("id", $"image-{i + 1:000}"),
                new XAttribute("href", $"images/{fileName}"),
                new XAttribute("media-type", ImageMediaType(fileName))));
        }

        var spineItems = new List<XElement>();

        if (cover is not null)
        {
            spineItems.Add(new XElement(Opf + "itemref", new XAttribute("idref", "cover")));
        }

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
            new XElement(Dc + "title", XmlTextSanitizer.Sanitize(project.Title)),
            new XElement(Dc + "language", project.Language));

        if (!string.IsNullOrWhiteSpace(project.Author))
        {
            metadata.Add(new XElement(Dc + "creator", XmlTextSanitizer.Sanitize(project.Author)));
        }

        if (!string.IsNullOrWhiteSpace(project.Publisher))
        {
            metadata.Add(new XElement(Dc + "publisher", XmlTextSanitizer.Sanitize(project.Publisher)));
        }

        if (cover is not null)
        {
            // EPUB 2世代のリーダー・書棚アプリ向けの表紙互換表記。
            metadata.Add(new XElement(Opf + "meta", new XAttribute("name", "cover"), new XAttribute("content", "cover-image")));
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

    /// <summary>
    /// 本文の見出し処理と同じデータから目次を作る。章（h1）を最上位に、章内の
    /// 節見出し（h2）をアンカー付きで入れ子にする。小見出し（h3）は数が多く
    /// 目次が破綻しやすいため含めない。
    /// </summary>
    private static XDocument BuildNavXhtml(
        string title,
        IReadOnlyList<DocumentChapter> chapters,
        IReadOnlyDictionary<string, PageBlock> blocksById,
        PreparedPageImage? cover)
    {
        var safeTitle = XmlTextSanitizer.Sanitize(title);
        var listItems = chapters.Select((chapter, i) =>
        {
            var item = new XElement(
                Xhtml + "li",
                new XElement(Xhtml + "a", new XAttribute("href", $"text/{ChapterFileName(i)}"), XmlTextSanitizer.Sanitize(chapter.Title)));

            var sectionItems = chapter.BlockIds
                .Select(id => blocksById.GetValueOrDefault(id))
                .Where(b => b is { Type: BlockType.SectionHeading })
                .Select(b => XmlTextSanitizer.Sanitize(b!.CorrectedText ?? b.OcrText) is { Length: > 0 } text
                    ? new XElement(
                        Xhtml + "li",
                        new XElement(Xhtml + "a", new XAttribute("href", $"text/{ChapterFileName(i)}#{b.Id}"), text))
                    : null)
                .Where(li => li is not null)
                .ToList();

            if (sectionItems.Count > 0)
            {
                item.Add(new XElement(Xhtml + "ol", sectionItems));
            }

            return item;
        });

        // 表紙を目次の先頭に置き、本文から表紙へ戻れるようにする。
        var coverItem = cover is null
            ? []
            : new[]
            {
                new XElement(
                    Xhtml + "li",
                    new XElement(Xhtml + "a", new XAttribute("href", $"text/{CoverXhtmlFileName}"), "表紙")),
            };

        var nav = new XElement(
            Xhtml + "nav",
            new XAttribute(EpubOps + "type", "toc"),
            new XAttribute("id", "toc"),
            new XElement(Xhtml + "h1", safeTitle),
            new XElement(Xhtml + "ol", coverItem.Concat(listItems)));

        var html = new XElement(
            Xhtml + "html",
            new XAttribute(XNamespace.Xmlns + "epub", EpubOps),
            new XElement(Xhtml + "head", new XElement(Xhtml + "title", safeTitle)),
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
