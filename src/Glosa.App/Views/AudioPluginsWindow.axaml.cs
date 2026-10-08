#if BRACK
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Glosa.App.ViewModels;

namespace Glosa.App.Views;

/// <summary>The rack of audio plugins.</summary>
public partial class AudioPluginsWindow : Window
{
    public AudioPluginsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Closed += (_, _) => Model?.Detach();
    }

    private AudioPluginsViewModel? Model => DataContext as AudioPluginsViewModel;

    private RackRow? _dragging;

    /// <summary>Drag-to-reorder, as in the port map list.</summary>
    private void OnRackLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not ListBox list) return;

        ReorderDrag.Attach(list, Press, () => _dragging is not null, target => MoveTo(list, target), () => { });
    }

    private bool Press(PointerPressedEventArgs e)
    {
        _dragging = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>()?.DataContext as RackRow;
        return _dragging is not null;
    }

    /// <summary>Drops the plugin into the gap, keeping it selected.</summary>
    private bool MoveTo(ListBox list, int target)
    {
        if (_dragging is null || Model is null || !Model.Move(_dragging, target)) return false;

        list.SelectedItem = _dragging;
        return true;
    }

    private async void OnRename(object? sender, RoutedEventArgs e)
    {
        if (Model is not { SelectedPlugin: { } row } model) return;

        string? name = await PromptWindow.AskAsync(
            this, Strings.AudioPluginsRenameTitle, Strings.AudioPluginsRenamePrompt, row.Name);
        if (name is not null && name != row.Name) await model.Rename(row, name);
    }

    private async void OnAddFile(object? sender, RoutedEventArgs e)
    {
        // On macOS every format is a bundle, a folder, which the file picker will not choose.
        IReadOnlyList<string> files = OperatingSystem.IsMacOS()
            ? await Dialogs.OpenFoldersAsync(this, Strings.PickAudioPlugin, multiple: false)
            : await Dialogs.OpenAsync(this, Strings.PickAudioPlugin, Dialogs.AudioPluginFiles);
        if (files.Count > 0 && Model is { } model) await model.AddFile(files[0]);
    }

    /// <summary>Adds the double-clicked instrument.</summary>
    private void OnFoundDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>() is null) return;
        if (Model?.AddCommand is { } add && add.CanExecute(null)) add.Execute(null);
    }

    private void OnRackContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is ListBox list) ListSelection.ForMenu(list, e);
    }

    /// <summary>Opens the double-clicked plugin's editor.</summary>
    private void OnRackDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>() is null) return;
        if (Model?.EditorCommand is { } editor && editor.CanExecute(null)) editor.Execute(null);
    }
}
#endif
