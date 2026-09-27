using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Glosa.App.ViewModels;

namespace Glosa.App.Views;

/// <summary>
/// The settings, over the same view model the main window uses, so every change takes
/// effect as it is made rather than on an OK button.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? Model => DataContext as MainViewModel;

    private DetectionSourceViewModel? _dragging;

    /// <summary>
    /// Arms drag-to-reorder on the list of words a song is detected from.
    /// </summary>
    /// <remarks>
    /// A press on a line's check box arms it too: a click that does not travel still ticks
    /// the box, and one that does is a drag.
    /// </remarks>
    private void OnSourcesLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not ListBox list) return;

        ReorderDrag.Attach(list, Press, () => _dragging is not null, MoveTo, () => { });
    }

    private bool Press(PointerPressedEventArgs e)
    {
        _dragging = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>()?.DataContext
            as DetectionSourceViewModel;
        // The song's data stays last: its line can be ticked but not picked up.
        if (_dragging is { IsFixed: true }) _dragging = null;
        return _dragging is not null;
    }

    private bool MoveTo(int gap)
        => _dragging is not null && Model is not null && Model.MoveDetectionSource(_dragging, gap);
}
