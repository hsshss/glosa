#if BRACK
using System.Text.Json;
using Brack;

namespace Glosa.Midi.Brack;

/// <summary>Where a plugin in the rack stands.</summary>
public enum RackPluginState
{
    /// <summary>Named in the session file, which is still being loaded.</summary>
    Loading,

    /// <summary>Loaded, and taking MIDI.</summary>
    Ready,

    /// <summary>Would not load: its file is gone, or it refused.</summary>
    Failed,

    /// <summary>Crashed, and is not called again unless reloaded.</summary>
    Crashed,
}

/// <summary>A plugin in the rack.</summary>
/// <param name="Id">Brack's id for it, unique in the rack.</param>
/// <param name="Name">What it is called, and what its output devices are named after.</param>
/// <param name="Format">CLAP, VST3 or VST2; empty while loading.</param>
/// <param name="Architecture">The CPU it is built for (x86, x64, arm64), which may not be the player's; empty while loading.</param>
/// <param name="Status">What Brack says of it: why it failed or where it crashed.</param>
/// <param name="NotePorts">How many note ports it takes MIDI on; none until it is loaded.</param>
public sealed record RackPlugin(string Id, string Name, string Format, string Architecture, RackPluginState State,
                                string Status, bool HasEditor, int NotePorts);

/// <summary>A session file moved out of the way, and why it would not load.</summary>
public sealed record SetAsideFile(string Path, string Why);

/// <summary>What the audio is doing, for meters.</summary>
/// <param name="Load">How much of each period the plugins took to render, 1 being all of it.</param>
/// <param name="Peaks">Each output channel's peak, linear, falling away after the sound.</param>
public sealed record RackMeters(bool Running, float Load, IReadOnlyList<float> Peaks);

/// <summary>
/// A Brack engine and its rack, kept in a session file; each plugin's note port is an output
/// (<see cref="Outputs"/>).
/// </summary>
/// <remarks>
/// Loads in the background, listing the file's plugins meanwhile; the audio runs only while
/// in use (IMPLEMENTATION.md, "Brack バックエンド").
/// </remarks>
public sealed class BrackRack : IDisposable
{
    /// <summary>How long the audio runs on after the last hold ends.</summary>
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(3);

    /// <summary>How long a plugin's change waits for the next before it is saved.</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(3);

    private readonly BrackEngine _engine;
    private readonly string _sessionPath;
    private readonly Task _loaded;
    private readonly Lock _gate = new();
    private readonly Timer _stopTimer;
    private readonly Timer _saveTimer;

    /// <summary>Open outputs and showing editors, which keep the audio running.</summary>
    private int _holds;

    /// <summary>The plugins whose editors hold the audio.</summary>
    private readonly HashSet<string> _editors = [];

    private ulong _savedChanges;
    private volatile IReadOnlyList<RackPlugin> _plugins;
    private bool _disposed;

    private readonly KeptLog _log;

    private BrackRack(string sessionPath, KeptLog log)
    {
        _engine = new BrackEngine();
        _log = log;
        _sessionPath = sessionPath;
        _plugins = Listed(sessionPath);
        _stopTimer = new Timer(_ => StopIfIdle());
        _saveTimer = new Timer(_ => SaveQuietly());
        Outputs = new BrackOutputFactory(this);
        _loaded = Task.Run(Load);
        _engine.EventsQueued += (_, _) => ThreadPool.QueueUserWorkItem(_ => OnEvents());
    }

    /// <summary>The rack in <paramref name="sessionPath"/>, or null with <paramref name="why"/> where Brack cannot load.</summary>
    public static BrackRack? TryCreate(string sessionPath, out string? why)
    {
        why = null;
        var log = new KeptLog();
        try
        {
            // Before the engine, so that loading the session is in it.
            BrackLibrary.Log += log.Write;
            return new BrackRack(sessionPath, log);
        }
        catch (Exception ex) when (Unavailable(ex))
        {
            why = ex.Message;
            try
            {
                BrackLibrary.Log -= log.Write;
            }
            catch (Exception again) when (Unavailable(again))
            {
            }
            return null;
        }

        static bool Unavailable(Exception ex) => ex is DllNotFoundException or EntryPointNotFoundException
                                                       or BadImageFormatException or BrackException;
    }

    /// <summary>
    /// Hands Brack's log to <paramref name="log"/>, on Brack's threads: first what came since the
    /// rack was created, then the rest as it comes.
    /// </summary>
    public void TakeLog(Action<BrackLogLevel, string> log) => _log.HandTo(log);

