namespace Glosa.Core.Emulation;

/// <summary>
/// The ways a module is put back to where a song starts, when there is no DEF.
/// </summary>
public static class InitializeType
{
    public const string GM = "GM";
    public const string GS = "GS";
    /// <summary>GS, on an SC-88 or later: the GS reset and then the system mode set.</summary>
    public const string SC88 = "SC88";
    public const string XG = "XG";
    public const string MT32 = "MT32";

    public static IReadOnlyList<string> All { get; } = [GM, GS, SC88, XG, MT32];

    private const int SettleMs = 200;

    private static readonly byte[] GmSystemOn = [0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7];
    private static readonly byte[] GsReset =
        [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7];
    private static readonly byte[] SystemModeSet1 =
        [0xF0, 0x41, 0x10, 0x42, 0x12, 0x00, 0x00, 0x7F, 0x00, 0x01, 0xF7];
    private static readonly byte[] XgSystemOn = [0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7];

    /// <summary>
    /// What resets a module the <paramref name="type"/> way on each of <paramref name="ports"/>.
    /// </summary>
    /// <remarks>
    /// Each step goes to every port and is then waited for once, rather than port by port:
    /// the machines settle side by side, and the song is not held up once per port.
    /// </remarks>
    public static IReadOnlyList<ScriptAction> InitFor(string type, IReadOnlyList<int> ports)
    {
        var actions = new List<ScriptAction>();
        if (ports.Count == 0) return actions;

        void Send(byte[] message)
        {
            foreach (int port in ports) actions.Add(ScriptAction.Message((ScriptPort)port, message));
            actions.Add(ScriptAction.Wait((ScriptPort)ports[0], SettleMs));
        }

        switch (type.ToUpperInvariant())
        {
            case GM:
                Send(GmSystemOn);
                break;
            case GS:
                Send(GsReset);
                break;
            case SC88:
                Send(GsReset);
                Send(SystemModeSet1);
                break;
            case XG:
                Send(XgSystemOn);
                break;
            case MT32:
                foreach (int port in ports) actions.Add(ScriptAction.Reset((ScriptPort)port, 0));
                break;
        }

        return actions;
    }
}
