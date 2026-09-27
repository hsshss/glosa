using System.Text;
using Glosa.Core.Text;

namespace Glosa.Tests;

public class DocumentTextTests
{
    private const string Text = "SC-88Pro用のデータです";

    [Fact]
    public void ShiftJisIsTheDefault()
        => Assert.Equal(Text, DocumentText.Decode(Cp932.Encoding.GetBytes(Text)));

    [Fact]
    public void Utf8WithoutAMarkIsTakenForWhatItIs()
        => Assert.Equal(Text, DocumentText.Decode(Encoding.UTF8.GetBytes(Text)));

    [Fact]
    public void AByteOrderMarkSaysSo()
    {
        Assert.Equal(Text, DocumentText.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Text)]));
        Assert.Equal(Text, DocumentText.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Text)]));
        Assert.Equal(Text, DocumentText.Decode([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(Text)]));
    }

    [Fact]
    public void AsciiReadsTheSameEitherWay()
        => Assert.Equal("for MU2000\r\n", DocumentText.Decode("for MU2000\r\n"u8));

    [Fact]
    public void NothingIsNothing()
        => Assert.Equal("", DocumentText.Decode([]));
}
