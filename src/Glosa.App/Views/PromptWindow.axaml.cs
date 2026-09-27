using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Glosa.App.Views;

/// <summary>
/// Asks for one line of text.
/// </summary>
/// <remarks>
/// Avalonia has no such dialog of its own. Returns null when the user backs out or leaves
/// the line empty.
/// </remarks>
public partial class PromptWindow : Window
{
    public PromptWindow() => AvaloniaXamlLoader.Load(this);

    public static async Task<string?> AskAsync(Window owner, string title, string label,
                                               string initial)
    {
        var window = new PromptWindow { Title = title };
        window.FindControl<TextBlock>("Label")!.Text = label;

        TextBox entry = window.FindControl<TextBox>("Entry")!;
        entry.Text = initial;
        window.Opened += (_, _) => { entry.SelectAll(); entry.Focus(); };

        return await window.ShowDialog<string?>(owner);
    }

    private void OnAccept(object? sender, RoutedEventArgs e)
    {
        string text = (this.FindControl<TextBox>("Entry")!.Text ?? string.Empty).Trim();
        Close(text.Length == 0 ? null : text);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnAccept(sender, e);
    }
}
