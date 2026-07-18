using EpubFabric.Document;

namespace EpubFabric.Tests;

public sealed class LanguageDetectorTests
{
    [Fact]
    public void 日本語テキストはjaと判定する()
    {
        var texts = new[]
        {
            "本連載では、シャープの電子辞書で動作するディストリビューションを紹介します。",
            "今回はハードウエアを中心に explore し、Linux カーネルの制御を説明します。",
        };

        Assert.Equal("ja", LanguageDetector.Detect(texts));
    }

    [Fact]
    public void 英語テキストはenと判定する()
    {
        var texts = new[]
        {
            "When the text interpreter cannot find the word in the dictionary, it tries NUMBER.",
            "In FORTH, a word is a character or group of characters that have a definition.",
        };

        Assert.Equal("en", LanguageDetector.Detect(texts));
    }

    [Fact]
    public void かなを含まない漢字主体のテキストはzhと判定する()
    {
        var texts = new[]
        {
            "本书介绍操作系统的基本概念与实现方法，包括进程管理、内存管理和文件系统。",
            "第二章讨论并发编程模型以及同步原语的设计。",
        };

        Assert.Equal("zh", LanguageDetector.Detect(texts));
    }

    [Fact]
    public void ハングル主体のテキストはkoと判定する()
    {
        var texts = new[]
        {
            "이 책은 운영체제의 기본 개념과 구현 방법을 소개합니다.",
            "２장에서는 동시성 프로그래밍 모델을 다룹니다.",
            "프로세스 관리와 메모리 관리, 파일 시스템의 구조를 차례로 설명합니다.",
        };

        Assert.Equal("ko", LanguageDetector.Detect(texts));
    }

    [Fact]
    public void テキストが少なすぎる場合は既定値を返す()
    {
        Assert.Equal("ja", LanguageDetector.Detect(["abc"]));
        Assert.Equal("en", LanguageDetector.Detect([], fallback: "en"));
    }
}
