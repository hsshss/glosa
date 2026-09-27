using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Glosa.App.ViewModels;

namespace Glosa.App.Views;

/// <summary>
/// Lets songs and folders be dropped onto a control.
/// </summary>
/// <remarks>
/// Attached to the main window and to the playlist in it, which takes a drop before the
/// window would, so a drop anywhere on the player behaves the same.
/// </remarks>
internal static class FileDrop
{
    internal static void Attach(Control target, Func<MainViewModel?> model)
    {
        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragOverEvent, (_, e) => OnDragOver(e));
        target.AddHandler(DragDrop.DropEvent, (_, e) => OnDrop(e, model()));
    }

    private static void OnDragOver(DragEventArgs e)
    {
        // Copy rather than move: the files stay where they are and join the list.
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? e.DragEffects & DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private static void OnDrop(DragEventArgs e, MainViewModel? model)
    {
        if (model is null) return;

        IStorageItem[]? items = e.DataTransfer.TryGetFiles();
        if (items is null) return;

        // Folders arrive as items too; what is inside them is sorted out in the core.
        model.ReceiveFiles(items.Select(item => item.Path.LocalPath));
        e.Handled = true;
    }
}
