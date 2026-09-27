using System.Reflection;

namespace Glosa.Core.Emulation;

/// <summary>
/// Which program and bank hold a tone, and which programs a drum set, on a Sound Canvas from
/// the SC-55mkII on — map by map, for <see cref="CapitalToneFallback"/> to tell a tone the
/// machine has from one it does not.
/// </summary>
/// <remarks>
/// The tables (<c>SoundCanvasTones.txt</c>) are facts about the machines — a slot is filled
/// or it is not — and the same whoever reads them out.
/// </remarks>
public sealed class SoundCanvasTones
{
    private readonly Dictionary<int, ToneSlots> _maps;

    private SoundCanvasTones(string model, Dictionary<int, ToneSlots> maps)
    {
        Model = model;
        _maps = maps;
        Native = maps.Count == 1 && maps.ContainsKey(0) ? (byte)0 : (byte)maps.Keys.Max();
    }

    /// <summary>The model whose tables these are.</summary>
    public string Model { get; }

    /// <summary>
    /// The map a CC#32 of 0 plays on, as the machine leaves the factory: its own, the last it
    /// has. 0 for a model with one map, which does not read CC#32.
    /// </summary>
    public byte Native { get; }

    /// <summary>
    /// The models, by every name the player may give one: define.yaml's, the DEF's
    /// <c>[moduleindex]</c> and <c>[alias]</c>. The SC-88VL and ST are the SC-88 and the
    /// SC-8820 is the SC-8850 map for map (checked as the table is made); SC-88_2 is the DEF's
    /// SC-88 that is reset slowly.
    /// </summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SC-55mk2"] = "SC-55mk2",
        ["SC-55ST"] = "SC-55mk2",
        ["SC-33"] = "SC-33",
        ["SC-88"] = "SC-88",
        ["SC-88VL"] = "SC-88",
        ["SC-88ST"] = "SC-88",
        ["SC-88_2"] = "SC-88",
        ["SC-88PRO"] = "SC-88PRO",
        ["SC-8820"] = "SC-8850",
        ["SC-8850"] = "SC-8850",
    };

    private static readonly Lazy<Dictionary<string, SoundCanvasTones>> Models = new(Load);

    /// <summary>The tables of a module, or null when it is not a Sound Canvas that needs them.</summary>
    public static SoundCanvasTones? Of(string useModule)
        => Names.TryGetValue(useModule, out string? model) ? Models.Value[model] : null;

    /// <summary>
    /// The map a part plays on: chosen by its CC#32, or TONE MAP-0 NUMBER where that is 0.
    /// Null for a number the machine has no map for.
    /// </summary>
    /// <param name="bankLsb">The part's CC#32 (TONE MAP NUMBER).</param>
    /// <param name="map0">The part's TONE MAP-0 NUMBER, or 0 when it is as the factory set it.</param>
    internal ToneSlots? MapFor(int bankLsb, int map0)
    {
        if (Native == 0) return _maps[0];
        int map = bankLsb != 0 ? bankLsb : map0 != 0 ? map0 : Native;
        return _maps.GetValueOrDefault(map);
    }

    private static Dictionary<string, SoundCanvasTones> Load()
    {
        using Stream stream = typeof(SoundCanvasTones).Assembly
            .GetManifestResourceStream("Glosa.Core.Emulation.SoundCanvasTones.txt")
            ?? throw new InvalidOperationException("SoundCanvasTones.txt is not embedded.");
        using var reader = new StreamReader(stream);
        return Parse(reader);
    }

    /// <summary>Reads the tables' text: <c>[model map]</c> sections of <c>program: banks</c> lines.</summary>
    internal static Dictionary<string, SoundCanvasTones> Parse(TextReader reader)
    {
        var models = new Dictionary<string, Dictionary<int, ToneSlots>>(StringComparer.OrdinalIgnoreCase);
        ToneSlots? slots = null;

        while (reader.ReadLine() is { } raw)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith('['))
            {
                string[] head = line.Trim('[', ']').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int map = head.Length > 1 ? int.Parse(head[1]) : 0;
                if (!models.TryGetValue(head[0], out Dictionary<int, ToneSlots>? maps))
                    models[head[0]] = maps = [];
                maps[map] = slots = new ToneSlots();
                continue;
            }

            if (slots is null) throw new FormatException($"A line before any map: {line}");
            int colon = line.IndexOf(':');
            string key = line[..colon];
            int[] values = [.. line[(colon + 1)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)];

            if (key == "drums")
                foreach (int program in values) slots.AddDrumSet(program);
            else
                foreach (int bank in values) slots.AddTone(int.Parse(key), bank);
        }

        return models.ToDictionary(entry => entry.Key, entry => new SoundCanvasTones(entry.Key, entry.Value),
                                   StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>The filled slots of one map: a bit per bank for each program, a bit per drum set.</summary>
internal sealed class ToneSlots
{
    private readonly UInt128[] _banks = new UInt128[128];
    private UInt128 _drumSets;

    public void AddTone(int program, int bank) => _banks[program] |= UInt128.One << bank;

    public void AddDrumSet(int program) => _drumSets |= UInt128.One << program;

    public bool HasTone(int program, int bank) => (_banks[program] & UInt128.One << bank) != 0;

    public bool HasDrumSet(int program) => (_drumSets & UInt128.One << program) != 0;
}
