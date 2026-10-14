#if BRACK
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Glosa.App.ViewModels;

namespace Glosa.App.Views;

/// <summary>A plugin to load: one the scan found, with the id that picks it in its file; or a file, with none.</summary>
public sealed record PickedPlugin(string Path, string? PluginId, string Name);

/// <summary>Picks an instrument or an effect to add, from the scan or from a file.</summary>
public partial class PluginPickerWindow : Window
{
    public PluginPickerWindow() => AvaloniaXamlLoader.Load(this);

    private PluginPickerViewModel? Model => DataContext as PluginPickerViewModel;

    /// <summary>The plugin picked; null when the user backs out.</summary>
    public static async Task<PickedPlugin?> PickAsync(Window owner, PluginPickerViewModel model)
    {
        var window = new PluginPickerWindow { DataContext = model };
        model.Done += () => window.Close(model.Picked is { } plugin ? new PickedPlugin(plugin.Path, plugin.Id, plugin.Name) : null);
        window.Opened += (_, _) => window.FindControl<TextBox>("Filter")?.Focus();
        try
        {
            return await window.ShowDialog<PickedPlugin?>(owner);
        }
        finally
        {
            model.Detach();
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private async void OnAddFile(object? sender, RoutedEventArgs e)
    {
        // On macOS every format is a bundle, a folder, which the file picker will not choose.
        IReadOnlyList<string> files = OperatingSystem.IsMacOS()
            ? await Dialogs.OpenFoldersAsync(this, Strings.PickAudioPlugin, multiple: false)
            : await Dialogs.OpenAsync(this, Strings.PickAudioPlugin, Dialogs.AudioPluginFiles);
        if (files.Count > 0) Close(new PickedPlugin(files[0], null, Path.GetFileName(files[0])));
    }

    /// <summary>Picks the double-clicked plugin.</summary>
    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>() is null) return;
        if (Model?.PickCommand is { } pick && pick.CanExecute(null)) pick.Execute(null);
    }

    /// <summary>Down from the filter goes into the list, so the keyboard can choose without the mouse.</summary>
    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || this.FindDescendantOfType<ListBox>() is not { } list) return;
        list.Focus();
        e.Handled = true;
    }
}
#endif
