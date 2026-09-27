using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace Glosa.App.Views;

/// <summary>
/// Which glosa this is: its version, its license and where it comes from.
/// </summary>
public partial class AboutWindow : Window
{
    private const string HomePage = "https://github.com/hsshss/glosa";

    /// <summary>
    /// X's compose page with the tag filled in. A tag on X is letters, digits and "_" and
    /// ignores case, so the capitals only make it easier to read.
    /// </summary>
    private const string PostOnX = "https://x.com/intent/post?hashtags=GlosaMIDI";

    public AboutWindow()
    {
        AvaloniaXamlLoader.Load(this);

        this.FindControl<TextBlock>("Version")!.Text = string.Format(Strings.AboutVersion, BuildVersion());

        var home = this.FindControl<HyperlinkButton>("Home")!;
        home.Content = HomePage;
        home.NavigateUri = new Uri(HomePage);

        var post = this.FindControl<HyperlinkButton>("Post")!;
        post.Content = Strings.AboutPostOnX;
        post.NavigateUri = new Uri(PostOnX);

        this.FindControl<SelectableTextBlock>("Runtime")!.Text =
            $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription} " +
            $"({RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})";
    }

    /// <summary>
    /// The version the build was given (a release's is its tag), and the commit it was built
    /// from where the build knew it: "1.2.0 (1eae798)".
    /// </summary>
    /// <remarks>
    /// The SDK appends the full commit to the informational version after a "+"; seven
    /// characters are what git itself shows.
    /// </remarks>
    private static string BuildVersion()
    {
        string informational = typeof(AboutWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = informational.IndexOf('+');
        if (plus < 0) return informational;

        string commit = informational[(plus + 1)..];
        return $"{informational[..plus]} ({commit[..Math.Min(7, commit.Length)]})";
    }

    /// <summary>Esc closes the window, there being no button to press.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape) return;

        Close();
        e.Handled = true;
    }
}
