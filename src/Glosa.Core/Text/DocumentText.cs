using System.Text;

namespace Glosa.Core.Text;

/// <summary>
/// Reads the attached document, the text file beside a song, in whatever encoding it is in.
/// </summary>
/// <remarks>
/// A byte order mark says so outright. Without one, the bytes are UTF-8 if they read as
/// UTF-8 all the way through, and Shift-JIS otherwise. Shift-JIS text that is also valid
/// UTF-8 is rare: its double-byte characters break the lead-and-continuation pattern UTF-8
/// needs almost at once. Text that is ASCII throughout reads the same either way.
///
/// Shift-JIS is the fallback because it is what the documents of the day were written in;
/// UTF-8 is what one written or converted since is likely to be.
/// </remarks>
public static class DocumentText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false,
                                                          throwOnInvalidBytes: true);

    private static ReadOnlySpan<byte> Utf8Mark => [0xEF, 0xBB, 0xBF];

    private static ReadOnlySpan<byte> Utf16LittleEndianMark => [0xFF, 0xFE];

    private static ReadOnlySpan<byte> Utf16BigEndianMark => [0xFE, 0xFF];

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Utf8Mark)) return Encoding.UTF8.GetString(bytes[Utf8Mark.Length..]);
        if (bytes.StartsWith(Utf16LittleEndianMark))
            return Encoding.Unicode.GetString(bytes[Utf16LittleEndianMark.Length..]);
        if (bytes.StartsWith(Utf16BigEndianMark))
            return Encoding.BigEndianUnicode.GetString(bytes[Utf16BigEndianMark.Length..]);

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Cp932.Decode(bytes);
        }
    }
}
