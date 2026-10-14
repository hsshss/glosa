#if BRACK
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Glosa.App.ViewModels;
using Glosa.Midi.Brack;

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

    /// <remarks>A press on an effect is its own list's to drag.</remarks>
    private bool Press(PointerPressedEventArgs e)
    {
        _dragging = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>()?.DataContext as RackRow;
        return _dragging is not null;
    }

    /// <summary>Drops the instrument into the gap, keeping it selected.</summary>
    private bool MoveTo(ListBox list, int target)
    {
        if (_dragging is null || Model is null || !Model.Move(_dragging, target)) return false;

        list.SelectedItem = _dragging;
        return true;
    }

    /// <summary>Drag-to-reorder among a chain's effects.</summary>
    private void OnEffectsLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not ListBox list) return;

        EffectRow? dragging = null;
        ReorderDrag.Attach(
            list,
            press =>
            {
                dragging = (press.Source as Control)?.FindAncestorOfType<ListBoxItem>()?.DataContext as EffectRow;
                return dragging is not null;
            },
            () => dragging is not null,
            target => dragging is not null && list.ItemsSource is IEnumerable<EffectRow> chain
                      && Model?.MoveEffect(dragging, [.. chain], target) == true,
            () => { });
    }

    private async void OnRename(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        (RackPlugin? plugin, string? name) = (sender as Control)?.DataContext switch
        {
            RackRow row => (row.Plugin, row.Name),
            EffectRow row => (row.Plugin, row.Name),
            _ => (null, null),
        };
        if (plugin is null) return;

        string? renamed = await PromptWindow.AskAsync(
            this, Strings.AudioPluginsRenameTitle, Strings.AudioPluginsRenamePrompt, name!);
        if (renamed is not null && renamed != name) await model.Rename(plugin, renamed);
    }

    /// <summary>Opens the menu of the card or the effect the button is on.</summary>
    private void OnMore(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control button) return;
        if (button.GetVisualAncestors().OfType<Control>().FirstOrDefault(control => control.ContextMenu is not null)
            is { ContextMenu: { } menu } owner)
            menu.Open(owner);
    }

    private async void OnAddInstrument(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        if (await PluginPickerWindow.PickAsync(this, model.InstrumentPicker()) is { } picked)
            await model.AddInstrument(picked.Path, picked.PluginId, picked.Name);
    }

    /// <summary>Adds an effect to the instrument the button or menu is on; to the master's elsewhere.</summary>
    private async void OnAddEffect(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        RackRow? row = (sender as Control)?.DataContext as RackRow;
        if (await PluginPickerWindow.PickAsync(this, model.EffectPicker(row)) is { } picked)
            await model.AddEffect(row, picked.Path, picked.PluginId, picked.Name);
    }

    /// <summary>Opens the double-clicked instrument's editor.</summary>
    private void OnRackDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (OnButton(e)) return;
        if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>()?.DataContext is RackRow row) OpenEditor(row.Plugin);
    }

    /// <summary>Opens the double-clicked effect's editor, and not its instrument's.</summary>
    private void OnEffectDoubleTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
        if (OnButton(e)) return;
        if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>()?.DataContext is EffectRow row) OpenEditor(row.Plugin);
    }

    /// <summary>Whether the double click landed on a button, whose clicks are its own.</summary>
    private static bool OnButton(TappedEventArgs e)
        => (e.Source as Control)?.FindAncestorOfType<Button>(includeSelf: true) is not null;

    private void OpenEditor(RackPlugin plugin)
    {
        if (Model?.EditorCommand is { } editor && editor.CanExecute(plugin)) editor.Execute(plugin);
    }
}
#endif
