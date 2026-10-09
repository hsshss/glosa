using Glosa.Core.Definition;
using Glosa.Core.Emulation;
using Glosa.Core.Playback;
using Glosa.Core.Smf;
using Glosa.Midi;

namespace Glosa.App.Services;

/// <summary>
/// A module's tone map, the ports that lead to it, and whether the song is on two ports and
/// so has both part groups to put on the map.
/// </summary>
public readonly record struct ToneMapChoice(ToneMap Maps, byte Map, IReadOnlyList<int> Ports,
                                            bool BothGroups = false);

/// <summary>
/// Holds the pieces the player is made of and keeps them wired together.
/// </summary>
/// <remarks>
/// The chain is the one the console harness builds:
/// <c>Sequencer → EmulationFilter → [PartSplitter] → CapitalToneFallback → PortSink → IMidiOutput</c>.
/// The filter is always in it, even with no DEF loaded, because the panel state the display
/// reads lives there. The fallback is always in it too, and does nothing until it is given a
/// machine's tables (<see cref="UseFallback"/>). The splitter is in it only for a port map
/// that asks for it (<see cref="UseSplit"/>).
/// </remarks>
public sealed class PlayerService : IDisposable
{
    /// <summary>The device each port goes to now, by port. What the sink reads.</summary>
    private readonly List<IMidiOutput?> _outputs = [];

    /// <summary>
    /// Every device opened since playback last came to rest, each once.
    /// </summary>
    private readonly Dictionary<DeviceName, IMidiOutput> _open = [];

    /// <summary>Devices that would not open, and why; until playback comes to rest.</summary>
    private readonly Dictionary<DeviceName, string> _failed = [];

    /// <summary>
    /// Guards <see cref="_open"/>, <see cref="_failed"/> and <see cref="_openProblems"/>, which
    /// the UI reads while the transport's thread opens devices. Never held while one opens.
    /// </summary>
    private readonly Lock _devices = new();
    private readonly PortSink _ports;

    /// <summary>The ports, behind the Capital Tone Fallback. Kept from song to song.</summary>
    private readonly CapitalToneFallback _fallback;

    /// <summary>
    /// What reaches the machines: <see cref="_fallback"/>, or a <see cref="PartSplitter"/> in
    /// front of it. The emulation layer sends here, and so does the initialisation.
    /// </summary>
    private volatile IEventSink _machines;
    private EmulationFilter _filter;
    private DefDocument? _definition;

    public PlayerService()
    {
        _ports = new PortSink(_outputs);
        _fallback = new CapitalToneFallback(_ports);
        _machines = _fallback;
        _filter = new EmulationFilter(_machines, new EmulationSettings(), new PatchMapSet());
        Options = new PlaybackOptions();
        Sequencer = new Sequencer(_filter, Options, Backends.Timer);
        // Recomposer data has its endless loops played out as it is read, so the setting
        // is taken each time a song is.
        Controller = new PlaybackController(Sequencer, Read, reset: _filter);

        // The controller raises this after it has put the emulation's state back.
        Controller.Rewinding += SendInitAgain;
    }

    /// <summary>The initialisation the emulation in force sends, for sending again.</summary>
    private volatile IReadOnlyList<ScriptAction> _init = [];

    /// <summary>
    /// Sends the module's initialisation again as a song goes back towards its start.
    /// </summary>
    /// <remarks>
    /// On the playback thread, before it replays the start of the song, which is the only
    /// thread allowed to send while a song is going. An output that refuses is treated as
    /// any other send that fails there.
    /// </remarks>
    private void SendInitAgain()
    {
        IEventSink machines = _machines;
        if (machines is PartSplitter splitter) splitter.Reset();
        MidiScript.Run(_init, machines);
    }

    public PlaybackOptions Options { get; }

    /// <summary>Reads a song the way playback will, with the loop setting as it stands.</summary>
    public MidiSequence Read(string path)
        => SmfReader.Read(path, Options.InfiniteLoopRepeatCount);

    /// <summary>Reads a song's summary, with the loop setting as it stands.</summary>
    public SongSummary ReadSummary(string path, int openingMessages)
        => SmfReader.ReadSummary(path, openingMessages, Options.InfiniteLoopRepeatCount);

    public Sequencer Sequencer { get; }

    public PlaybackController Controller { get; }

    public PanelState Panel => _filter.Panel;

    /// <summary>The definition in force, for the module auto-detection to read.</summary>
    public DefDocument? Definition => _definition;

    /// <summary>The module pair in force, for display.</summary>
    public string EmulationLabel { get; private set; } = "(no definition)";

