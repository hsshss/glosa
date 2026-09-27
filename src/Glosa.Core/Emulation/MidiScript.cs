using Glosa.Core.Definition;

namespace Glosa.Core.Emulation;

/// <summary>
/// Destination of a <c>MIDI*</c> key. A is 0, B is 1, C is 2.
/// </summary>
/// <remarks>
/// The plain <c>MIDI</c> key uses <see cref="All"/>, expanded over every port at send time,
/// although the shipped DEF's comment describes it as "A and B".
/// </remarks>
public enum ScriptPort { All = -1, A = 0, B = 1, C = 2 }

public enum ScriptActionKind
{
    /// <summary>Send <see cref="ScriptAction.Bytes"/> to <see cref="ScriptAction.Port"/>.</summary>
    Message,
    /// <summary>Pause for <see cref="ScriptAction.WaitMs"/> milliseconds.</summary>
    Wait,
    /// <summary>
    /// An <c>R:n</c> reset: every channel's controllers put back (<see cref="MidiScript.ResetMessages"/>).
    /// 2 sets the channels up for CM-32L emulation instead; any other number is the plain reset.
    /// </summary>
    Reset,
    /// <summary>
    /// A <c>C:</c> template to repeat on all 16 channels. <see cref="ScriptAction.Bytes"/>
    /// holds the message as written, for channel 1.
    /// </summary>
    ChannelBroadcast,
}

public readonly record struct ScriptAction(
    ScriptActionKind Kind, ScriptPort Port, byte[] Bytes, int WaitMs, int ResetKind)
{
    public static ScriptAction Message(ScriptPort port, byte[] bytes)
        => new(ScriptActionKind.Message, port, bytes, 0, 0);

    public static ScriptAction Wait(ScriptPort port, int ms)
        => new(ScriptActionKind.Wait, port, [], ms, 0);

    public static ScriptAction Reset(ScriptPort port, int kind)
        => new(ScriptActionKind.Reset, port, [], 0, kind);

    public static ScriptAction ChannelBroadcast(ScriptPort port, byte[] template)
        => new(ScriptActionKind.ChannelBroadcast, port, template, 0, 0);
}

/// <summary>
/// Expands the little language used by the <c>MIDI</c>, <c>MIDI_A</c>, <c>MIDI_B</c> and
/// <c>MIDI_C</c> keys.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>nn</c> — a decimal byte.</item>
/// <item><c>$nn</c> — a hexadecimal byte.</item>
/// <item><c>M:name</c> — splices in a <c>[midimessage]</c> entry.</item>
/// <item><c>C:name</c> — sends that entry on all 16 channels, replacing the low nibble
/// of its first byte, so the definition is written for channel 1. Emitted as a single
/// <see cref="ScriptActionKind.ChannelBroadcast"/>, expanded later with ports on the outside
/// and channels on the inside.</item>
/// <item><c>R:n</c> — a reset action.</item>
/// <item><c>W:n</c> — waits n milliseconds, up to <see cref="MaxWaitMs"/>.</item>
/// </list>
/// Byte tokens accumulate into one message; anything else flushes what has accumulated.
/// </remarks>
public static class MidiScript
{
    private const int MaxExpansionDepth = 8;

    /// <summary>
    /// How much one expansion may produce, in <see cref="Cost"/> units — roughly bytes.
    /// </summary>
    /// <remarks>
    /// The depth limit alone does not bound it: an entry that splices another several times
    /// over grows by that factor at every level, and a few hundred bytes of DEF would come
    /// to gigabytes. A real setup is a few kilobytes.
    /// </remarks>
    public const int MaxCost = 1 << 20;

    /// <summary>
    /// The longest one <c>W:</c> waits, in milliseconds.
    /// </summary>
    /// <remarks>
    /// A module wants a few hundred after a reset at most. The wait blocks the thread that
    /// sends the song, so a mistyped one would leave stop and the next song unanswered.
    /// </remarks>
    public const int MaxWaitMs = 2000;

