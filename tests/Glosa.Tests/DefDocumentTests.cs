using Glosa.Core.Definition;
using Glosa.Core.Text;

namespace Glosa.Tests;

public class DefDocumentTests
{
    [Fact]
    public void KeepsEntryOrder()
    {
        DefDocument def = DefDocument.ParseText("""
            [keyword]
            ND-1050=ND-1050
            ND-10EX=ND-10EX
            ND-10=ND-10
            """);

        string[] keys = [.. def["keyword"]!.Entries.Select(e => e.Key)];

        // Order is priority: the longer model names have to be tried first.
        Assert.Equal(["ND-1050", "ND-10EX", "ND-10"], keys);
    }

    [Fact]
    public void KeepsDuplicateKeysAndReturnsTheFirst()
    {
        DefDocument def = DefDocument.ParseText("""
            [patch]
            *:12:3=0:0:3
            *:12:3=9:9:9
            """);

        Assert.Equal(2, def["patch"]!.Entries.Count);
        Assert.Equal("0:0:3", def["patch"]!.Get("*:12:3"));
    }

    [Fact]
    public void LooksUpSectionsAndKeysWithoutRegardToCase()
    {
        DefDocument def = DefDocument.ParseText("""
            [ConvIndex]
            SC-88Pro:GS=Something
            """);

        Assert.NotNull(def["convindex"]);
        Assert.Equal("Something", def["CONVINDEX"]!.Get("sc-88pro:gs"));
    }

    [Fact]
    public void TreatsSemicolonAsCommentOnlyAtStartOfLine()
    {
        DefDocument def = DefDocument.ParseText("""
            [s]
            ; this whole line is a comment
            a=1
            b=value;with;semicolons
            """);

        DefSection s = def["s"]!;
        Assert.Equal(2, s.Entries.Count);
        Assert.Equal("value;with;semicolons", s.Get("b"));
    }

    [Fact]
    public void TrimsWhitespaceAroundKeysAndValues()
    {
        DefDocument def = DefDocument.ParseText("[s]\n  spaced   =   value  \n");
        Assert.Equal("value", def["s"]!.Get("spaced"));
    }

    [Fact]
    public void ReadsShiftJisContent()
    {
        Cp932.Register();
        byte[] bytes = Cp932.Encoding.GetBytes("[Reset]\ncomment=リセットを送る（全角ＡＢＣ）\n");

        Assert.Equal("リセットを送る（全角ＡＢＣ）", DefDocument.Parse(bytes)["Reset"]!.Get("comment"));
    }

    [Fact]
    public void ReturnsNullForMissingSectionsAndKeys()
    {
        DefDocument def = DefDocument.ParseText("[a]\nx=1\n");
        Assert.Null(def["nope"]);
        Assert.Null(def["a"]!.Get("nope"));
        Assert.Equal(7, def["a"]!.GetInt("nope", 7));
    }

    [Fact]
    public void ReadsAPresentButUnreadableValueAsZero()
    {
        // The fallback only covers an absent key. A key that is there goes through atoi, so
        // "GSToGMEmu=" turns the flag off rather than leaving it as it was.
        DefDocument def = DefDocument.ParseText("""
            [a]
            blank=
            word=off
            trailing=12ab
            """);
        DefSection a = def["a"]!;

        Assert.Equal(0, a.GetInt("blank", 5));
        Assert.Equal(0, a.GetInt("word", 5));
        Assert.Equal(12, a.GetInt("trailing", 5));
        Assert.False(a.GetBool("word", true));
    }
}
