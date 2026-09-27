using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Glosa.App.Views;

/// <summary>
/// A frame for one pane shown outside the main window.
/// </summary>
public partial class PaneWindow : Window
{
    public PaneWindow() => AvaloniaXamlLoader.Load(this);

    public PaneWindow(string title, Control pane, object? model) : this()
    {
        Title = title;
        DataContext = model;
        this.FindControl<ContentControl>("Host")!.Content = pane;
    }
}