    /// <summary>
    /// Plays a script's actions into a sink, honouring the waits between them.
    /// </summary>
    /// <remarks>
    /// Blocks for the waits, so this belongs on a worker rather than a UI thread. Short
    /// messages go out packed and SysEx as a buffer, because that is the split the output
    /// API makes.
    /// </remarks>
    public static void Run(IReadOnlyList<ScriptAction> actions, Playback.IEventSink sink)
    {
        foreach (ScriptAction action in actions)
        {
            switch (action.Kind)
            {
                case ScriptActionKind.Message when action.Bytes.Length == 0:
                    break;
                case ScriptActionKind.Message when action.Bytes[0] == 0xF0:
                    sink.SendLong((int)action.Port, action.Bytes);
                    break;
                case ScriptActionKind.Message:
                    uint packed = action.Bytes[0];
                    if (action.Bytes.Length > 1) packed |= (uint)action.Bytes[1] << 8;
                    if (action.Bytes.Length > 2) packed |= (uint)action.Bytes[2] << 16;
                    sink.SendShort((int)action.Port, packed);
                    break;
                case ScriptActionKind.Wait:
                    Thread.Sleep(action.WaitMs);
                    break;
                case ScriptActionKind.Reset:
                    foreach (uint message in ResetMessages(action.ResetKind))
                        sink.SendShort((int)action.Port, message);
                    break;
            }
        }
    }

    /// <summary>
    /// What an <c>R:n</c> sends, channel by channel.
    /// </summary>
    /// <remarks>
    /// The DEF's own comment has <c>R:1</c> putting back "every track and every parameter"
    /// and <c>R:2</c> starting CM-32L emulation on an SC-55; <c>R:0</c> is not described,
    /// and is the same as <c>R:1</c>. <c>R:2</c> widens the bend range to an octave, gives
    /// the reverb 64, and puts each channel on the MT-32's own sound and pan, from the SC's
    /// CM banks.
    ///
    /// Everything goes out in order. TMIDI sends the <c>R:2</c> bank LSB outside the queue
    /// the rest goes through, so there it can overtake them.
    /// </remarks>
    public static IEnumerable<uint> ResetMessages(int kind)
    {
        bool cm = kind == 2;
        for (int channel = 0; channel < 16; channel++)
        {
            uint cc = (uint)(0xB0 | channel);
            uint Cc(int number, int value) => cc | (uint)number << 8 | (uint)value << 16;

            yield return Cc(0x78, 0x00);                  // all sound off
            yield return Cc(0x79, 0x00);                  // reset all controllers
            yield return Cc(0x65, 0x00);                  // RPN 0: bend range
            yield return Cc(0x64, 0x00);
            yield return Cc(0x06, cm ? 12 : 2);
            yield return Cc(0x65, 0x00);                  // RPN 1: fine tune, centred
            yield return Cc(0x64, 0x01);
            yield return Cc(0x06, 0x40);
            yield return Cc(0x26, 0x00);
            yield return Cc(0x65, 0x00);                  // RPN 2: coarse tune, centred
            yield return Cc(0x64, 0x02);
            yield return Cc(0x06, 0x40);
            yield return Cc(0x26, 0x00);
            yield return Cc(0x64, 0x7F);                  // RPN closed
            yield return Cc(0x65, 0x7F);
            yield return Cc(0x01, 0x00);                  // modulation
            yield return Cc(0x5D, 0x00);                  // chorus
            yield return Cc(0x5B, cm ? 0x40 : 0x00);      // reverb
            yield return Cc(0x07, 100);                   // volume
            yield return Cc(0x0B, 127);                   // expression
            yield return (uint)(0xE0 | channel) | 0x00u << 8 | 0x40u << 16;   // bend centred

            if (cm)
            {
                yield return Cc(0x0A, CmPan[channel]);
                yield return Cc(0x20, 0x01);
                yield return Cc(0x00, channel < 10 ? 0x7F : 0x7E);
                yield return (uint)(0xC0 | channel) | (uint)CmProgram[channel] << 8;
            }
            else
            {
                yield return Cc(0x0A, 0x40);              // pan centred
                yield return Cc(0x20, 0x00);              // bank LSB, MSB
                yield return Cc(0x00, 0x00);
            }
        }
    }

