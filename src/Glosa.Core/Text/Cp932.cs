using System.Text;

namespace Glosa.Core.Text;

/// <summary>
/// Shift-JIS support. Code page 932 is outside .NET's built-in set, so the code pages
/// provider has to be registered before any DEF file, SMF text event or document is read.
/// </summary>
public static class Cp932
{
    private static Encoding? _encoding;

    /// <summary>Shift-JIS (code page 932).</summary>
    public static Encoding Encoding
    {
        get
        {
            if (_encoding is not null) return _encoding;
            Register();
            return _encoding ??= System.Text.Encoding.GetEncoding(932);
        }
    }

    private static bool _registered;

    /// <summary>Registers the code pages provider. Safe to call more than once.</summary>
    public static void Register()
    {
        if (_registered) return;
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _registered = true;
    }

    /// <summary>Decodes SMF text bytes, which are Shift-JIS in practice for Japanese data.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
        => bytes.IsEmpty ? string.Empty : Encoding.GetString(bytes).TrimEnd('\0');
}
