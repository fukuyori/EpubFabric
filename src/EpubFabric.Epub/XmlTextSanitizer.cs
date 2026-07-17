using System.Text;
using System.Xml;

namespace EpubFabric.Epub;

/// <summary>
/// OCR由来のテキストにはXML 1.0で許可されない文字（U+FFFE、制御文字、不対サロゲート等）が
/// 混入することがあるため、XHTML/OPFへ書き出す前に不正文字を除去する。
/// </summary>
internal static class XmlTextSanitizer
{
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var firstInvalid = FindFirstInvalid(text);
        if (firstInvalid < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        builder.Append(text, 0, firstInvalid);

        for (var i = firstInvalid; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], c))
                {
                    builder.Append(c).Append(text[i + 1]);
                    i++;
                }
                continue;
            }

            if (XmlConvert.IsXmlChar(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static int FindFirstInvalid(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], c))
                {
                    i++;
                    continue;
                }

                return i;
            }

            if (!XmlConvert.IsXmlChar(c))
            {
                return i;
            }
        }

        return -1;
    }
}
