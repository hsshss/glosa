using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Glosa.App.Views;

/// <summary>The file pickers, in one place so every pane can reach them.</summary>
internal static class Dialogs
{
    internal static readonly FilePickerFileType MidiFiles =
        new(Strings.FileTypeSongs)
        {
            Patterns = AnyCase("*.mid", "*.midi", "*.rmi", "*.rcp", "*.r36", "*.g18", "*.g36",
                               "*.lzh", "*.lha", "*.zip"),
        };

    internal static readonly FilePickerFileType PlaylistFiles =
        new(Strings.FileTypePlaylists) { Patterns = AnyCase("*.yaml", "*.yml") };

    /// <summary>What can be opened as a playlist: the player's own, and M3U.</summary>
    internal static readonly FilePickerFileType OpenablePlaylistFiles =
        new(Strings.FileTypePlaylists) { Patterns = AnyCase("*.yaml", "*.yml", "*.m3u", "*.m3u8") };

    internal static readonly FilePickerFileType M3uFiles =
        new(Strings.FileTypeM3u) { Patterns = AnyCase("*.m3u8", "*.m3u") };

    internal static readonly FilePickerFileType DefinitionFiles =
        new(Strings.FileTypeDefinitions) { Patterns = AnyCase("*.def") };

    /// <summary>CLAP, VST3 (the file inside a bundle) and VST2, as Windows names them.</summary>
    internal static readonly FilePickerFileType AudioPluginFiles =
        new(Strings.FileTypeAudioPlugins) { Patterns = AnyCase("*.clap", "*.vst3", "*.dll") };

    /// <summary>
    /// Each pattern in lower case and in upper case.
    /// </summary>
    /// <remarks>
    /// On Linux the picker is GTK's or the desktop portal's, and both match patterns with
    /// case sensitivity: <c>*.lzh</c> alone hides <c>SONG.LZH</c>, which is how MS-DOS named
    /// everything. Windows and macOS ignore case, so the second copy costs them nothing. A
    /// name in mixed case (<c>Song.Mid</c>) still slips through on Linux; a bracketed glob
    /// would catch it there but means nothing to the Windows picker.
    /// </remarks>
    private static string[] AnyCase(params string[] patterns)
        => [.. patterns, .. patterns.Select(p => p.ToUpperInvariant())];

    internal static async Task<IReadOnlyList<string>> OpenAsync(
        Visual? owner, string title, FilePickerFileType type, bool multiple = false)
    {
        if (TopLevel.GetTopLevel(owner) is not { } top) return [];

        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = multiple,
                FileTypeFilter = [type],
            });

        return [.. files.Select(f => f.Path.LocalPath)];
    }

    internal static async Task<IReadOnlyList<string>> OpenFoldersAsync(Visual? owner, string title)
    {
        if (TopLevel.GetTopLevel(owner) is not { } top) return [];

        IReadOnlyList<IStorageFolder> folders = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = true });

        return [.. folders.Select(f => f.Path.LocalPath)];
    }

    internal static async Task<string?> SaveAsync(
        Visual? owner, string title, string suggestedName, FilePickerFileType type)
    {
        if (TopLevel.GetTopLevel(owner) is not { } top) return null;

        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                DefaultExtension = Path.GetExtension(suggestedName).TrimStart('.'),
                FileTypeChoices = [type],
            });

        return file?.Path.LocalPath;
    }
}
