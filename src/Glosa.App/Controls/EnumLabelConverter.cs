using System.Globalization;
using Avalonia.Data.Converters;
using Glosa.Core.Emulation;
using Glosa.Core.Playback;

namespace Glosa.App.Controls;

/// <summary>
/// Shows the enums the menus and settings choose among under their names in Strings.resx.
/// </summary>
public sealed class EnumLabelConverter : IValueConverter
{
    public static readonly EnumLabelConverter Instance = new();

    private static readonly Dictionary<object, string> Labels = new()
    {
        [RepeatMode.None] = Strings.RepeatNone,
        [RepeatMode.Single] = Strings.RepeatSingle,
        [RepeatMode.SingleRepeat] = Strings.RepeatSingleRepeat,
        [RepeatMode.All] = Strings.RepeatAll,
        [PlayOrder.Registered] = Strings.OrderRegistered,
        [PlayOrder.Random] = Strings.OrderRandom,
        [PlayOrder.FileName] = Strings.OrderFileName,
        [PlayOrder.Title] = Strings.OrderTitle,
        [MatchPosition.First] = Strings.DetectionFirst,
        [MatchPosition.Last] = Strings.DetectionLast,
        [PlaybackPriority.Low] = Strings.PriorityLow,
        [PlaybackPriority.Normal] = Strings.PriorityNormal,
        [PlaybackPriority.High] = Strings.PriorityHigh,

        // The transfer-rate choices are plain numbers, and a boxed int never matches a
        // boxed enum, so they can share the table.
        [0] = Strings.RateUnlimited,
        [3125] = Strings.RateMidiCable,
    };

    /// <summary>The name to show for a value, for callers that are not bindings.</summary>
    public static string Text(object? value)
        => value is not null && Labels.TryGetValue(value, out string? label)
            ? label
            : value?.ToString() ?? string.Empty;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Text(value);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("The combo boxes keep the enum as their selection.");
}
