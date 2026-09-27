namespace Glosa.Core.Emulation;

/// <summary>
/// The modules to play a song as when detection names no model: THRU, or only a family
/// (GS, XG). Empty leaves the answer as it is.
/// </summary>
/// <remarks>
/// Only for detected answers; a module set on the song by hand is its own.
/// </remarks>
public sealed record DetectionDefaults(string Thru, string Gs, string Xg)
{
    public static readonly DetectionDefaults None = new(string.Empty, string.Empty, string.Empty);

    /// <summary>The module a detected answer is played as.</summary>
    public string Resolve(string detected)
    {
        string instead = detected.ToUpperInvariant() switch
        {
            "THRU" => Thru,
            "GS" => Gs,
            "XG" => Xg,
            _ => string.Empty,
        };

        return instead.Length > 0 ? instead : detected;
    }
}
