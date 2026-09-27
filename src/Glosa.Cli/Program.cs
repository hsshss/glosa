using System.Diagnostics;
using Glosa.Core.Playback;
using Glosa.Core.Smf;
using Glosa.Core.Text;
using Glosa.Midi;

Cp932.Register();
// Console defaults to the OEM code page on Windows; force UTF-8 so Japanese
// titles survive redirection.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }

if (args.Length == 0) { Usage(); return 1; }

string command = args[0].ToLowerInvariant();
try
{
    return command switch
    {
        "devices" => Devices(),
        "info" => Info(args),
        "play" => Play(args),
        "detect" => Detect(args),
        "jitter" => Glosa.Cli.Jitter.Run(args),
        _ => Usage(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static int Usage()
{
    Console.WriteLine("""
        glosa - developer harness

          glosa devices
          glosa info <file.mid>
          glosa play <file.mid> [options]
          glosa detect <file.DEF | define.yaml> <file.mid>... [options]
                                                  auto-detect each song's module
          glosa jitter [file.mid] [options]       how close the sequencer keeps to the clock

        play options:
          --device <id>     output device id (default: first)
          --devices <list>  comma-separated ids or names, one per port A,B,C...
          --dry-run         do not open a device; measure timing only
          --init-only       run the DEF initialisation and stop, for comparing it
          --seconds <n>     stop after n seconds
          --no-reset        do not call midiOutReset on stop
          --rate <bytes>    transfer rate cap per port, bytes/sec (0 = unlimited)
          --priority <p>    low | normal | high (default: high)
          --loop-repeat <n> times an endless loop plays (default: 2)
          --def <file>      emulation definition file
          --use <module>    output module (needs --def, except for --ctf)
          --target <module> module the data was written for (needs --def)
          --ctf             Capital Tone Fallback for the --use module (SC-55mk2 and later SCs)

        detect options (define.yaml only):
          --sources <list>  the words searched, in order: folder, name, title, document
                            (default: title,name,folder)
          --last            the model named last wins, not the one named first

        jitter options:
          (no file)         a built-in metronome, one note every --spacing ms
          --spacing <ms>    metronome spacing (default: 10)
          --seconds <n>     metronome length (default: 20)
          --no-timer        do not raise the system timer resolution, to see what it buys
          --def/--use/--target   put the emulation layer in the chain, as playback has it
        """);
    return 1;
}

/// <summary>
/// Runs the module auto-detection over songs and prints what it decided and why.
/// </summary>
/// <remarks>
/// With a DEF, its <c>[keyword]</c> decides, and the answer is put through its <c>[alias]</c>.
/// With a <c>define.yaml</c>, its patterns do, over the words <c>--sources</c> names in the
/// order it names them; the song's data is asked when they say nothing.
/// </remarks>
static int Detect(string[] args)
{
    const string usage = "usage: glosa detect <file.DEF | define.yaml> <file.mid>... "
                       + "[--sources folder,name,title,document] [--last]";

    var songs = new List<string>();
    Glosa.Core.Emulation.NameDetection how = Glosa.Core.Emulation.NameDetection.Default;
    for (int i = 2; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--last":
                how = how with { Position = Glosa.Core.Emulation.MatchPosition.Last };
                break;
            case "--sources" when i + 1 < args.Length:
                how = how with { Sources = [.. args[++i].Split(',').Select(SourceNamed)] };
                break;
            default:
                songs.Add(args[i]);
                break;
        }
    }

    if (args.Length < 2 || songs.Count == 0)
    {
        Console.Error.WriteLine(usage);
        return 1;
    }

    Func<string, string, string, MidiSequence, (Glosa.Core.Emulation.ModuleDetection Found, string Resolved)> detect;
    if (Path.GetExtension(args[1]).Equals(".yaml", StringComparison.OrdinalIgnoreCase))
    {
        var define = Glosa.Core.Emulation.ModuleDefinition.Load(args[1]);
        foreach (string problem in define.Problems) Console.Error.WriteLine($"define.yaml: {problem}");

        detect = (path, title, document, sequence) =>
        {
            var found = Glosa.Core.Emulation.ModuleDetector.Detect(
                define, path, title, document, sequence: sequence, how: how);
            return (found, found.Module);
        };
    }
    else
    {
        var def = Glosa.Core.Definition.DefDocument.Load(args[1]);
        Glosa.Core.Definition.DefSection? alias = def["alias"];

        detect = (path, title, document, _) =>
        {
            var found = Glosa.Core.Emulation.ModuleDetector.Detect(def, path, title, document);
            return (found, Glosa.Core.Emulation.EmulationResolver.Alias(alias, found.Module));
        };
    }

    foreach (string path in songs)
    {
        MidiSequence sequence;
        try { sequence = SmfReader.Read(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Console.WriteLine($"{Path.GetFileName(path),-40} (could not load: {ex.Message})");
            continue;
        }

        // The document is whatever text file sits beside the song under the same name.
        string documentPath = Path.ChangeExtension(path, ".txt");
        string document = Glosa.Core.Archives.SongStore.Exists(documentPath)
            ? DocumentText.Decode(Glosa.Core.Archives.SongStore.ReadAllBytes(documentPath))
            : string.Empty;

        (var found, string resolved) = detect(Path.GetFullPath(path), sequence.Title, document, sequence);
        string arrow = resolved == found.Module ? "" : $" -> {resolved}";
        Console.WriteLine($"{Path.GetFileName(path),-40} {found}{arrow}");
    }

    return 0;

    static Glosa.Core.Emulation.DetectionSource SourceNamed(string name) => name.Trim().ToLowerInvariant() switch
    {
        "folder" => Glosa.Core.Emulation.DetectionSource.FolderPath,
        "name" => Glosa.Core.Emulation.DetectionSource.FileName,
        "title" => Glosa.Core.Emulation.DetectionSource.Title,
        "document" => Glosa.Core.Emulation.DetectionSource.Document,
        _ => throw new ArgumentException($"unknown source: {name} (folder, name, title, document)"),
    };
}

static IEnumerable<int> PortsUsed(MidiSequence sequence)
    => sequence.Events.Select(e => e.Port).Distinct().Order();

static IMidiOutputFactory Factory() => Glosa.Cli.Backends.MidiOutputs
    ?? throw new PlatformNotSupportedException("No MIDI backend on this system: there are WinMM (Windows) and CoreMIDI (macOS).");

static int Devices()
{
    IMidiOutputFactory factory = Factory();
    Console.WriteLine($"backend: {factory.BackendName}");
    IReadOnlyList<MidiDeviceInfo> devices = factory.Enumerate();
    if (devices.Count == 0) { Console.WriteLine("  (no output devices)"); return 0; }
    foreach (MidiDeviceInfo d in devices)
        Console.WriteLine($"  [{d.Id}] {d.Name}");
    return 0;
}

static int Info(string[] args)
{
    if (args.Length < 2) return Usage();
    MidiSequence seq = SmfReader.Read(args[1]);

    Console.WriteLine($"format      : {seq.Format}");
    Console.WriteLine($"tracks      : {seq.TrackCount}");
    Console.WriteLine($"division    : {seq.Division}"
        + ((seq.Division & 0x8000) != 0 ? " (SMPTE)" : " ticks/quarter"));
    Console.WriteLine($"events      : {seq.Events.Length}");
    Console.WriteLine($"duration    : {seq.Duration:mm\\:ss\\.fff}");
    // The ports actually addressed, not the range: a song can name 0 and 2 and skip 1.
    Console.WriteLine($"ports used  : {string.Join(", ", PortsUsed(seq))}");
    Console.WriteLine($"title       : {seq.Title}");

    int channel = 0, sysex = 0, meta = 0;
    foreach (MidiEvent e in seq.Events)
    {
        switch (e.Kind)
        {
            case MidiEventKind.Channel: channel++; break;
            case MidiEventKind.SysEx: sysex++; break;
            case MidiEventKind.Meta: meta++; break;
        }
    }
    Console.WriteLine($"  channel={channel} sysex={sysex} meta={meta}");

    if (seq.Texts.Count > 0)
    {
        Console.WriteLine("texts:");
        foreach (string t in seq.Texts.Take(10))
            Console.WriteLine($"  {t}");
        if (seq.Texts.Count > 10) Console.WriteLine($"  ... (+{seq.Texts.Count - 10})");
    }

    foreach (MidiEvent e in seq.Events.Where(x => x.Kind == MidiEventKind.SysEx).Take(5))
    {
        string hex = Convert.ToHexString(seq.GetData(e).ToArray());
        Console.WriteLine($"sysex @{e.TimeUs / 1000}ms port {e.Port}: {hex}");
    }
    return 0;
}

static int Play(string[] args)
{
    if (args.Length < 2) return Usage();
    string path = args[1];

    string? deviceId = null;
    string[]? deviceList = null;
    bool dryRun = false;
    bool initOnly = false;
    double seconds = 0;
    string? defPath = null;
    string useModule = "THRU";
    string targetModule = "THRU";
    bool fallback = false;
    var options = new PlaybackOptions();

    for (int i = 2; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--device": deviceId = args[++i]; break;
            case "--ctf": fallback = true; break;
            case "--devices":
                deviceList = args[++i].Split(',', StringSplitOptions.TrimEntries);
                break;
            case "--dry-run": dryRun = true; break;
            case "--init-only": initOnly = true; break;
            case "--seconds": seconds = double.Parse(args[++i]); break;
            case "--no-reset": options.UseMidiOutReset = false; break;
            case "--rate": options.TransferRateBytesPerSecond = int.Parse(args[++i]); break;
            case "--priority":
                options.Priority = Enum.Parse<PlaybackPriority>(args[++i], ignoreCase: true);
                break;
            case "--loop-repeat":
                options.InfiniteLoopRepeatCount = int.Parse(args[++i]);
                break;
            case "--def": defPath = args[++i]; break;
            case "--use": useModule = args[++i]; break;
            case "--target": targetModule = args[++i]; break;
            default: Console.Error.WriteLine($"unknown option: {args[i]}"); return Usage();
        }
    }

    MidiSequence seq = SmfReader.Read(path, options.InfiniteLoopRepeatCount);
    Console.WriteLine($"{Path.GetFileName(path)}  {seq.Duration:mm\\:ss}  "
        + $"{seq.Events.Length} events  ports {string.Join(",", PortsUsed(seq))}");

    var outputs = new List<IMidiOutput?>();
    if (dryRun)
    {
        outputs.Add(new NullMidiOutput());
        outputs[0]!.Open();
        Console.WriteLine("dry run: no device opened");
    }
    else
    {
        IMidiOutputFactory factory = Factory();
        IReadOnlyList<MidiDeviceInfo> devices = factory.Enumerate();
        if (devices.Count == 0) { Console.Error.WriteLine("no MIDI output devices"); return 1; }

        // One device per port, A first. An empty entry leaves that port with nothing on it,
        // as the player holds a gap.
        foreach (string id in deviceList ?? [deviceId ?? devices[0].Id])
        {
            char letter = (char)('A' + outputs.Count);
            if (id.Length == 0)
            {
                outputs.Add(null);
                Console.WriteLine($"port {letter}: (none)");
                continue;
            }

            IMidiOutput output = factory.Create(id);
            output.Open();
            outputs.Add(output);
            Console.WriteLine($"port {letter}: [{output.Device.Id}] {output.Device.Name}");
        }
    }

    // Right before the ports, so it counts what reaches them after the emulation layer.
    var probe = new TimingProbe(new PortSink(outputs));
    IEventSink sink = probe;
    if (fallback)
    {
        // In front of the ports, behind the emulation layer and its initialisation, as the
        // player has it.
        Glosa.Core.Emulation.SoundCanvasTones? tones =
            Glosa.Core.Emulation.SoundCanvasTones.Of(useModule);
        Console.WriteLine(tones is null
            ? $"capital tone fallback: {useModule} has no tables, nothing done"
            : $"capital tone fallback: {tones.Model}");
        sink = new Glosa.Core.Emulation.CapitalToneFallback(sink) { Tones = tones };
    }
    if (defPath is not null)
    {
        var def = Glosa.Core.Definition.DefDocument.Load(defPath);
        Glosa.Core.Emulation.EmulationSetup setup =
            new Glosa.Core.Emulation.EmulationBuilder(def)
                .Build(useModule, targetModule, filterSection: "NONE");

        Console.WriteLine($"emulation: [{setup.Resolution.Key}] {setup.Resolution.Commands}");
        Console.WriteLine($"  patches melodic={setup.Patches.Melodic.Count} "
            + $"drum={setup.Patches.Drum.Count}");

        var emulation = new Glosa.Core.Emulation.EmulationFilter(
            sink, setup.Settings, setup.Patches);
        Glosa.Core.Emulation.MidiScript.Run(setup.InitActions, sink);
        sink = emulation;
    }

    if (initOnly)
    {
        // The initialisation on its own, so it can be looked at apart from the song.
        Console.WriteLine("init only");
        foreach (IMidiOutput? o in outputs) o?.Dispose();
        return 0;
    }

    using var sequencer = new Sequencer(sink, options, Glosa.Cli.Backends.Timer);
    sequencer.Load(seq);

    var done = new ManualResetEventSlim(false);
    sequencer.Finished += () => done.Set();

    var wall = Stopwatch.StartNew();
    sequencer.Play();

    TimeSpan limit = seconds > 0
        ? TimeSpan.FromSeconds(seconds)
        : seq.Duration + TimeSpan.FromSeconds(2);
    done.Wait(limit);
    TimeSpan reached = sequencer.Position;   // Stop() rewinds, so read it first
    sequencer.Stop();
    wall.Stop();

    if (options.UseMidiOutReset)
        foreach (IMidiOutput? o in outputs) o?.Reset();
    foreach (IMidiOutput? o in outputs) o?.Dispose();

    Console.WriteLine($"stopped at {reached:mm\\:ss\\.fff} "
        + $"(wall {wall.Elapsed:mm\\:ss\\.fff})");
    probe.Report();
    return 0;
}


/// <summary>Counts the short and long messages sent.</summary>
internal sealed class TimingProbe(IEventSink inner) : IEventSink
{
    private readonly IEventSink _inner = inner;
    private long _short, _long;

    public void SendShort(int port, uint packedMessage)
    {
        _short++;
        _inner.SendShort(port, packedMessage);
    }

    public void SendLong(int port, ReadOnlySpan<byte> sysEx)
    {
        _long++;
        _inner.SendLong(port, sysEx);
    }

    public void Report() => Console.WriteLine($"sent: {_short} short, {_long} sysex");
}
