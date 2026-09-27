namespace Glosa.Core.Emulation;

/// <summary>
/// The tone maps a module carries of the models before it, and how one is chosen — for when
/// there is no DEF.
/// </summary>
/// <remarks>
/// Roland: the map rides on the bank select LSB (CC#32). While the song plays, the emulation
/// layer puts the map in front of each program change (<see cref="Settings"/>); after the
/// reset every part is also put on the map and its tone picked again (<see cref="Select"/>).
///
/// Yamaha: the Voice Map system parameter (<c>F0 43 1n 49 00 00 12 vv F7</c>, 00 = MU Basic,
/// 01 = MU100 Native), which outlives resets (<see cref="Lasting"/>).
///
/// The device ID is the factory setting, the one the resets in <see cref="InitializeType"/>
/// use: the player has no setting for it.
/// </remarks>
public sealed class ToneMap
{
    /// <summary>
    /// The factory device ID: 17 on a Roland (<c>10H</c>), device number 1 on a Yamaha
    /// (<c>1n</c>, n = 0).
    /// </summary>
    public const byte DeviceId = 0x10;

    private enum Maker { Roland, Yamaha }

    private readonly Maker _maker;

    private ToneMap(Maker maker, byte native)
    {
        _maker = maker;
        Native = native;
    }

    /// <summary>The module's own map.</summary>
    public byte Native { get; }

    /// <summary>
    /// Whether the choice stays in the machine through resets — and so has to be made for
    /// every song, and put back to <see cref="Native"/> before the machine is let go.
    /// </summary>
    public bool Lasting => _maker == Maker.Yamaha;

