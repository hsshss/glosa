using Glosa.Core.Emulation;
using Glosa.Core.Playback;

namespace Glosa.App.Services;

/// <summary>
/// One set of output devices, and the modules it is meant for.
/// </summary>
/// <remarks>
/// When auto-detection settles on a module, the first map that names it is taken and the
/// outputs switch to it.
/// </remarks>
public sealed class PortMap
{
    public string Title { get; set; } = Strings.DefaultPortMapName;

    /// <summary>
    /// Output device names by port (<c>A</c>..<c>F</c>, <see cref="PortKey"/>). A port with
    /// no device is not in it.
    /// </summary>
    /// <remarks>
    /// Keyed by port rather than listed in order, so an unused port is left out instead of
    /// being written as an empty name to hold the place of the ones after it.
    /// </remarks>
    public Dictionary<string, string> Ports { get; set; } = [];

    /// <summary>The key a port goes under in <see cref="Ports"/>: A for 0.</summary>
    public static string PortKey(int port) => ((char)('A' + port)).ToString();

    /// <summary>The port a key names (A for 0), or null for one that names none.</summary>
    public static int? PortOf(string? key)
        => key is [var c] && c >= 'A' && c - 'A' < IEventSink.PortCount ? c - 'A' : null;

    /// <summary>
    /// The ports a module reset goes to when there is no DEF, by the same keys as
    /// <see cref="Ports"/>.
    /// </summary>
    /// <remarks>
    /// Not used with a DEF loaded: its resets go where it says.
    /// </remarks>
    public List<string> ResetPorts { get; set; } = [PortKey(0)];

    /// <summary>
    /// Target modules this map is for. Empty means it is never chosen on its own.
    /// </summary>
    /// <remarks>
    /// Always empty on the first map.
    /// </remarks>
    public List<string> Modules { get; set; } = [];

    /// <summary>
    /// The output module this map plays through — the machine on the other end of its ports.
    /// </summary>
    /// <remarks>THRU, no emulation, is what a map with nothing said about it plays as.</remarks>
    public string UseModule { get; set; } = "THRU";
}

/// <summary>One of the words a song is detected from, and whether it is searched.</summary>
public sealed class DetectionSourceSetting
{
    public DetectionSource Source { get; set; }

    public bool Enabled { get; set; }

    /// <summary>
    /// The sources <see cref="NameDetection.Default"/> searches, in its order and on, then
    /// the rest, off.
    /// </summary>
    public static List<DetectionSourceSetting> Defaults()
    {
        IReadOnlyList<DetectionSource> on = NameDetection.Default.Sources;
        return [.. on.Concat(Enum.GetValues<DetectionSource>().Except(on))
                     .Select(source => new DetectionSourceSetting
                     {
                         Source = source,
                         Enabled = on.Contains(source),
                     })];
    }

    /// <summary>
    /// Each source once, in the order it first comes, and any that is missing after them as
    /// <see cref="Defaults"/> has it.
    /// </summary>
    /// <remarks>
    /// So the list on screen always has all of them, whatever the settings file says.
    /// </remarks>
    public static List<DetectionSourceSetting> Tidy(IEnumerable<DetectionSourceSetting> saved)
    {
        List<DetectionSourceSetting> kept = [.. saved.Where(s => Enum.IsDefined(s.Source))
                                                     .DistinctBy(s => s.Source)];
        kept.AddRange(Defaults().Where(d => kept.All(s => s.Source != d.Source)));
        return kept;
    }
}

/// <summary>What a playlist row shows for a song.</summary>
public enum SongLabel
{
    /// <summary>The title, or the file name without its extension when the song has none.</summary>
    Title,

    /// <summary>The file name, extension and all.</summary>
    FileName,

    /// <summary>The whole path, the archive's included for a song inside one.</summary>
    Path,
}

/// <summary>Where a window was and how big, as the last run left it.</summary>
/// <remarks>
/// Each in the units the window is placed and sized in: the position in the screen's pixels,
/// the size of its client area in device-independent pixels. Zero width means nothing was
/// recorded, so the window opens where it likes.
/// </remarks>
public sealed class WindowPlacement
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>
    /// Whether the window was maximised. The position and size stay those it had before, to
    /// come back to.
    /// </summary>
    public bool Maximized { get; set; }

    /// <summary>
    /// Whether the window was open when the player was last closed.
    /// </summary>
    /// <remarks>
    /// Only the panes are opened again from this. The main window opens either way, and the
    /// two settings windows are answers to a question that was asked last time, not this one.
    /// </remarks>
    public bool Open { get; set; }
}

