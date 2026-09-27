using System.Diagnostics;
using Glosa.Core.Playback;
using Glosa.Core.Smf;

namespace Glosa.Cli;

/// <summary>
/// Measures when the sequencer actually hands each event over, against when the data says.
/// </summary>
/// <remarks>
/// The probe sits where the sequencer lets go, so no device, driver or cable is measured.
/// Work downstream of it still shows, as it delays the loop that dispatches the next event;
/// <c>--def</c> puts the emulation layer in the chain for that.
/// </remarks>
internal static class Jitter
{
    /// <summary>Ticks per quarter note, with a tempo that makes one tick one millisecond.</summary>
    private const int Division = 480;

    private const int TempoUsPerQuarter = 480_000;

    /// <summary>Records when each event was handed over, then passes it on.</summary>
    private sealed class Probe(IEventSink? inner) : IEventSink
    {
        private readonly IEventSink? _inner = inner;

        public List<long> Stamps { get; } = [];

        public int Limit { get; set; } = int.MaxValue;

        public void SendShort(int port, uint packedMessage)
        {
            if (Stamps.Count < Limit) Stamps.Add(Stopwatch.GetTimestamp());
            _inner?.SendShort(port, packedMessage);
        }

        public void SendLong(int port, ReadOnlySpan<byte> sysEx)
        {
            if (Stamps.Count < Limit) Stamps.Add(Stopwatch.GetTimestamp());
            _inner?.SendLong(port, sysEx);
        }
    }