    /// <summary>
    /// The pan <c>R:2</c> gives each channel: the part settings of the SC's CM-64 sound map,
    /// as the SC-88Pro Owner's Manual lists them ("Selecting the CM-64 sound map", p.114),
    /// with L<i>n</i> as 64 − <i>n</i> and R<i>n</i> as 64 + <i>n</i>.
    /// </summary>
    private static ReadOnlySpan<byte> CmPan =>
        [64, 54, 54, 54, 54, 18, 91, 1, 127, 64, 64, 81, 64, 99, 27, 45];

    /// <summary>
    /// The program <c>R:2</c> gives each channel, from 0: the tones of the same table, whose
    /// instrument numbers count from 1. The CM-64/32L drum set is set 128.
    /// </summary>
    private static ReadOnlySpan<byte> CmProgram =>
        [0, 68, 48, 95, 78, 41, 3, 110, 122, 127, 27, 29, 0, 37, 13, 46];

    public static IReadOnlyList<ScriptAction> Expand(
        string script, ScriptPort port, DefSection? messages)
    {
        int budget = MaxCost;
        return Expand(script, port, messages, ref budget);
    }

    /// <summary>
    /// Expands a script on what is left of <paramref name="budget"/>, taking from it what the
    /// actions cost (<see cref="Cost"/>).
    /// </summary>
    /// <remarks>
    /// Stops at the first action the budget cannot pay for, and leaves it below zero, so a
    /// caller expanding several scripts on one budget can tell that one was cut short.
    /// </remarks>
    public static IReadOnlyList<ScriptAction> Expand(
        string script, ScriptPort port, DefSection? messages, ref int budget)
    {
        var expansion = new Expansion(messages, budget);
        expansion.Into(script, port, depth: 0);
        budget = expansion.Left;
        return expansion.Actions;
    }

    /// <summary>What an action counts against the budget: its bytes, as many times as it sends them.</summary>
    /// <remarks>
    /// A splice costs one even when it adds nothing, so a chain of empty ones is bounded too.
    /// </remarks>
    public static int Cost(ScriptAction action) => action.Kind switch
    {
        ScriptActionKind.Message => 1 + action.Bytes.Length,
        ScriptActionKind.ChannelBroadcast => 16 * (1 + action.Bytes.Length),
        ScriptActionKind.Reset => 16 * 30,
        _ => 1,
    };

    /// <summary>One expansion's output and what is left of its budget.</summary>
    private sealed class Expansion(DefSection? messages, int budget)
    {
        public readonly List<ScriptAction> Actions = [];

        public int Left = budget;

        /// <summary>Whether something has been refused, after which nothing more is taken.</summary>
        public bool Spent => Left < 0;

        public void Add(ScriptAction action)
        {
            if (Pay(Cost(action))) Actions.Add(action);
        }

        public bool Pay(int cost)
        {
            if (Spent) return false;
            if (cost > Left) { Left = -1; return false; }
            Left -= cost;
            return true;
        }

        public void Into(string script, ScriptPort port, int depth)
            => ExpandInto(script, port, messages, this, depth);
    }

    /// <summary>
    /// Walks the script one character at a time, the way TMIDI does.
    /// </summary>
    /// <remarks>
    /// This is a scanner rather than a tokeniser on purpose. Only the space is a separator:
    /// any other character that is neither a byte nor a directive ends the message being
    /// built and is stepped over, so a tab between two bytes splits them into two messages
    /// instead of joining them. A directive is recognised from its first letter alone and
    /// two characters are then skipped unconditionally, which is why <c>W200</c> waits 0ms
    /// and <c>Rfoo</c> is a reset.
    /// </remarks>
    private static void ExpandInto(
        string script, ScriptPort port, DefSection? messages,
        Expansion actions, int depth)
    {
        if (depth > MaxExpansionDepth) return;

        int at = 0;
        while (at < script.Length && !actions.Spent)
        {
            var pending = new List<byte>();
            at = ReadBytes(script, at, pending);

            // A directive keeps its position; anything else is consumed here.
            char next = at < script.Length ? char.ToUpperInvariant(script[at]) : '\0';
            if (next is not ('M' or 'C' or 'R' or 'W')) at++;

            if (pending.Count > 0) actions.Add(ScriptAction.Message(port, [.. pending]));

            at = ReadDirectives(script, at, port, messages, actions, depth);
        }
    }

