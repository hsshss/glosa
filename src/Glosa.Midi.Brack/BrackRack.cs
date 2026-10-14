#if BRACK
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

/// <summary>An instrument in the rack, or an effect in a chain (an instrument's, or the master's).</summary>
/// <param name="Id">Brack's id for it, unique in the rack, effects included.</param>
/// <param name="Name">What it is called, and what an instrument's output devices are named after.</param>
/// <param name="Format">CLAP, VST3 or VST2; empty while loading.</param>
/// <param name="Architecture">The CPU it is built for (x86, x64, arm64), which may not be the player's; empty while loading.</param>
/// <param name="Status">What Brack says of it: why it failed or where it crashed.</param>
/// <param name="NotePorts">How many note ports it takes MIDI on; none until it is loaded, and none for an effect.</param>
/// <param name="Bypass">Whether an effect lets the sound through unprocessed.</param>
/// <param name="Effects">An instrument's effects, in the order they process its main output; none for an effect.</param>
/// <param name="Gain">An instrument's volume, on its sound after its effects, linear (1 = 0 dB); 1 for an effect.</param>
/// <param name="Mute">Whether an instrument is silenced, while it goes on taking MIDI and playing.</param>
public sealed record RackPlugin(string Id, string Name, string Format, string Architecture, RackPluginState State,
                                string Status, bool HasEditor, int NotePorts, bool Bypass,
                                IReadOnlyList<RackPlugin> Effects, float Gain = 1, bool Mute = false);

/// <summary>A session file moved out of the way, and why it would not load.</summary>
public sealed record SetAsideFile(string Path, string Why);

/// <summary>A channel's level.</summary>
/// <param name="Peak">Linear, falling away after the sound.</param>
/// <param name="Clips">Blocks past full scale since the rack was made, or the instrument added; it never goes down.</param>
public readonly record struct ChannelLevel(float Peak, ulong Clips);

/// <summary>
/// The time taken to process a block against the time the block lasts, 1 being all of it: on
/// average, and the most in the last second.
/// </summary>
public readonly record struct ProcessLoad(float Average, float Peak);

/// <summary>An instrument and its effects, as the audio runs.</summary>
/// <param name="Active">Whether it is processing.</param>
/// <param name="Load">The instrument and its effects together.</param>
/// <param name="Output">Its main output's channels, after its effects.</param>
/// <param name="MidiMessages">How many MIDI messages it has taken since it was added.</param>
public sealed record ChainMeters(bool Active, ProcessLoad Load, IReadOnlyList<ChannelLevel> Output, ulong MidiMessages);

