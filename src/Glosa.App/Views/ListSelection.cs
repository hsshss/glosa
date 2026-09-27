using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;

namespace Glosa.App.Views;

/// <summary>Small things every list box in the window wants to do the same way.</summary>
internal static class ListSelection
{
    /// <summary>
    /// Makes the row under a right-click the selection, unless it is already in one.
    /// </summary>
    /// <remarks>
    /// What every file list does, and what a context menu needs: without it the menu opens
    /// over one row and acts on whichever rows happen to be selected elsewhere. A row that
    /// is already part of the selection is left alone, so a right-click on a group of rows
    /// still means the group.
    /// </remarks>
    public static void ForMenu(ListBox list, ContextRequestedEventArgs e)
    {
        if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>() is not { } row) return;
        if (list.SelectedItems?.Contains(row.DataContext) == true) return;

        list.SelectedItems?.Clear();
        list.SelectedItem = row.DataContext;
    }

    /// <summary>
    /// The selected rows in list order, as text, one per line.
    /// </summary>
    /// <remarks>
    /// In the order the list has them, not the order they were clicked in. A block of log
    /// lines copied out of order would be a different account of what happened.
    /// </remarks>
    public static string SelectedText(ListBox list)
    {
        if (list.SelectedItems is not { Count: > 0 }) return string.Empty;

        var rows = new List<(int Index, string Text)>();
        foreach (object? item in list.SelectedItems)
        {
            int index = list.Items.IndexOf(item);
            if (index >= 0) rows.Add((index, item?.ToString() ?? string.Empty));
        }

        return string.Join(Environment.NewLine, rows.OrderBy(r => r.Index).Select(r => r.Text));
    }

    /// <summary>Puts the selected rows on the clipboard. False when there was nothing to put.</summary>
    public static bool CopySelected(ListBox list)
    {
        string text = SelectedText(list);
        if (text.Length == 0) return false;
        if (TopLevel.GetTopLevel(list)?.Clipboard is not { } clipboard) return false;

        // Nothing waits for this: the clipboard is the platform's to get to when it can, and
        // the list has no answer to give either way.
        _ = clipboard.SetTextAsync(text);
        return true;
    }
}