    /// <summary>
    /// Runs the directives from <paramref name="at"/> on, stopping at the first character
    /// that does not start one.
    /// </summary>
    private static int ReadDirectives(
        string script, int at, ScriptPort port, DefSection? messages,
        Expansion actions, int depth)
    {
        while (at < script.Length && !actions.Spent)
        {
            switch (char.ToUpperInvariant(script[at]))
            {
                case 'C':
                    at = ReadName(script, at, out string broadcast);
                    var template = new List<byte>();
                    ReadBytes(messages?.Get(broadcast) ?? string.Empty, 0, template);
                    if (template.Count > 0)
                        actions.Add(ScriptAction.ChannelBroadcast(port, [.. template]));
                    break;

                case 'M':
                    at = ReadName(script, at, out string spliced);
                    string? inner = messages?.Get(spliced);
                    if (inner is not null && actions.Pay(1))
                        ExpandInto(inner, port, messages, actions, depth + 1);
                    break;

                case 'R':
                    at = ReadNumber(script, at, out int kind);
                    actions.Add(ScriptAction.Reset(port, kind));
                    break;

                case 'W':
                    at = ReadNumber(script, at, out int ms);
                    if (ms > 0) actions.Add(ScriptAction.Wait(port, Math.Min(ms, MaxWaitMs)));
                    break;

                default:
                    return at;
            }
        }
        return at;
    }

    /// <summary>
    /// Reads the run of bytes starting at <paramref name="at"/>, stopping at the first
    /// character that is neither a byte nor a space.
    /// </summary>
    private static int ReadBytes(string script, int at, List<byte> into)
    {
        while (at < script.Length)
        {
            char c = script[at];
            if (c == '$')
            {
                if (into.Count < 0x7FF)
                {
                    into.Add((byte)(Hex(script, at + 1) << 4 | Hex(script, at + 2)));
                }
                at += 3;
            }
            else if (c == ' ')
            {
                at++;
            }
            else if (c is >= '0' and <= '9')
            {
                int start = at;
                while (at < script.Length && script[at] is >= '0' and <= '9') at++;
                if (into.Count < 0x800) into.Add((byte)Digits(script, start, at));
            }
            else
            {
                break;
            }
        }
        return at;
    }

    /// <summary>Value of a digit run, saturating rather than overflowing.</summary>
    private static int Digits(string script, int start, int end)
    {
        long value = 0;
        for (int i = start; i < end && value <= int.MaxValue; i++) value = value * 10 + (script[i] - '0');
        return (int)Math.Min(value, int.MaxValue);
    }

    private static int Hex(string script, int at)
    {
        if (at >= script.Length) return 0;
        char c = char.ToUpperInvariant(script[at]);
        if (c is >= '0' and <= '9') return c - '0';
        if (c is >= 'A' and <= 'F') return c - 'A' + 10;
        return 0;
    }

    /// <summary>Reads a <c>[midimessage]</c> name: two characters skipped, then up to 31
    /// more until a space.</summary>
    private static int ReadName(string script, int at, out string name)
    {
        at = Math.Min(at + 2, script.Length);
        int start = at;
        while (at < script.Length && at - start < 0x1F && script[at] != ' ') at++;
        name = script[start..at];
        return at;
    }

    /// <summary>Reads a directive's argument: two characters skipped, then <c>atoi</c>.</summary>
    private static int ReadNumber(string script, int at, out int value)
    {
        at = Math.Min(at + 2, script.Length);
        int start = at;
        while (at < script.Length && script[at] is >= '0' and <= '9') at++;
        value = Digits(script, start, at);
        return at;
    }
}