/// <summary>
/// Everything the player remembers between runs.
/// </summary>
/// <remarks>
/// In a file beside the playlists (<see cref="SettingsPath"/>), the same on every platform.
/// Written whole, defaults included: a value left out would read back as whatever the default
/// is by the time it is read.
/// </remarks>
public sealed class AppSettings
{
    /// <summary>
    /// The version of the format this player reads and writes. Raised only when a file
    /// written in it could no longer be read as it was meant (IMPLEMENTATION.md).
    /// </summary>
    public const int FormatVersion = 1;

    /// <summary>The format the file is in; 0 when it says none.</summary>
    public int Version { get; set; }

    /// <summary>
    /// Named sets of port assignments.
    /// </summary>
    /// <remarks>
    /// The first is the default map: always there, and claims no modules.
    /// </remarks>
    public List<PortMap> PortMaps { get; set; } = [];

    /// <summary>
    /// The map chosen by hand, as its place in <see cref="PortMaps"/>; null for Auto.
    /// </summary>
    public int? PinnedPortMap { get; set; }

    /// <summary>Path of the DEF in use.</summary>
    public string DefinitionPath { get; set; } = string.Empty;

    /// <summary>
    /// Whether target modules are told apart by the DEF's <c>[keyword]</c> rather than by
    /// the patterns of <c>define.yaml</c>.
    /// </summary>
    public bool UseDefKeywords { get; set; }

    /// <summary>
    /// The words <c>define.yaml</c>'s patterns are run over, in the order they are searched,
    /// each on or off.
    /// </summary>
    /// <remarks>
    /// Every source is listed, the ones turned off too, so that turning one back on puts it
    /// where it was. A file edited by hand is put right as it is read
    /// (<see cref="DetectionSourceSetting.Tidy"/>).
    /// </remarks>
    public List<DetectionSourceSetting> DetectionSources { get; set; } = DetectionSourceSetting.Defaults();

    /// <summary>Whether the model named first in those words decides, or the one named last.</summary>
    public MatchPosition DetectionPosition { get; set; } = NameDetection.Default.Position;

    /// <summary>
    /// The target modules a song detected as THRU, GS or XG is played as. Empty leaves it as
    /// detected (<see cref="DetectionDefaults"/>).
    /// </summary>
    public string ThruPlaysAs { get; set; } = string.Empty;

    public string GsPlaysAs { get; set; } = string.Empty;

    public string XgPlaysAs { get; set; } = string.Empty;

    /// <summary>How many times a loop the data says is endless plays.</summary>
    public int LoopRepeatCount { get; set; } = 2;

    /// <summary>The priority of the thread that sends the song.</summary>
    public PlaybackPriority Priority { get; set; } = PlaybackPriority.High;

    /// <summary>
    /// The most bytes a second handed to each port, for hardware that cannot keep up. 0 is
    /// unlimited.
    /// </summary>
    public int TransferRate { get; set; }

    /// <summary>Whether stopping also resets the output devices, besides All Notes Off.</summary>
    public bool UseMidiOutReset { get; set; } = true;

    public bool SendAllNotesOffOnStop { get; set; } = true;

    /// <summary>
    /// Plays a variation tone a Sound Canvas from the SC-55mkII on does not have on one it
    /// does, as the SC-55 did.
    /// </summary>
    public bool CapitalToneFallback { get; set; }

    /// <summary>
    /// With no DEF, whether the output module is reset before each song, as define.yaml's
    /// initializeType says.
    /// </summary>
    public bool SendModuleReset { get; set; } = true;

    /// <summary>
    /// With no DEF, whether a song for an earlier model is played on that model's tone map
    /// where the output module carries it.
    /// </summary>
    public bool SwitchToneMap { get; set; } = true;

    /// <summary>Whether a song the words name no model for is detected from its data.</summary>
    public bool DetectFromData { get; set; } = true;

    /// <summary>Whether the platform's media keys drive the player.</summary>
    public bool UseMediaKeys { get; set; } = true;

    /// <summary>
    /// The language the player is shown in: a culture (<c>en</c>, <c>ja</c>), or empty for the
    /// system's. Taken up at startup (<see cref="Languages.Apply"/>).
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// Whether songs are read as they are added, before they are played: for their length,
    /// and for the title and data the list's target module column is worked out from.
    /// </summary>
    public bool ScanLength { get; set; } = true;

    /// <summary>Tidy the spacing in titles before showing them.</summary>
    public bool TrimTitles { get; set; }

    /// <summary>What the playlists show for each song.</summary>
    public SongLabel SongLabel { get; set; } = SongLabel.Title;

