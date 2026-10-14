using System.Globalization;

namespace Glosa.App.ViewModels;

/// <summary>A volume slider's decibels: the audio plugins' master volume, and each instrument's.</summary>
public static class Decibels
{
    /// <summary>The range; the floor is silent.</summary>
    public const double Floor = -60, Ceiling = 12;

    public static double FromGain(float gain) => gain > 0 ? Math.Max(20 * Math.Log10(gain), Floor) : Floor;

    public static float ToGain(double db) => db <= Floor ? 0 : (float)Math.Pow(10, Math.Min(db, Ceiling) / 20);

    /// <summary>Signed, to a tenth; the floor as -∞.</summary>
    public static string Text(double db) => db <= Floor ? "-∞" : db.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture);
}