/// <summary>What the audio is doing, for meters; all empty while it is stopped.</summary>
/// <param name="Device">The output device it is playing on.</param>
/// <param name="PluginRate">The rate the plugins run at, converted to <paramref name="OutputRate"/> when they differ.</param>
/// <param name="Load">Everything a period takes: the plugins, the master's effects and the conversion.</param>
/// <param name="Overruns">Periods since the device opened that took longer than they last: each can be a dropout.</param>
/// <param name="Output">Each output channel, after the master gain.</param>
/// <param name="MasterInput">The mix of every instrument, before the master's effects.</param>
/// <param name="MasterOutput">The mix after the master's effects, before the master gain.</param>
/// <param name="Instruments">By instrument id.</param>
public sealed record RackMeters(bool Running, string Device, int OutputRate, int Channels, int PluginRate,
                                ProcessLoad Load, ulong Overruns, IReadOnlyList<ChannelLevel> Output,
                                IReadOnlyList<ChannelLevel> MasterInput, IReadOnlyList<ChannelLevel> MasterOutput,
                                ProcessLoad MasterLoad, IReadOnlyDictionary<string, ChainMeters> Instruments)
{
    public static readonly RackMeters Stopped = new(false, string.Empty, 0, 0, 0, default, 0, [], [], [], default,
                                                    new Dictionary<string, ChainMeters>());
}

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

    /// <summary>A new rack's device period, longer than Brack's (IMPLEMENTATION.md, "Brack バックエンド").</summary>
    private static readonly TimeSpan DefaultBuffer = TimeSpan.FromMilliseconds(20);

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

    /// <summary>The instruments and the master's effects, replaced together.</summary>
    private volatile RackContents _contents;
    private bool _disposed;

    private readonly KeptLog _log;

    private BrackRack(string sessionPath, KeptLog log)
    {
        _engine = new BrackEngine();
        _log = log;
        _sessionPath = sessionPath;
        _contents = Listed(sessionPath);
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

    /// <summary>The instruments in rack order, each with its effects.</summary>
    public IReadOnlyList<RackPlugin> Plugins => _contents.Instruments;

    /// <summary>The effects on the mix of every instrument, in the order they process it.</summary>
    public IReadOnlyList<RackPlugin> MasterEffects => _contents.MasterEffects;

    /// <summary>The CPU the master's effects must be built for: Brack's, which is the player's.</summary>
    public static string MasterArchitecture => BrackLibrary.Architecture;

    /// <summary>
    /// Raised on any thread when <see cref="Plugins"/>, <see cref="MasterEffects"/> or
    /// <see cref="Problem"/> changes.
    /// </summary>
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

    /// <summary>The instrument with this id; effects are not looked at.</summary>
    public RackPlugin? Find(string id) => Plugins.FirstOrDefault(plugin => plugin.Id == id);

    /// <summary>The meters as of at most about 50 ms ago, without waiting for a plugin being loaded.</summary>
    public RackMeters Meters()
    {
        if (_disposed) return RackMeters.Stopped;
        EngineStatus status = _engine.GetCachedStatus();
        if (status.Mode == EngineMode.Stopped) return RackMeters.Stopped;

        return new RackMeters(
            true, status.Device, (int)status.OutputSampleRate, (int)status.OutputChannels, (int)status.ProcessSampleRate,
            new ProcessLoad(status.CpuLoad, status.CpuPeakLoad), status.Overruns,
            Levels(status.OutputPeaks, status.OutputClips),
            Levels(status.MasterInputPeaks, status.MasterInputClips),
            Levels(status.MasterOutputPeaks, status.MasterOutputClips),
            new ProcessLoad(status.MasterLoad, status.MasterPeakLoad),
            status.Plugins.ToDictionary(plugin => plugin.Id, plugin => new ChainMeters(
                plugin.Active, new ProcessLoad(plugin.Load, plugin.PeakLoad), MainOutput(plugin), plugin.MidiMessages)));
    }

    private static ChannelLevel[] Levels(IReadOnlyList<float> peaks, IReadOnlyList<ulong> clips)
        => [.. peaks.Select((peak, channel) => new ChannelLevel(peak, channel < clips.Count ? clips[channel] : 0))];

    /// <summary>The levels of an instrument's main output port, the one its effects process.</summary>
    private static ChannelLevel[] MainOutput(PluginStatus plugin)
        => plugin.AudioOutputs.FirstOrDefault(port => port.Main) is { } main ? Levels(main.Peaks, main.Clips) : [];

    /// <summary>The audio output devices, the system's default first.</summary>
    public static IReadOnlyList<AudioDeviceInfo> AudioDevices() => BrackLibrary.ListAudioDevices();

    /// <summary>The audio output device; null or empty for the system's default.</summary>
    /// <remarks>The buffer keeps its length, in frames at the new device's rate.</remarks>
    public string? AudioDevice
    {
        get => _engine.GetConfig().AudioDevice;
        set
        {
            WaitLoaded();
            Run(() =>
            {
                TimeSpan buffer = Buffer;
                _engine.SetConfig(_engine.GetConfig() with
                {
                    AudioDevice = value,
                    BufferFrames = Frames(buffer, OpenRate(value)),
                });
            });
        }
    }

    /// <summary>
    /// The audio device's period, kept in the session in frames: as many as last this long at the
    /// device's rate when set, and as long as they last at its rate now when read.
    /// </summary>
    public TimeSpan Buffer
    {
        get => TimeSpan.FromSeconds((double)_engine.GetConfig().BufferFrames / DeviceRate());
        set
        {
            WaitLoaded();
            Run(() => _engine.SetConfig(BufferOf(value)));
        }
    }

    private BrackConfig BufferOf(TimeSpan buffer) => _engine.GetConfig() with { BufferFrames = Frames(buffer, DeviceRate()) };

    private static uint Frames(TimeSpan buffer, int rate) => (uint)Math.Round(buffer.TotalSeconds * rate);

    /// <summary>The rate the output device runs at, or would if started now.</summary>
    private int DeviceRate()
        => _engine.GetCachedStatus() is { Mode: not EngineMode.Stopped, OutputSampleRate: > 0 and uint running }
            ? (int)running
            : OpenRate(_engine.GetConfig().AudioDevice);

    /// <summary>The rate an output device would run at if opened now; null or empty for the default.</summary>
    private static int OpenRate(string? name)
    {
        try
        {
            return (int)BrackLibrary.AudioDeviceRate(name);
        }
        catch (BrackException)
        {
            return 48000;  // a device that is gone: a common rate
        }
    }

    /// <summary>The rate the plugins run at, converted to the device's; 0 for the device's own. Kept in the session.</summary>
    public int PluginSampleRate
    {
        get => (int)_engine.GetConfig().ProcessSampleRate;
        set
        {
            WaitLoaded();
            Run(() => _engine.SetConfig(_engine.GetConfig() with { ProcessSampleRate = (uint)value }));
        }
    }

    /// <summary>
    /// Whether the session's plugins load one after another rather than all at once; kept in the
    /// session, so it takes effect from the next load (IMPLEMENTATION.md, "Brack バックエンド").
    /// </summary>
    public bool LoadPluginsSerially
    {
        get => _engine.GetConfig().LoadPluginsSerially;
        set
        {
            WaitLoaded();
            Run(() => _engine.SetConfig(_engine.GetConfig() with { LoadPluginsSerially = value }));
        }
    }

    /// <summary>The gain on all the plugins' sound, linear (1 = 0 dB); kept in the session.</summary>
    /// <remarks>Never blocks. Set before the session has loaded, it gives way to the session's.</remarks>
    public float MasterGain
    {
        get => _engine.MasterGain;
        set => _engine.MasterGain = value;
    }

    /// <summary>Silences all the plugins' sound, keeping <see cref="MasterGain"/>, while they play on; kept in the session.</summary>
    /// <remarks>Never blocks. Set before the session has loaded, it gives way to the session's.</remarks>
    public bool MasterMute
    {
        get => _engine.MasterMute;
        set => _engine.MasterMute = value;
    }

    /// <summary>
    /// The instruments and effects in the standard plugin folders. Blocks; files unchanged since
    /// the scan cached in <paramref name="cachePath"/> are not loaded again.
    /// </summary>
    /// <exception cref="BrackException">The scan failed.</exception>
    public static IReadOnlyList<PluginDescription> Scan(string cachePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        return BrackLibrary.ScanPlugins(cachePath);
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

    /// <summary>
    /// Loads an effect into a chain at <paramref name="index"/> among its effects. Blocks; the
    /// chain plays on meanwhile.
    /// </summary>
    /// <param name="ownerId">The instrument; null for the master.</param>
    /// <param name="pluginId">Which plugin in the file; null for the first.</param>
    /// <exception cref="BrackException">
    /// It would not load, it is built for another CPU than its chain, or the instrument is not in
    /// the rack.
    /// </exception>
    public void AddEffect(string? ownerId, int index, string path, string? pluginId = null)
    {
        WaitLoaded();
        Run(() => _engine.AddEffect(ownerId, index, path, pluginId));
    }

    /// <summary>Lets the sound through an effect unprocessed, or processes it again.</summary>
    /// <exception cref="BrackException">It is not an effect in the rack.</exception>
    public void SetBypass(string effectId, bool bypass)
    {
        WaitLoaded();
        Run(() => _engine.SetEffectBypass(effectId, bypass));
    }

    /// <summary>Sets an instrument's volume, kept in the session.</summary>
    /// <remarks>Never blocks.</remarks>
    /// <param name="gain">On its sound after its effects, linear (1 = 0 dB).</param>
    /// <exception cref="BrackException">It is not an instrument in the rack.</exception>
    public void SetGain(string id, float gain)
    {
        _engine.SetPluginGain(id, gain);
        Update(id, plugin => plugin with { Gain = gain });
    }

    /// <summary>Silences an instrument, which goes on taking MIDI and playing, or lets it be heard; kept in the session.</summary>
    /// <remarks>Never blocks.</remarks>
    /// <exception cref="BrackException">It is not an instrument in the rack.</exception>
    public void SetMute(string id, bool mute)
    {
        _engine.SetPluginMute(id, mute);
        Update(id, plugin => plugin with { Mute = mute });
    }

    /// <summary>Removes an instrument with its effects, or an effect.</summary>
    /// <exception cref="BrackException">It is not in the rack.</exception>
    public void Remove(string id)
    {
        WaitLoaded();
        Run(() => _engine.RemovePlugin(id));
    }

    /// <summary>
    /// Loads a crashed or failed plugin again, keeping its state, name and devices; it loads with
    /// the rest of its chain. Blocks.
    /// </summary>
    /// <exception cref="BrackException">It is not in the rack, or would not load.</exception>
    public void Reload(string id)
    {
        WaitLoaded();
        Run(() => _engine.ReloadPlugin(id));
    }

    /// <summary>Renames a plugin, and so an instrument's output devices.</summary>
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

    /// <summary>
    /// Moves an instrument to <paramref name="index"/>, and its devices with it; or an effect to
    /// <paramref name="index"/> among its chain's effects.
    /// </summary>
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
            case MidiSendResult.QueueFull: return "the plugin is not taking messages as fast as they come";
        }
        // Not loaded: Brack's status says why.
        Refresh();
        return Find(id)?.Status ?? "the plugin is not loaded";
    }

    /// <remarks>Never throws: opening an output and closing the player wait for it.</remarks>
    private void Load()
    {
        bool loaded = false;
        if (File.Exists(_sessionPath))
        {
            try
            {
                _engine.LoadSession(_sessionPath);
                loaded = true;
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

        try
        {
            // A player's settings rather than Brack's (IMPLEMENTATION.md, "Brack バックエンド").
            if (!loaded) _engine.SetConfig(BufferOf(DefaultBuffer) with { ResamplerQuality = ResamplerQuality.Ultra });
        }
        catch (BrackException ex)
        {
            Problem = ex.Message;
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
    private static RackContents Listed(string sessionPath)
    {
        try
        {
            SessionContents session = BrackLibrary.DescribeSession(sessionPath);
            return new RackContents([.. session.Plugins.Select(Plugin)], [.. session.MasterEffects.Select(Plugin)]);
        }
        catch (BrackException)
        {
            return RackContents.Empty;
        }

        static RackPlugin Plugin(SessionPlugin plugin) => new(
            plugin.Id, plugin.Name, string.Empty, string.Empty, RackPluginState.Loading, string.Empty, false, 0,
            plugin.Bypass, [.. plugin.Effects.Select(Plugin)]);
    }

    /// <summary>An instrument as changed without a refresh, until the next one.</summary>
    private void Update(string id, Func<RackPlugin, RackPlugin> change)
        => _contents = _contents with { Instruments = [.. _contents.Instruments.Select(plugin => plugin.Id == id ? change(plugin) : plugin)] };

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

        EngineStatus status = _engine.GetStatus();
        _contents = new RackContents([.. status.Plugins.Select(Plugin)], [.. status.MasterEffects.Select(Plugin)]);
        Changed?.Invoke();

        static RackPlugin Plugin(PluginStatus plugin) => new(
            plugin.Id, plugin.Name, plugin.Format.ToString().ToUpperInvariant(), plugin.Architecture,
            plugin.Crashed ? RackPluginState.Crashed
            : plugin.Failed || !plugin.Loaded ? RackPluginState.Failed
            : RackPluginState.Ready,
            plugin.Status, plugin.HasGui, plugin.NotePorts.Count, plugin.Bypass, [.. plugin.Effects.Select(Plugin)],
            plugin.Gain, plugin.Mute);
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
                if (changes == _savedChanges && !(always && _engine.GetStatus() is { } status
                                                  && status.Plugins.Count + status.MasterEffects.Count > 0)) return;
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

    private sealed record RackContents(IReadOnlyList<RackPlugin> Instruments, IReadOnlyList<RackPlugin> MasterEffects)
    {
        public static readonly RackContents Empty = new([], []);
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
