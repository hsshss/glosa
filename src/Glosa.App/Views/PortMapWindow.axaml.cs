using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Glosa.App.ViewModels;

namespace Glosa.App.Views;

/// <summary>
/// The port mapper, over the same view model the main window uses, so every change takes
/// effect as it is made rather than on an OK button.
/// </summary>
public partial class PortMapWindow : Window
{
    public PortMapWindow() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? Model => DataContext as MainViewModel;

    private PortMapViewModel? _dragging;

    /// <summary>Arms drag-to-reorder, on the same gesture the playlist uses.</summary>
    private void OnListLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not ListBox list) return;

        ReorderDrag.Attach(list, Press, () => _dragging is not null,
                           target => MoveTo(list, target), () => { });
    }

    private bool Press(PointerPressedEventArgs e)
    {
        _dragging = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>()?.DataContext
            as PortMapViewModel;
        return _dragging is not null;
    }

    /// <summary>
    /// Puts the map into the gap it was dropped in.
    /// </summary>
    /// <remarks>
    /// The selection, which the form on the right follows, is put back: the list box drops
    /// it when the item is taken out.
    /// </remarks>
    private bool MoveTo(ListBox list, int target)
    {
        if (_dragging is null || Model is null) return false;
        if (!Model.MovePortMap(_dragging, target)) return false;

        list.SelectedItem = _dragging;
        return true;
    }
}