    /// <summary>
    /// Start playing what was dropped or handed over from outside, rather than only listing it.
    /// </summary>
    public bool AutoDropPlay { get; set; } = true;

    /// <summary>
    /// The playlists open as tabs, in tab order, and where each was left.
    /// </summary>
    public List<OpenPlaylist> OpenPlaylists { get; set; } = [];

    /// <summary>Which tab was in front.</summary>
    public int ActivePlaylist { get; set; }

    /// <summary>The order the playlist is played in.</summary>
    public PlayOrder Order { get; set; } = PlayOrder.Registered;

    /// <summary>What happens when a song ends.</summary>
    public RepeatMode Repeat { get; set; } = RepeatMode.None;

    /// <summary>
    /// Whether the display gets room of its own instead of taking the height of the
    /// transport beside it. Off to begin with: the compact one lines the window up.
    /// </summary>
    public bool LcdEnlarged { get; set; }

    /// <summary>Whether the graphics hardware draws the windows. Taken up at startup.</summary>
    public bool HardwareRendering { get; set; } = true;

    /// <summary>
    /// Where each window was, by name: <c>main</c>, <c>settings</c>, <c>portMaps</c>,
    /// <c>monitor</c>, <c>debug</c>.
    /// </summary>
    /// <remarks>
    /// A map rather than a field per window, since every window wants the same numbers.
    /// </remarks>
    public Dictionary<string, WindowPlacement> Windows { get; set; } = [];

    /// <summary>
    /// Where the settings and the working playlist live: the roaming application data
    /// folder: <c>~/Library/Application Support</c> on macOS, and <c>~/.config</c> on Linux,
    /// which has no such thing.
    /// </summary>
    /// <remarks>
    /// Settable for <c>--config</c>. Set before anything reads the settings; moving it
    /// afterwards would leave half the player reading one folder and half the other.
    /// </remarks>
    public static string ConfigDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "glosa");

    public static string SettingsPath => Path.Combine(ConfigDirectory, "settings.yaml");

    /// <summary>Where playlists of our own making go. One file each.</summary>
    public static string PlaylistDirectory => Path.Combine(ConfigDirectory, "playlists");

    /// <summary>
    /// Reads the settings, or the defaults when there are none. Also the defaults when the
    /// file is there but cannot be read, and <paramref name="problem"/> says why.
    /// </summary>
    public static AppSettings Load(out string? problem)
    {
        problem = null;
        if (!File.Exists(SettingsPath)) return new AppSettings();

        try
        {
            AppSettings read = YamlFile.Load<AppSettings>(SettingsPath);
            if (read.Version <= FormatVersion) return Tidy(read);
            problem = string.Format(Strings.SettingsTooNew, read.Version, FormatVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidDataException or YamlDotNet.Core.YamlException)
        {
            problem = ex.Message;
        }
        return new AppSettings();
    }

    /// <summary>
    /// Puts back what a key left without a value took away: the reader makes it null. An
    /// empty entry in a list or a map is dropped.
    /// </summary>
    private static AppSettings Tidy(AppSettings read)
    {
        var fresh = new AppSettings();

        read.PortMaps = [.. (read.PortMaps ?? []).OfType<PortMap>()];
        foreach (PortMap map in read.PortMaps)
        {
            var blank = new PortMap();
            map.Title ??= blank.Title;
            map.Ports = (map.Ports ?? []).Where(port => port.Value is not null).ToDictionary();
            map.ResetPorts = [.. (map.ResetPorts ?? blank.ResetPorts).OfType<string>()];
            map.Modules = [.. (map.Modules ?? []).OfType<string>()];
            map.UseModule ??= blank.UseModule;
        }

        read.DefinitionPath ??= fresh.DefinitionPath;
        read.DetectionSources = [.. (read.DetectionSources ?? fresh.DetectionSources)
                                        .OfType<DetectionSourceSetting>()];
        read.ThruPlaysAs ??= fresh.ThruPlaysAs;
        read.GsPlaysAs ??= fresh.GsPlaysAs;
        read.XgPlaysAs ??= fresh.XgPlaysAs;
        read.Language ??= fresh.Language;
        read.OpenPlaylists = [.. (read.OpenPlaylists ?? []).Where(open => open is { Path: not null })];
        read.Windows = (read.Windows ?? []).Where(window => window.Value is not null).ToDictionary();
        return read;
    }

    /// <summary>The settings as <see cref="Save"/> writes them.</summary>
    public string ToYaml()
    {
        Version = FormatVersion;
        return YamlFile.ToYaml(this, whole: true);
    }

    public void Save() => YamlFile.Write(ToYaml(), SettingsPath);
}