    private static readonly Dictionary<string, ToneMap> Modules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SC-88"] = new(Maker.Roland, 0x02),
        ["SC-88PRO"] = new(Maker.Roland, 0x03),
        ["SC-8820"] = new(Maker.Roland, 0x04),
        ["SC-8850"] = new(Maker.Roland, 0x04),
        ["MU100"] = new(Maker.Yamaha, 0x01),
        ["MU128"] = new(Maker.Yamaha, 0x01),
        ["MU1000"] = new(Maker.Yamaha, 0x01),
        ["MU2000"] = new(Maker.Yamaha, 0x01),
        ["MU500"] = new(Maker.Yamaha, 0x01),
    };

    /// <summary>The map each earlier model's songs want, by the model they were written for.</summary>
    private static readonly Dictionary<string, byte> Wanted = new(StringComparer.OrdinalIgnoreCase)
    {
        // Roland: the bank select LSB.
        ["SC-55"] = 0x01,
        ["SC-55mk2"] = 0x01,
        ["SC-33"] = 0x01,
        ["SC-88"] = 0x02,
        ["SC-88PRO"] = 0x03,
        ["SC-8820"] = 0x04,
        ["SC-8850"] = 0x04,
        // Yamaha: VOICE MAP. There is no map after the MU100's.
        ["MU50"] = 0x00,
        ["MU80"] = 0x00,
        ["MU90"] = 0x00,
        ["MU100"] = 0x01,
        ["MU128"] = 0x01,
        ["MU1000"] = 0x01,
        ["MU2000"] = 0x01,
        ["MU500"] = 0x01,
    };

    private static readonly HashSet<string> RolandModels = new(StringComparer.OrdinalIgnoreCase)
        { "SC-55", "SC-55mk2", "SC-33", "SC-88", "SC-88PRO", "SC-8820", "SC-8850" };

    /// <summary>The SC-55 map, whose songs never meant anything by a bank select LSB.</summary>
    private const byte Sc55Map = 0x01;

    /// <summary>
    /// The maps of a module, or null when it has none to choose from.
    /// </summary>
    /// <remarks>
    /// A Yamaha reset the GS way is in TG300B mode, which has no Voice Map.
    /// </remarks>
    public static ToneMap? Of(string useModule, string? initializeType)
    {
        if (!Modules.TryGetValue(useModule, out ToneMap? map)) return null;
        if (map._maker == Maker.Yamaha
            && string.Equals(initializeType, InitializeType.GS, StringComparison.OrdinalIgnoreCase))
            return null;
        return map;
    }

    /// <summary>
    /// The map to play a song on: the earlier model's, when this module carries it, and the
    /// module's own otherwise.
    /// </summary>
    /// <remarks>
    /// Native for a song written for this model or a later one, for one made to a standard
    /// (GM, GS, XG) rather than a model, and for one written for another maker's machine.
    /// </remarks>
    public byte For(string targetModule)
    {
        if (!Wanted.TryGetValue(targetModule, out byte wanted)) return Native;
        if (RolandModels.Contains(targetModule) != (_maker == Maker.Roland)) return Native;
        return Math.Min(wanted, Native);
    }

    /// <summary>
    /// Whether putting the module on <paramref name="map"/> takes anything: always where the
    /// choice lasts, since the last song's may still stand; otherwise only off the native map.
    /// </summary>
    public bool Needs(byte map) => Lasting || map != Native;

    /// <summary>The name a map goes by in the manuals, for the status line.</summary>
    public string NameOf(byte map) => (_maker, map) switch
    {
        (Maker.Roland, 0x01) => "SC-55 MAP",
        (Maker.Roland, 0x02) => "SC-88 MAP",
        (Maker.Roland, 0x03) => "SC-88Pro MAP",
        (Maker.Roland, 0x04) => "SC-8850 MAP",
        (Maker.Yamaha, 0x00) => "MU Basic",
        (Maker.Yamaha, 0x01) => "MU100 Native",
        _ => $"{map:X2}",
    };

    /// <summary>
    /// What is sent after the reset to put <paramref name="ports"/> on <paramref name="map"/>.
    /// </summary>
    /// <remarks>
    /// Roland: what the DEF's <c>C:55MAP W:20 C:PC0 W:20</c> does, as exclusives on every
    /// part: TONE MAP NUMBER (<c>40 4x 00</c>), then TONE NUMBER (<c>40 1x 00</c>, CC#0 and
    /// program, both 0). With <paramref name="bothGroups"/> (a song on two ports) the other
    /// part group is reached at <c>50 xx xx</c> too.
    /// </remarks>
    public IReadOnlyList<ScriptAction> Select(byte map, IReadOnlyList<int> ports, bool bothGroups = false)
    {
        var actions = new List<ScriptAction>();
        if (ports.Count == 0) return actions;

        if (_maker == Maker.Yamaha)
        {
            foreach (int port in ports)
                actions.Add(ScriptAction.Message((ScriptPort)port, VoiceMap(map)));
            return actions;
        }

        byte[] groups = bothGroups ? [0x40, 0x50] : [0x40];

        void EveryPart(Func<byte, int, byte[]> message)
        {
            foreach (int port in ports)
                foreach (byte group in groups)
                    for (int block = 0; block < 16; block++)
                        actions.Add(ScriptAction.Message((ScriptPort)port, message(group, block)));
            actions.Add(ScriptAction.Wait((ScriptPort)ports[0], 20));
        }

        EveryPart((group, block) => RolandSet(group, (byte)(0x40 | block), 0x00, map));
        EveryPart((group, block) => RolandSet(group, (byte)(0x10 | block), 0x00, 0x00, 0x00));
        return actions;
    }

    /// <summary>
    /// A Roland DT1: the checksum is what brings the address and data to a multiple of 128.
    /// </summary>
    private static byte[] RolandSet(byte high, byte mid, byte low, params byte[] data)
    {
        int sum = high + mid + low;
        foreach (byte b in data) sum += b;
        return [0xF0, 0x41, DeviceId, 0x42, 0x12, high, mid, low, .. data, (byte)((0x80 - (sum & 0x7F)) & 0x7F), 0xF7];
    }

    /// <summary>
    /// What the emulation layer does to the song to keep it on <paramref name="map"/>, or
    /// null when it does nothing.
    /// </summary>
    /// <remarks>
    /// The DEF's map sections: a program change is preceded by CC#32 = the map unless the
    /// part has chosen one (<c>MapSelect</c>), and a CC#32 of 0 becomes the map
    /// (<c>DefaultBankSelectLSB</c>). A song for the SC-55 has its own CC#32 dropped as well
    /// (<c>[55Map]</c>'s <c>DisableBankSelectLSB</c>): the SC-55 has one map and ignores
    /// CC#32, so such a song never meant to pick one. A song for a later model keeps its own.
    /// </remarks>
    public EmulationSettings? Settings(byte map)
    {
        if (_maker == Maker.Yamaha || map == Native) return null;

        return new EmulationSettings
        {
            MapSelect = map,
            DefaultBankSelectLsb = map,
            DisableBankSelectLsb = map == Sc55Map,
        };
    }

    /// <summary>What puts the machine back on its own map, where the choice lasts.</summary>
    public IReadOnlyList<byte[]> Restore() => Lasting ? [VoiceMap(Native)] : [];

    private static byte[] VoiceMap(byte map) => [0xF0, 0x43, DeviceId, 0x49, 0x00, 0x00, 0x12, map, 0xF7];
}
