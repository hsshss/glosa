using Avalonia.Input;

namespace Glosa.App.Views;

/// <summary>The keys that mean the same thing under a different name on each system.</summary>
internal static class Shortcuts
{
    /// <summary>What copy and select all are pressed with: Ctrl, and ⌘ on the Mac.</summary>
    public static KeyModifiers Command { get; } =
        OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>Whether <paramref name="key"/> deletes: on the Mac, Backspace counts too.</summary>
    public static bool IsDelete(Key key)
        => key == Key.Delete || OperatingSystem.IsMacOS() && key == Key.Back;
}
