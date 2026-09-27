using CommunityToolkit.Mvvm.ComponentModel;
using Glosa.Core.Emulation;

namespace Glosa.App.ViewModels;

/// <summary>
/// One line of the settings' list of what a song is detected from: which, and whether it is
/// used.
/// </summary>
/// <remarks>
/// The order is the list's, so a line does not carry it.
///
/// The song's own data is the line with no <see cref="Source"/>. It is asked only when the
/// words have named nothing, so it stays last (<see cref="IsFixed"/>).
/// </remarks>
public sealed partial class DetectionSourceViewModel : ObservableObject
{
    private readonly Action _changed;

    /// <param name="source">The words, or null for the song's data.</param>
    public DetectionSourceViewModel(DetectionSource? source, bool enabled, Action changed)
    {
        Source = source;
        Enabled = enabled;
        // Set after the first value, so taking it up is not a change.
        _changed = changed;
    }

    /// <summary>The words this line stands for; null for the song's data.</summary>
    public DetectionSource? Source { get; }

    /// <summary>Whether the line stays where it is: the song's data, always last.</summary>
    public bool IsFixed => Source is null;

    public string Label => Source switch
    {
        DetectionSource.FolderPath => Strings.DetectionFolderPath,
        DetectionSource.FileName => Strings.DetectionFileName,
        DetectionSource.Title => Strings.DetectionTitle,
        DetectionSource.Document => Strings.DetectionDocument,
        null => Strings.DetectionData,
        _ => Source.ToString() ?? string.Empty,
    };

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    partial void OnEnabledChanged(bool value) => _changed?.Invoke();
}