    /// <summary>Each note port as an output device.</summary>
    public IMidiOutputFactory Outputs { get; }

    /// <summary>The plugins in rack order.</summary>
    public IReadOnlyList<RackPlugin> Plugins => _plugins;

    /// <summary>Raised on any thread when <see cref="Plugins"/> or <see cref="Problem"/> changes.</summary>
    public event Action? Changed;

    /// <summary>The last problem with loading, saving or the audio device.</summary>
    public string? Problem { get; private set; }

    /// <summary>The session file moved aside because it would not load; or null.</summary>
    public SetAsideFile? SetAside { get; private set; }

    /// <summary>Waits until the session file has been loaded.</summary>
    /// <remarks>
    /// Never on the macOS main thread before <see cref="Loaded"/> ends: Brack loads it on that
    /// thread, which would be waiting.
    /// </remarks>
    public void WaitLoaded() => _loaded.Wait();

    /// <summary>Ends once the session file has been loaded; never faults.</summary>
    public Task Loaded => _loaded;

    public RackPlugin? Find(string id) => _plugins.FirstOrDefault(plugin => plugin.Id == id);

    /// <summary>The meters as of at most about 50 ms ago, without waiting for a plugin being loaded.</summary>
    public RackMeters Meters()
    {
        if (_disposed) return new RackMeters(false, 0, []);
        EngineStatus status = _engine.GetCachedStatus();
        return new RackMeters(status.Mode != EngineMode.Stopped, status.CpuLoad, status.OutputPeaks);
    }

    /// <summary>The audio output devices, the system's default first.</summary>
    public static IReadOnlyList<AudioDeviceInfo> AudioDevices() => BrackLibrary.ListAudioDevices();

    /// <summary>The audio output device; null or empty for the system's default.</summary>
    public string? AudioDevice
    {
        get => _engine.GetConfig().AudioDevice;
        set
        {
            WaitLoaded();
            Run(() => _engine.SetConfig(_engine.GetConfig() with { AudioDevice = value }));
        }
    }

    /// <summary>The gain on all the plugins' sound, linear (1 = 0 dB); kept in the session.</summary>
    /// <remarks>Never blocks. Set before the session has loaded, it gives way to the session's.</remarks>
    public float MasterGain
    {
        get => _engine.MasterGain;
        set => _engine.MasterGain = value;
    }

    /// <summary>
    /// The instruments in the standard plugin folders. Blocks; files unchanged since the scan
    /// cached in <paramref name="cachePath"/> are not loaded again.
    /// </summary>
    /// <exception cref="BrackException">The scan failed.</exception>
    public static IReadOnlyList<PluginDescription> Scan(string cachePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        return [.. BrackLibrary.ScanPlugins(cachePath).Where(plugin => plugin.Instrument)];
    }

    /// <summary>Loads a plugin, routed to the first two channels. Blocks.</summary>
    /// <remarks>A name already in the rack gets the lowest free "(2)", "(3)"…, bracketed apart from port numbers.</remarks>
    /// <param name="pluginId">Which plugin in the file; null for the first.</param>
    /// <exception cref="BrackException">It would not load.</exception>
    public void Add(string path, string? pluginId = null)
    {
        WaitLoaded();
        Run(() =>
        {
            string id = _engine.AddPlugin(path, pluginId);
            IReadOnlyList<PluginStatus> plugins = _engine.GetStatus().Plugins;
            string name = plugins.First(plugin => plugin.Id == id).Name;
            var taken = new HashSet<string>(plugins.Where(plugin => plugin.Id != id).Select(plugin => plugin.Name),
                                             StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(name)) return;

            int number = 2;
            while (taken.Contains($"{name} ({number})")) number++;
            _engine.SetPluginName(id, $"{name} ({number})");
        });
    }

    /// <exception cref="BrackException">It is not in the rack.</exception>
    public void Remove(string id)
    {
        WaitLoaded();
        lock (_gate)
            if (_editors.Contains(id)) _engine.ShowPluginGui(id, false);
        Run(() => _engine.RemovePlugin(id));
        LetGoOfEditor(id);
    }

    /// <summary>Loads a crashed or failed plugin again, keeping its state, name and devices. Blocks.</summary>
    /// <exception cref="BrackException">It is not in the rack, or would not load.</exception>
    public void Reload(string id)
    {
        WaitLoaded();
        Run(() => _engine.ReloadPlugin(id));
    }

