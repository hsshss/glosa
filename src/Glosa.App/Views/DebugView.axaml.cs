using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Glosa.App.Views;

public partial class DebugView : UserControl
{
    public DebugView() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// The list a context menu was last opened over.
    /// </summary>
    /// <remarks>
    /// Remembered rather than looked up when the item is clicked: the menu is drawn in a
    /// popup of its own, and nothing in the tree leads from the item back to the list. Not
    /// static, since each debug window has its own list and menu.
    /// </remarks>
    private ListBox? _list;

    private void OnListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListBox list) return;

        _list = list;
        ListSelection.ForMenu(list, e);

        if (list.ContextMenu?.Items.OfType<MenuItem>()
                .FirstOrDefault(item => item.Name == "Copy") is { } copy)
            copy.IsEnabled = list.SelectedItems is { Count: > 0 };
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (e.Key != Key.C || !e.KeyModifiers.HasFlag(Shortcuts.Command)) return;

        // Handled either way: Ctrl+C (⌘C on the Mac) on a list with nothing selected is still
        // a copy, and letting it travel on would only find something else to mean.
        ListSelection.CopySelected(list);
        e.Handled = true;
    }

    private void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (_list is { } list) ListSelection.CopySelected(list);
    }

    private void OnSelectAll(object? sender, RoutedEventArgs e) => _list?.SelectAll();
}