    /// <summary>
    /// What went wrong the last time ports were closed, or null. Worth showing: a port that
    /// will not close is a port the next run cannot open.
    /// </summary>
    public string? LastCloseProblem { get; private set; }

    /// <summary>
    /// A device's notes were all turned off because it may have lost some of what it was
    /// sent (<see cref="IMidiOutput.Silenced"/>). With its name, on the sending thread.
    /// </summary>
    public event Action<string>? OutputSilenced;

    public static IReadOnlyList<MidiDeviceInfo> Devices()
        => Backends.MidiOutputs?.Enumerate() ?? [];

    /// <summary>
    /// Sends each port to a device, opening the ones not open yet and keeping the rest.
    /// </summary>
    /// <remarks>
    /// On the transport's thread, as a song is set up: opening a device can take a while.
    ///
    /// Nothing already open is closed; a device stays open until <see cref="ReleaseDevices"/>.
    ///
    /// A port with no device keeps its place in the list as a null, so the list stays
    /// indexed by port and a gap in the middle does not shift the ports after it.
    ///
    /// A device that will not open leaves its port empty, and the other ports are routed all
    /// the same (<see cref="OpenProblems"/>).
    ///
    /// The sequencer is stopped only when the routing actually changes: the list is what the
    /// playback thread reads.
    /// </remarks>
    public void Route(IReadOnlyList<DeviceName?> devices)
    {
        var problems = new List<string>();
        var routed = new List<IMidiOutput?>(devices.Count);
        for (int port = 0; port < devices.Count; port++)
        {
            if (devices[port] is not { } name)
            {
                routed.Add(null);
                continue;
            }

            IMidiOutput? output = Opened(name, out string? problem);
            if (output is null) problems.Add(string.Format(Strings.PortOpenProblem, PortName(port), name, problem));
            routed.Add(output);
        }

        lock (_devices)
        {
            _openProblems.Clear();
            _openProblems.AddRange(problems);
        }

        if (routed.SequenceEqual(_outputs)) return;

        Sequencer.Stop();
        _outputs.Clear();
        _outputs.AddRange(routed);
    }