    /// <summary>Renames a plugin, and so its output devices.</summary>
    /// <returns>The devices renamed, before and after.</returns>
    /// <exception cref="BrackException">It is not in the rack.</exception>
    public IReadOnlyList<(MidiDeviceInfo From, MidiDeviceInfo To)> Rename(string id, string name)
    {
        WaitLoaded();
        IReadOnlyList<MidiDeviceInfo> before = Find(id) is { } plugin ? BrackOutputFactory.Devices([plugin]) : [];
        Run(() => _engine.SetPluginName(id, name));
        IReadOnlyList<MidiDeviceInfo> after = Find(id) is { } renamed ? BrackOutputFactory.Devices([renamed]) : [];
        return [.. before.Zip(after).Where(devices => devices.First.Name != devices.Second.Name)];
    }

    /// <summary>Moves a plugin to <paramref name="index"/>, and its devices with it.</summary>
    /// <exception cref="BrackException">It is not in the rack.</exception>
    public void Move(string id, int index)
    {
        WaitLoaded();
        Run(() => _engine.MovePlugin(id, index));
    }

    /// <summary>Shows a plugin's editor, holding the audio while it is open.</summary>
    /// <exception cref="BrackException">No editor, or it or the audio would not open.</exception>
    public void ShowEditor(string id)
    {
        WaitLoaded();
        lock (_gate)
        {
            if (!_editors.Contains(id))
            {
                Hold();
                _editors.Add(id);
            }
        }

        try
        {
            _engine.ShowPluginGui(id, true);
        }
        catch (BrackException)
        {
            LetGoOfEditor(id);
            throw;
        }
        Refresh();
    }

    /// <summary>Starts the audio if needed, and keeps it running until <see cref="Release"/>.</summary>
    /// <exception cref="BrackException">The audio device would not start.</exception>
    internal void Hold()
    {
        lock (_gate)
        {
            _stopTimer.Change(Timeout.Infinite, Timeout.Infinite);
            if (_engine.GetStatus().Mode == EngineMode.Stopped)
            {
                try
                {
                    _engine.Start();
                    Problem = null;
                }
                catch (BrackException ex)
                {
                    Problem = ex.Message;
                    Changed?.Invoke();
                    throw;
                }
            }
            _holds++;
        }
    }