    public static int Run(string[] args)
    {
        string? path = null;
        int spacingMs = 10;
        int seconds = 20;
        bool noTimer = false;
        string? defPath = null;
        string useModule = "THRU";
        string targetModule = "THRU";

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--spacing": spacingMs = int.Parse(args[++i]); break;
                case "--seconds": seconds = int.Parse(args[++i]); break;
                case "--no-timer": noTimer = true; break;
                case "--def": defPath = args[++i]; break;
                case "--use": useModule = args[++i]; break;
                case "--target": targetModule = args[++i]; break;
                default:
                    if (args[i].StartsWith('-')) { Console.Error.WriteLine($"unknown option: {args[i]}"); return 1; }
                    path = args[i];
                    break;
            }
        }

        if (spacingMs < 1) { Console.Error.WriteLine("--spacing must be at least 1"); return 1; }

        MidiSequence sequence = path is null
            ? SmfReader.Read(Metronome(spacingMs, seconds))
            : SmfReader.Read(path);

        int scheduled = sequence.Events.Count(
            e => e.Kind is MidiEventKind.Channel or MidiEventKind.SysEx);
        if (scheduled < 2) { Console.Error.WriteLine("nothing to measure"); return 1; }

        long[] due = [.. sequence.Events
            .Where(e => e.Kind is MidiEventKind.Channel or MidiEventKind.SysEx)
            .Select(e => e.TimeUs)];

        IEventSink? downstream = null;
        string chain = "sequencer only";
        if (defPath is not null)
        {
            Glosa.Core.Emulation.EmulationSetup setup =
                new Glosa.Core.Emulation.EmulationBuilder(
                    Glosa.Core.Definition.DefDocument.Load(defPath))
                    .Build(useModule, targetModule, filterSection: "NONE");

            downstream = new Glosa.Core.Emulation.EmulationFilter(
                new NullSink(), setup.Settings, setup.Patches);
            chain = $"through the emulation [{setup.Resolution.Key}]";
        }

        var probe = new Probe(downstream) { Limit = scheduled };
        var options = new PlaybackOptions { SendAllNotesOffOnStop = false };

        using var sequencer = noTimer
            ? new Sequencer(probe, options)
            : new Sequencer(probe, options, Backends.Timer);

        sequencer.Load(sequence);
        var finished = new ManualResetEventSlim(false);
        sequencer.Finished += () => finished.Set();

        sequencer.Play();
        finished.Wait(TimeSpan.FromSeconds(sequence.DurationUs / 1_000_000.0 + 5));
        sequencer.Stop();

        return Report(probe.Stamps, due, noTimer, chain,
                      path is null ? $"metronome {spacingMs}ms x {seconds}s" : Path.GetFileName(path));
    }

    /// <summary>A sink that takes everything and does nothing, to end the chain.</summary>
    private sealed class NullSink : IEventSink
    {
        public void SendShort(int port, uint packedMessage) { }

        public void SendLong(int port, ReadOnlySpan<byte> sysEx) { }
    }

    /// <summary>
    /// Writes the numbers out.
    /// </summary>
    /// <remarks>
    /// The errors are taken around their own median, not the first event: that one is late
    /// by the playback thread's start-up, once, so it is reported on its own and left out.
    /// </remarks>
    private static int Report(List<long> stamps, long[] due, bool noTimer, string chain, string what)
    {
        int n = Math.Min(stamps.Count, due.Length);
        if (n < 2) { Console.Error.WriteLine($"only {n} event(s) arrived"); return 1; }

        double tickUs = 1_000_000.0 / Stopwatch.Frequency;
        double[] raw = [.. Enumerable.Range(0, n)
            .Select(i => (stamps[i] - stamps[0]) * tickUs - (due[i] - due[0]))];

        double offset = Median(raw);
        double[] error = [.. raw.Select(r => r - offset)];
        double[] steady = [.. error.Skip(1).Select(Math.Abs).Order()];

        int tenth = Math.Max(1, raw.Length / 10);
        double walk = Median(raw[^tenth..]) - Median(raw[..tenth]);
        double span = (due[n - 1] - due[0]) / 1e6;

        Console.WriteLine($"what        : {what}");
        Console.WriteLine($"chain       : {chain}");
        Console.WriteLine($"timer       : {(noTimer || Backends.Timer is NullPlatformTimer ? "none" : "timeBeginPeriod(1)")}");
        Console.WriteLine($"clock       : {Stopwatch.Frequency / 1e6:0.###} MHz ({tickUs:0.####} us per tick)");
        Console.WriteLine($"events      : {n} over {span:0.0} s");
        Console.WriteLine($"first event : {error[0]:0} us late (thread start, once; left out below)");
        Console.WriteLine($"|error| p50 : {Pick(steady, 0.50):0.0} us");
        Console.WriteLine($"|error| p95 : {Pick(steady, 0.95):0.0} us");
        Console.WriteLine($"|error| p99 : {Pick(steady, 0.99):0.0} us");
        Console.WriteLine($"|error| max : {steady[^1]:0.0} us");
        Console.WriteLine($"under 1 ms  : {steady.Count(e => e < 1000) * 100.0 / steady.Length:0.00} %");
        Console.WriteLine($"drift       : {walk:0.0} us over the run"
            + (span > 0 ? $" ({walk / span:0.00} us per second)" : string.Empty));
        return 0;

        static double Median(double[] v) { double[] c = [.. v.Order()]; return c[c.Length / 2]; }

        static double Pick(double[] sorted, double q)
            => sorted[(int)Math.Min(sorted.Length - 1, q * sorted.Length)];
    }

    /// <summary>
    /// An SMF of one note every <paramref name="spacingMs"/> milliseconds, with one tick one
    /// millisecond so that no rounding stands between the schedule and the measurement.
    /// </summary>
    private static byte[] Metronome(int spacingMs, int seconds)
    {
        int count = Math.Max(2, seconds * 1000 / spacingMs);
        var body = new List<byte>();

        body.AddRange(Delta(0));
        body.AddRange([0xFF, 0x51, 0x03,
            unchecked((byte)(TempoUsPerQuarter >> 16)),
            unchecked((byte)(TempoUsPerQuarter >> 8)),
            unchecked((byte)TempoUsPerQuarter)]);

        for (int i = 0; i < count; i++)
        {
            body.AddRange(Delta(i == 0 ? 0 : spacingMs));
            body.AddRange([0x90, 0x3C, 0x40]);
        }

        body.AddRange(Delta(0));
        body.AddRange([0xFF, 0x2F, 0x00]);

        var file = new List<byte>();
        file.AddRange("MThd"u8);
        file.AddRange(BigEndian(6));
        file.AddRange([0x00, 0x00, 0x00, 0x01, Division >> 8, unchecked((byte)Division)]);
        file.AddRange("MTrk"u8);
        file.AddRange(BigEndian(body.Count));
        file.AddRange(body);
        return [.. file];

        static byte[] BigEndian(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

        static byte[] Delta(int value)
        {
            var bytes = new List<byte> { (byte)(value & 0x7F) };
            for (value >>= 7; value > 0; value >>= 7) bytes.Add((byte)((value & 0x7F) | 0x80));
            bytes.Reverse();
            return [.. bytes];
        }
    }
}