    /// <summary>The device by that name, opened now if it was not already.</summary>
    /// <remarks>
    /// Looked up afresh in the list as it is now (<see cref="DeviceName.Find"/>).
    /// </remarks>
    private IMidiOutput? Opened(DeviceName name, out string? problem)
    {
        problem = null;
        IMidiOutput? output;
        lock (_devices)
            if (_open.TryGetValue(name, out output)) return output;

        if (Backends.MidiOutputs is not { } outputs)
        {
            problem = Strings.ProblemNotMidiOutput;
            return null;
        }

        try
        {
            if (name.Find(outputs.Enumerate()) is not { } device)
                throw new MidiDeviceException(Strings.ProblemDeviceNotFound);

            output = outputs.Create(device.Id);
            output.Silenced += () => OutputSilenced?.Invoke(device.Name);
            output.Open();
            lock (_devices)
            {
                _open[name] = output;
                _failed.Remove(name);
            }
            return output;
        }
        catch (MidiDeviceException ex)
        {
            problem = ex.Message;
            lock (_devices) _failed[name] = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Closes every device, for when playback has come to rest.
    /// </summary>
    /// <remarks>
    /// The next song opens what it needs again, which is also how a device unplugged and
    /// plugged back in comes back.
    /// </remarks>
    public void ReleaseDevices()
    {
        Sequencer.Stop();
        RestoreMapsOnTheWayOut();
        CloseDevices();
        lock (_devices) _openProblems.Clear();
    }

    /// <summary>Whether a device is open, would not open, or has not been asked for.</summary>
    public (OutputState State, string? Why) StateOf(DeviceName name)
    {
        lock (_devices)
            return _open.ContainsKey(name) ? (OutputState.Open, null)
                 : _failed.TryGetValue(name, out string? why) ? (OutputState.Failed, why)
                 : (OutputState.Closed, null);
    }

    private readonly List<string> _openProblems = [];

    /// <summary>
    /// Every port that would not open the last time ports were routed, in port order.
    /// </summary>
    /// <remarks>
    /// All of them, not the last one, so they can be fixed in one go.
    /// </remarks>
    public IReadOnlyList<string> OpenProblems
    {
        get
        {
            lock (_devices) return [.. _openProblems];
        }
    }

    /// <summary>The letter a port wears on the port map screen.</summary>
    private static string PortName(int port) => string.Format(Strings.PortName, (char)('A' + port));

    /// <summary>
    /// Takes the definition in, without resolving anything yet.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ApplyEmulation"/> because the target module is not always
    /// known at this point: left to auto-detection, it is settled per song, and the
    /// detection needs the document that is being loaded here.
    /// </remarks>
    public void LoadDefinition(DefDocument definition) => _definition = definition;

    /// <summary>
    /// Lets the definition go. Like loading one, it resolves nothing: the song playing keeps
    /// the emulation it started with, and the next one sets itself up without a DEF.
    /// </summary>
    public void CloseDefinition() => _definition = null;

    /// <summary>
    /// Makes a module's reset the initialisation in force, and the emulation layer the one
    /// that keeps the song on <paramref name="map"/> — for when there is no DEF.
    /// <see cref="SendInit"/> sends the initialisation.
    /// </summary>
    /// <remarks>
    /// <paramref name="map"/> is the tone map the module plays the song on, chosen after the
    /// reset: the reset's own wait is the gap the module needs before it takes more. On a
    /// Roland the song's own program changes are kept on it too, which is the emulation
    /// layer's to do (<see cref="ToneMap.Settings"/>); with no map it is put back to one
    /// that changes nothing, whatever the last song or a DEF left there.
    /// </remarks>
    public void UseInit(IReadOnlyList<ScriptAction> actions, ToneMapChoice? map = null)
    {
        _init = map is { } chosen ? [.. actions, .. chosen.Maps.Select(chosen.Map, chosen.Ports, chosen.BothGroups)] : actions;
        _map = map;

        EmulationSettings settings =
            map is { } choice ? choice.Maps.Settings(choice.Map) ?? new() : new();
        UseFilter(new EmulationFilter(_machines, settings, new PatchMapSet()));
        EmulationLabel = "(no definition)";
    }

    /// <summary>
    /// Puts a part splitter in front of the machines, a new one for the song being set up, or
    /// takes it out. Before the emulation layer is built (<see cref="UseInit"/>,
    /// <see cref="ApplyEmulation"/>), which sends to it.
    /// </summary>
    public void UseSplit(bool split)
    {
        Sequencer.Stop();
        _machines = split ? new PartSplitter(_fallback) : _fallback;
    }

    /// <summary>
    /// Has the Capital Tone Fallback judge program changes by a machine's tables, or stop
    /// when <paramref name="tones"/> is null. Takes effect with the song being set up.
    /// </summary>
    public void UseFallback(SoundCanvasTones? tones)
    {
        Sequencer.Stop();
        _fallback.Tones = tones;
    }

    /// <summary>Puts an emulation layer in the chain, with the sequencer stopped for it.</summary>
    private void UseFilter(EmulationFilter filter)
    {
        Sequencer.Stop();
        _filter = filter;
        Sequencer.Sink = _filter;
        Controller.Reset = _filter;
    }

    /// <summary>The tone map the initialisation in force puts the module on, if it does.</summary>
    private ToneMapChoice? _map;

    /// <summary>
    /// What puts each machine that was moved off its own tone map back on it, by device.
    /// </summary>
    /// <remarks>
    /// Only where the choice outlives resets (<see cref="ToneMap.Lasting"/>, the MU's Voice
    /// Map). Kept by device rather than by port: the ports may be routed elsewhere by the
    /// time it is put back.
    /// </remarks>
    private readonly Dictionary<IMidiOutput, IReadOnlyList<byte[]>> _mapsToRestore = [];

    /// <summary>
    /// Puts back the tone map of every machine moved off its own, except the ones in
    /// <paramref name="keep"/>, which are about to be told which map to use anyway.
    /// </summary>
    /// <exception cref="MidiDeviceException">
    /// A machine would not take it. The others are put back all the same, and this is the
    /// first that refused.
    /// </exception>
    private void RestoreMaps(IReadOnlyCollection<IMidiOutput> keep)
    {
        MidiDeviceException? refused = null;
        lock (_mapsToRestore)
        {
            // Forgotten first: a machine that refuses is not asked again every time.
            KeyValuePair<IMidiOutput, IReadOnlyList<byte[]>>[] pending =
                [.. _mapsToRestore.Where(entry => !keep.Contains(entry.Key) && entry.Key.IsOpen)];
            _mapsToRestore.Clear();

            foreach ((IMidiOutput device, IReadOnlyList<byte[]> messages) in pending)
            {
                try
                {
                    foreach (byte[] message in messages) device.SendLong(message);
                }
                catch (MidiDeviceException ex)
                {
                    refused ??= ex;
                }
            }
        }

        if (refused is not null) throw refused;
    }

    /// <summary>Notes the machines the tone map in force moves off their own.</summary>
    private void RememberMaps(ToneMapChoice? map)
    {
        if (map is not { } chosen || chosen.Map == chosen.Maps.Native) return;

        IReadOnlyList<byte[]> native = chosen.Maps.Restore();
        if (native.Count == 0) return;

        lock (_mapsToRestore)
            foreach (IMidiOutput device in DevicesOn(chosen.Ports))
                _mapsToRestore[device] = native;
    }

    /// <summary>The machines on <paramref name="ports"/>, each once.</summary>
    private IMidiOutput[] DevicesOn(IReadOnlyList<int> ports)
        => [.. ports.Where(port => port >= 0 && port < _outputs.Count)
                    .Select(port => _outputs[port]).OfType<IMidiOutput>().Distinct()];

    /// <summary>
    /// Puts the machines back on their own tone maps, for when playback comes to rest or the
    /// player closes. A machine that has gone is past being put back.
    /// </summary>
    private void RestoreMapsOnTheWayOut()
    {
        try
        {
            RestoreMaps([]);
        }
        catch (MidiDeviceException)
        {
        }
    }

    /// <summary>
    /// Sends the initialisation in force, and says why it did not all go out, or null when
    /// it did.
    /// </summary>
    /// <remarks>
    /// Separate from setting it up, which is done on the UI thread; sending has waits in it
    /// and is done on the transport's thread as the song is set up.
    ///
    /// Past the emulation layer: the initialisation is its own output and must not be run
    /// back through it. Through the part splitter and the Capital Tone Fallback, which have to
    /// see the resets and the map the parts are put on as the machine does. A refusal is
    /// returned rather than thrown: the emulation is in place either way, and the caller
    /// decides what to tell.
    /// </remarks>
    public string? SendInit()
    {
        try
        {
            // A machine the last song moved off its own tone map is put back, unless this
            // song is about to say which map it wants.
            ToneMapChoice? map = _map;
            RestoreMaps(map is { Maps.Lasting: true } chosen ? DevicesOn(chosen.Ports) : []);

            MidiScript.Run(_init, _machines);
            RememberMaps(map);
            return null;
        }
        catch (MidiDeviceException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Builds the emulation for a module pair and puts it in the chain. Its initialisation
    /// becomes the one in force; <see cref="SendInit"/> sends it.
    /// </summary>
    public void ApplyEmulation(string useModule, string targetModule)
    {
        if (_definition is null) return;

        EmulationSetup setup = new EmulationBuilder(_definition)
            .Build(useModule, targetModule, filterSection: "NONE");

        UseFilter(new EmulationFilter(_machines, setup.Settings, setup.Patches));

        EmulationLabel = $"[{setup.Resolution.Key}] {setup.Resolution.Commands}";
        _init = setup.InitActions;
        InitCut = setup.InitCut;
        _map = null;
    }

    /// <summary>
    /// Whether the DEF's initialisation came to more than it may, and was cut short
    /// (<see cref="EmulationSetup.InitCut"/>), when the emulation was last built.
    /// </summary>
    public bool InitCut { get; private set; }

    /// <summary>
    /// Closes every port, letting no single failure strand the rest.
    /// </summary>
    /// <remarks>
    /// A port left open is a port the next run cannot use. What went wrong is kept in
    /// <see cref="LastCloseProblem"/> rather than thrown.
    /// </remarks>
    private void CloseDevices()
    {
        LastCloseProblem = null;

        IMidiOutput[] open;
        lock (_devices)
        {
            open = [.. _open.Values];
            _open.Clear();
            _failed.Clear();
        }

        foreach (IMidiOutput output in open)
        {
            try
            {
                // Closed first, as the setting says; Dispose would reset regardless.
                output.Close(reset: Options.UseMidiOutReset);
                output.Dispose();
            }
            catch (MidiDeviceException ex)
            {
                LastCloseProblem = $"{output.Device.Name}: {ex.Message}";
            }

            if (output.CloseError is { } problem)
                LastCloseProblem = $"{output.Device.Name}: {problem}";
            else if (output.DroppedLongMessages > 0)
                LastCloseProblem =
                    string.Format(Strings.ProblemSysExDropped, output.Device.Name, output.DroppedLongMessages);
        }
        _outputs.Clear();
    }

    public void Dispose()
    {
        // Closed here as well as on the way to rest: quitting mid-song never comes to rest.
        Controller.Dispose();
        Sequencer.Dispose();
        RestoreMapsOnTheWayOut();
        CloseDevices();
    }
}
