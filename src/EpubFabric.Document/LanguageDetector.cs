namespace EpubFabric.Document;

/// <summary>
/// 認識済みテキストの文字種構成から出版物の言語（BCP 47コード）を判定する。
/// かなが含まれれば日本語、ハングルが支配的なら韓国語、かな無しで漢字が支配的なら中国語、
/// それ以外はラテン文字主体として英語とみなす。EPUBのdc:language / xml:langに使う
/// 粗い判定であり、リーダーのフォント選択・ハイフネーションに十分な精度でよい。
/// </summary>
public static class LanguageDetector
{
    /// <summary>判定に必要な最小文字数。これ未満は既定（日本語）のままにする。</summary>
    private const int MinSampleSize = 50;

    /// <summary>かながこの割合以上なら日本語（日本語は漢字・ラテン文字と混在するため低め）。</summary>
    private const double KanaRatioThreshold = 0.05;

    /// <summary>ハングル・漢字の支配率のしきい値。</summary>
    private const double DominantRatioThreshold = 0.3;

    public static string Detect(IEnumerable<string> texts, string fallback = "ja")
    {
        long kana = 0;
        long hangul = 0;
        long ideograph = 0;
        long latin = 0;

        foreach (var text in texts)
        {
            foreach (var c in text)
            {
                if (c is >= 'ぁ' and <= 'ヿ')
                {
                    kana++; // ひらがな・カタカナ（U+3041〜U+30FF）
                }
                else if (c is (>= '가' and <= '힣') or (>= 'ᄀ' and <= 'ᇿ'))
                {
                    hangul++;
                }
                else if (c is (>= '一' and <= '鿿') or (>= '㐀' and <= '䶿') or (>= '豈' and <= '﫿'))
                {
                    ideograph++;
                }
                else if (char.IsAsciiLetter(c))
                {
                    latin++;
                }
            }
        }

        var total = kana + hangul + ideograph + latin;
        if (total < MinSampleSize)
        {
            return fallback;
        }

        if ((double)kana / total >= KanaRatioThreshold)
        {
            return "ja";
        }

        if ((double)hangul / total >= DominantRatioThreshold)
        {
            return "ko";
        }

        if ((double)ideograph / total >= DominantRatioThreshold)
        {
            return "zh";
        }

        return "en";
    }
}
