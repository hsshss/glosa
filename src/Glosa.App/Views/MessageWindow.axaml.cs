using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Glosa.App.Views;

/// <summary>
/// Says one thing and waits to be acknowledged.
/// </summary>
/// <remarks>
/// Avalonia has no message box of its own.
/// </remarks>
public partial class MessageWindow : Window
{
    public MessageWindow() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Shows the message over <paramref name="owner"/>, once the ones before it are closed.
    /// </summary>
    /// <remarks>
    /// One at a time, and each once: a message the same as one on screen or waiting, title
    /// and all, is the same trouble again, so a song that fails the same way twice running
    /// does not stack two dialogs. Anything that says something else is shown, even under the
    /// same title, such as two files unreadable at startup.
    /// </remarks>
    public static async Task ShowAsync(Window owner, string title, string body)
    {
        if (_showing == (title, body) || Waiting.Contains((title, body))) return;

        Waiting.Enqueue((title, body));
        if (_showing is not null) return;

        while (Waiting.TryDequeue(out (string Title, string Body) next))
        {
            _showing = next;
            try
            {
                var window = new MessageWindow { Title = next.Title };
                window.FindControl<TextBlock>("Body")!.Text = next.Body;
                await window.ShowDialog(owner);
            }
            finally
            {
                _showing = null;
            }
        }
    }

    /// <summary>The message on screen, or null.</summary>
    private static (string Title, string Body)? _showing;

    private static readonly Queue<(string Title, string Body)> Waiting = new();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