    /// <summary>Ends a <see cref="Hold"/>; the audio stops shortly after the last.</summary>
    internal void Release()
    {
        lock (_gate)
            if (--_holds == 0) _stopTimer.Change(Linger, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Sends one message to a note port, played where in the audio's period it was sent, a
    /// period on (Brack's <c>brack_send_midi</c>). Does not allocate.
    /// </summary>
    internal MidiSendResult Send(string id, int notePort, ReadOnlySpan<byte> message)
        => _engine.SendMidi(id, notePort, message);

    /// <summary>For the tests.</summary>
    internal BrackEngine Engine => _engine;

    /// <summary>Why a plugin did not take a message (<see cref="Send"/>).</summary>
    internal string WhyNotTaken(string id, MidiSendResult result)
    {
        switch (result)
        {
            case MidiSendResult.NotFound: return "the plugin is no longer in the rack";
            case MidiSendResult.InvalidArgument: return "not a complete MIDI message";
        }
        // A plugin that crashed or failed stays in the rack, and its queue fills.
        Refresh();
        return Find(id) is { State: RackPluginState.Crashed or RackPluginState.Failed } plugin
            ? plugin.Status
            : "the plugin is not taking messages as fast as they come";
    }

    /// <remarks>Never throws: opening an output and closing the player wait for it.</remarks>
    private void Load()
    {
        if (File.Exists(_sessionPath))
        {
            try
            {
                _engine.LoadSession(_sessionPath);
            }
            catch (BrackException ex)
            {
                // Moved aside so the next save does not overwrite it.
                Problem = ex.Message;
                string aside = _sessionPath + ".broken";
                try
                {
                    File.Move(_sessionPath, aside, overwrite: true);
                    SetAside = new SetAsideFile(aside, ex.Message);
                }
                catch (Exception moving) when (moving is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        _savedChanges = _engine.ChangeCount;
        try
        {
            Refresh();
        }
        catch (BrackException ex)
        {
            Problem = ex.Message;
        }
    }

    /// <summary>The plugins the session file names, as loading; none if it cannot be read.</summary>
    private static IReadOnlyList<RackPlugin> Listed(string sessionPath)
    {
        try
        {
            using FileStream file = File.OpenRead(sessionPath);
            using JsonDocument session = JsonDocument.Parse(file);
            if (!session.RootElement.TryGetProperty("plugins", out JsonElement plugins)) return [];
            return [.. plugins.EnumerateArray().Select(plugin => new RackPlugin(
                Text(plugin, "id"), Text(plugin, "name"), string.Empty, string.Empty, RackPluginState.Loading, string.Empty,
                false, 0))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                      or InvalidOperationException)
        {
            return [];
        }

        static string Text(JsonElement element, string name)
            => element.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>Makes a change, then saves and refreshes.</summary>
    private void Run(Action change)
    {
        try
        {
            change();
        }
        finally
        {
            SaveQuietly();
            Refresh();
        }
    }

    /// <summary>Takes the plugins' state from the engine.</summary>
    private void Refresh()
    {
        if (_disposed) return;

        _plugins = [.. _engine.GetStatus().Plugins.Select(plugin => new RackPlugin(
            plugin.Id, plugin.Name, plugin.Format.ToString().ToUpperInvariant(), plugin.Architecture,
            plugin.Crashed ? RackPluginState.Crashed
            : plugin.Failed || !plugin.Loaded ? RackPluginState.Failed
            : RackPluginState.Ready,
            plugin.Status, plugin.HasGui, plugin.NotePorts.Count))];
        Changed?.Invoke();
    }

    /// <summary>Handles the engine's queued events on a pool thread; any after disposal are dropped.</summary>
    private void OnEvents()
    {
        try
        {
            bool refresh = false;
            foreach (BrackEvent ev in _engine.PollEvents())
            {
                switch (ev.Type)
                {
                    case BrackEventType.Changed:
                        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
                        break;
                    case BrackEventType.EditorClosed or BrackEventType.PluginCrashed:
                        LetGoOfEditor(ev.Id);
                        refresh = true;
                        break;
                }
            }
            if (refresh) Refresh();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void LetGoOfEditor(string id)
    {
        lock (_gate)
            if (_editors.Remove(id)) Release();
    }

    private void StopIfIdle()
    {
        lock (_gate)
            if (_holds == 0 && !_disposed) _engine.Stop();
    }

    /// <summary>Saves the session if the rack changed since the last save.</summary>
    /// <param name="always">
    /// Saves a rack that has plugins even with no change counted, for plugins that do not report
    /// theirs. An empty one is left, so it does not write over a file that would not load.
    /// </param>
    private void SaveQuietly(bool always = false)
    {
        if (!_loaded.IsCompleted) return;
        lock (_gate)
        {
            if (_disposed) return;
            ulong changes = _engine.ChangeCount;
            try
            {
                if (changes == _savedChanges && !(always && _engine.GetStatus().Plugins.Count > 0)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(_sessionPath)!);
                _engine.SaveSession(_sessionPath);
                _savedChanges = changes;
                Problem = null;
            }
            catch (Exception ex) when (ex is BrackException or IOException or UnauthorizedAccessException)
            {
                Problem = ex.Message;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Saves the rack and unloads the plugins.</summary>
    /// <remarks>Waits for <see cref="Loaded"/>, as <see cref="WaitLoaded"/> does.</remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _loaded.Wait();
        _stopTimer.Dispose();
        _saveTimer.Dispose();
        SaveQuietly(always: true);
        lock (_gate) _disposed = true;
        _engine.Dispose();
        BrackLibrary.Log -= _log.Write;
    }

    /// <summary>Brack's log, kept until someone takes it (<see cref="TakeLog"/>).</summary>
    private sealed class KeptLog
    {
        /// <summary>Enough for loading a large rack; a log nobody takes stops growing here.</summary>
        private const int Limit = 1000;

        private readonly Lock _gate = new();
        private List<(BrackLogLevel Level, string Message)>? _kept = [];
        private Action<BrackLogLevel, string>? _taker;

        public void Write(BrackLogLevel level, string message)
        {
            Action<BrackLogLevel, string> taker;
            lock (_gate)
            {
                if (_taker is null)
                {
                    if (_kept!.Count < Limit) _kept.Add((level, message));
                    return;
                }
                taker = _taker;
            }
            taker(level, message);
        }

        public void HandTo(Action<BrackLogLevel, string> taker)
        {
            lock (_gate)
            {
                // Under the lock, so that nothing written meanwhile comes before what was kept.
                foreach ((BrackLogLevel level, string message) in _kept ?? []) taker(level, message);
                _kept = null;
                _taker = taker;
            }
        }
    }
}
#endif
