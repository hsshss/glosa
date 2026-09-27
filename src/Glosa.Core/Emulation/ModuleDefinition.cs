using System.Text;
using System.Text.RegularExpressions;
using Glosa.Core.Playback;
using Glosa.Core.Smf;

namespace Glosa.Core.Emulation;

/// <summary>
/// The player's own module definition, <c>define.yaml</c>: which target modules there are,
/// and the patterns that pick them out of the words around a song.
/// </summary>
/// <remarks>
/// The names are the DEF's own, so a map or a song set to one means the same module
/// whichever of define.yaml and the DEF decided it, and a DEF loaded later builds the
/// emulation for it as it stands.
///
/// There is no counterpart to <c>[alias]</c>. A spelling of a model the list does not name,
/// such as SC-8820, is a pattern of the module it stands for (SC-8850).
///
/// The text is folded with NFKC first, so ＳＣ−８８ＰＲＯ arrives as SC-88PRO, and a pattern
/// says which parts are optional instead of listing each combination.
///
/// Nothing is cut into words: where a pattern may start and end is the pattern's to say,
/// with lookarounds such as <c>(?&lt;![0-9A-Za-z])</c>.
///
/// Between models the place decides: the one named first, or last (<see cref="NameDetection"/>).
/// A module marked as a fallback (the standards) is only looked for when no model is named
/// anywhere, and among those the list decides: a title tends to open with what it is broadly
/// compatible with, and "GM/GS SC-88Pro compatible" is no GM song.
///
/// When the words name nothing, the song itself is asked
/// (<see cref="ScanData(MidiSequence)"/>). The data names only the family of machine, never a
/// model: every model in a family understands its predecessors' messages.
/// </remarks>
public sealed class ModuleDefinition
{
    public const string FileName = "define.yaml";

    /// <summary>The version of the format this player reads (<see cref="Version"/>).</summary>
    public const int FormatVersion = 1;

    /// <summary>The definition read in place of the shipped one, from the settings folder.</summary>
    public const string OverrideFileName = "define.override.yaml";

    /// <summary>A copy of the shipped file beside the override, to be renamed into it.</summary>
    public const string SampleFileName = OverrideFileName + ".sample";

    /// <summary>
    /// Copies the shipped file into <paramref name="folder"/> as the sample, over any earlier one.
    /// </summary>
    public static void WriteSample(string shipped, string folder)
    {
        Directory.CreateDirectory(folder);
        File.Copy(shipped, Path.Combine(folder, SampleFileName), overwrite: true);
    }

    /// <summary>
    /// The modules detection answers with whatever the file lists: THRU when nothing is
    /// found, and the families the song's data names (<see cref="DataRules"/>), with how
    /// each is reset.
    /// </summary>
    private static readonly (string Name, string? Type)[] OwnAnswers =
    [
        ("THRU", null),
        ("CM-64", InitializeType.MT32),
        (InitializeType.GS, InitializeType.GS),
        (InitializeType.XG, InitializeType.XG),
    ];

    /// <summary>
    /// This definition with the modules detection answers with added where the file left
    /// them out: THRU first, the families last.
    /// </summary>
    /// <remarks>One the file lists keeps its place and what the file says of it.</remarks>
    public ModuleDefinition WithOwnAnswers()
    {
        bool Has(string name) => Modules.Contains(name, StringComparer.OrdinalIgnoreCase);

        (string Name, string? Type)[] missing = [.. OwnAnswers.Where(a => !Has(a.Name))];
        if (missing.Length == 0) return this;

        var modules = new List<string>(Modules);
        var types = new Dictionary<string, string>(_initializeTypes, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string? type) in missing)
        {
            if (name == "THRU") modules.Insert(0, name);
            else modules.Add(name);
            if (type is not null) types[name] = type;
        }

        return new ModuleDefinition(Version, modules, _rules, _dataRules, _messages,
                                    DocumentLength, types, Problems);
    }

    /// <summary>The override in <paramref name="folder"/>, or null when there is none.</summary>
    public static string? FindOverride(string folder)
    {
        string path = Path.Combine(folder, OverrideFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// How long one pattern may take over one text. The patterns are written by hand, and
    /// a careless one must not be able to hang the player.
    /// </summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>Dashes NFKC leaves as they are but that are plainly meant as a hyphen.</summary>
    private const string HyphenLike = "\u2010\u2011\u2012\u2013\u2212";

    /// <summary>
    /// How many messages from the start of a song the data rules look at when the file does
    /// not say. The setup a song sends comes before its first notes, or among them.
    /// </summary>
    public const int DefaultMessages = 1000;

    /// <summary>
    /// How many characters of the attached document are searched when the file does not say.
    /// </summary>
    public const int DefaultDocumentLength = 2048;

    private readonly Rule[] _rules;
    private readonly DataRule[] _dataRules;
    private readonly int _messages;
    private readonly Dictionary<string, string> _initializeTypes;

    private ModuleDefinition(int version, IReadOnlyList<string> modules, Rule[] rules,
                             DataRule[] dataRules, int messages, int documentLength,
                             Dictionary<string, string> initializeTypes, IReadOnlyList<string> problems)
    {
        Version = version;
        Modules = modules;
        _initializeTypes = initializeTypes;
        _rules = rules;
        _dataRules = dataRules;
        _messages = messages;
        DocumentLength = documentLength;
        Problems = problems;
    }

    /// <summary>No modules, nothing detected.</summary>
    public static ModuleDefinition Empty { get; } =
        new(0, [], [], [], DefaultMessages, DefaultDocumentLength, new(StringComparer.OrdinalIgnoreCase), []);

    /// <summary>
    /// The version of the format the file is written in; 0 when the file does not say.
    /// </summary>
    /// <remarks>
    /// Raised only when the format stops being compatible (IMPLEMENTATION.md).
    /// </remarks>
    public int Version { get; }

    /// <summary>
    /// How many characters of the attached document are searched (<see cref="NameDetection"/>).
    /// </summary>
    public int DocumentLength { get; }

    /// <summary>The target modules, in the order the file lists them.</summary>
    public IReadOnlyList<string> Modules { get; }

    /// <summary>
    /// What was wrong with the file: patterns that are not valid expressions, and the like.
    /// </summary>
    /// <remarks>
    /// Such a pattern is left out rather than failing the whole file, so one typo costs one
    /// spelling rather than every detection.
    /// </remarks>
    public IReadOnlyList<string> Problems { get; }

    public static ModuleDefinition Load(string path)
        => Parse(File.ReadAllText(path, Encoding.UTF8));

    public static ModuleDefinition Parse(string yaml)
    {
        DefineFile file = YamlFile.Parse<DefineFile>(yaml);
        var problems = new List<string>();

        var modules = new List<string>();
        var rules = new List<Rule>();
        var initializeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModuleEntry? entry in file.Modules ?? [])
        {
            if (entry?.Name?.Trim() is not { Length: > 0 } name)
            {
                problems.Add(Strings.DefineModuleUnnamed);
                continue;
            }

            modules.Add(name);

            if (entry.InitializeType?.Trim() is { Length: > 0 } type)
            {
                if (InitializeType.All.FirstOrDefault(
                        t => t.Equals(type, StringComparison.OrdinalIgnoreCase)) is { } known)
                    initializeTypes[name] = known;
                else
                    problems.Add(string.Format(Strings.DefineUnknownInitializeType, name, type, string.Join(" / ", InitializeType.All)));
            }

            foreach (string? pattern in entry.Patterns ?? [])
            {
                if (string.IsNullOrEmpty(pattern)) continue;

                // Multiline, so ^ and $ stand at the start and end of each source's line as
                // well as of the whole text (NameDetection).
                try
                {
                    rules.Add(new Rule(name, new Regex(pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline,
                        MatchTimeout),
                        entry.Fallback));
                }
                catch (ArgumentException ex)
                {
                    problems.Add($"{name}: {pattern} — {ex.Message}");
                }
            }
        }

        int messages = file.DataDetection?.MaxMessages is > 0 and int m ? m : DefaultMessages;
        int documentLength = file.NameDetection?.MaxDocumentLength is > 0 and int d
            ? d : DefaultDocumentLength;
        return new ModuleDefinition(file.Version, modules, [.. rules], DataRules, messages,
                                    documentLength, initializeTypes, problems);
    }

    /// <summary>
    /// How <paramref name="module"/> is reset when there is no DEF (<see cref="InitializeType"/>),
    /// or null when the file gives no way — THRU, or a module the list does not have.
    /// </summary>
    public string? InitializeTypeOf(string module)
        => _initializeTypes.TryGetValue(module, out string? type) ? type : null;

    /// <summary>
    /// Runs the patterns over text the caller has already put together.
    /// </summary>
    /// <remarks>
    /// Every match of every pattern is found, and one that overlaps a match of a pattern
    /// listed above it is dropped. Of what is left, first the models: the one named first in
    /// the text, or the one named last. Only when none is named anywhere, the fallbacks: the
    /// first in the list with a match left, with its first (or last) match as the reason.
    ///
    /// No two matches that are left overlap, so no two start at the same place, and the
    /// place alone settles which comes first.
    /// </remarks>
    public ModuleDetection? Scan(string text, MatchPosition position = MatchPosition.First)
    {
        if (_rules.Length == 0) return null;

        List<(Rule Rule, Match Match)> kept = Kept(Fold(text));

        if (Pick(kept.Where(found => !found.Rule.Fallback), position) is { } model)
            return new ModuleDetection(model.Rule.Module, ModuleSource.Keyword, model.Match.Value);

        // The rules are in list order, and so are the matches kept from them.
        if (kept.FirstOrDefault(found => found.Rule.Fallback) is { Rule: { } standard })
        {
            (Rule _, Match reason) = Pick(
                kept.Where(found => found.Rule.Fallback && found.Rule.Module == standard.Module),
                position)!.Value;
            return new ModuleDetection(standard.Module, ModuleSource.Keyword, reason.Value);
        }

        return null;
    }

    /// <summary>
    /// Every match of every rule, rule by rule in list order, less those that overlap a match
    /// kept from a rule above.
    /// </summary>
    private List<(Rule Rule, Match Match)> Kept(string text)
    {
        var kept = new List<(Rule Rule, Match Match)>();
        var taken = new bool[text.Length];
        foreach (Rule rule in _rules)
        {
            foreach (Match match in Matches(rule.Pattern, text))
            {
                Span<bool> span = taken.AsSpan(match.Index, match.Length);
                if (span.Contains(true)) continue;

                span.Fill(true);
                kept.Add((rule, match));
            }
        }
        return kept;
    }

    /// <summary>The match that appears first, or last.</summary>
    private static (Rule Rule, Match Match)? Pick(
        IEnumerable<(Rule Rule, Match Match)> found, MatchPosition position)
    {
        (Rule Rule, Match Match)? best = null;
        foreach ((Rule Rule, Match Match) one in found)
        {
            if (best is not { } so
                || (position == MatchPosition.Last ? one.Match.Index > so.Match.Index
                                                   : one.Match.Index < so.Match.Index))
                best = one;
        }
        return best;
    }

    /// <summary>
    /// Looks at the song itself: the first of the rules built in, in their order, that an
    /// exclusive message near the start of the song matches.
    /// </summary>
    /// <remarks>
    /// Only the first so many messages are looked at — channel and exclusive messages, in
    /// the order they play; meta events are not messages and are not counted. A rule listed
    /// earlier wins wherever its message is among them, so a song that sets up both an XG
    /// and a GS machine is an XG song: XG is listed first.
    /// </remarks>
    public ModuleDetection? ScanData(MidiSequence sequence)
        => ScanData(sequence.Events, sequence.Payload);

    /// <summary>
    /// <see cref="ScanData(MidiSequence)"/> on a summary read for <see cref="DataMessages"/> messages.
    /// </summary>
    public ModuleDetection? ScanData(SongSummary song) => ScanData(song.Opening, song.Payload);

    /// <summary>How many messages from the start of a song the data rules look at.</summary>
    public int DataMessages => _messages;

    private ModuleDetection? ScanData(MidiEvent[] events, byte[] payload)
    {
        if (_dataRules.Length == 0) return null;

        int best = _dataRules.Length;
        string matched = string.Empty;
        int seen = 0;
        foreach (MidiEvent e in events)
        {
            if (e.Kind is not (MidiEventKind.Channel or MidiEventKind.SysEx)) continue;
            if (++seen > _messages) break;
            if (e.Kind != MidiEventKind.SysEx) continue;

            ReadOnlySpan<byte> message = payload.AsSpan(e.DataOffset, e.DataLength);
            for (int i = 0; i < best; i++)
            {
                if (!_dataRules[i].Pattern.Matches(message)) continue;
                best = i;
                matched = Hex(message);
                break;
            }

            // Nothing can beat the first rule, so there is no point reading on.
            if (best == 0) break;
        }

        return best < _dataRules.Length
            ? new ModuleDetection(_dataRules[best].Module, ModuleSource.Data, matched)
            : null;
    }

    /// <summary>The start of a message, for showing why.</summary>
    private static string Hex(ReadOnlySpan<byte> message)
    {
        const int Shown = 8;
        string head = Convert.ToHexString(message[..Math.Min(Shown, message.Length)]);
        string spaced = string.Join(' ', Enumerable.Range(0, head.Length / 2)
                                                   .Select(i => head.Substring(i * 2, 2)));
        return message.Length > Shown ? spaced + " …" : spaced;
    }

    /// <summary>
    /// The module in the list that <paramref name="name"/> stands for: itself when the list
    /// has it, otherwise whichever module's patterns pick it out, otherwise itself unchanged.
    /// </summary>
    /// <remarks>
    /// For names that come from somewhere other than these patterns — the DEF's
    /// <c>[keyword]</c> answers SC-8820 or CM-32L, which the list folds into SC-8850 and
    /// CM-64. Reading the name as a text of one word finds the module the same patterns
    /// would have found in a title, so no second table of aliases has to be kept in step.
    /// </remarks>
    public string ListName(string name)
    {
        foreach (string module in Modules)
            if (string.Equals(module, name, StringComparison.OrdinalIgnoreCase)) return module;

        return Scan(name)?.Module ?? name;
    }

    /// <summary>
    /// Every match that has something in it; a pattern that can match nothing proves nothing.
    /// </summary>
    /// <remarks>
    /// A pattern that runs out of time is taken as matching nothing at all, so a careless
    /// one costs its own spellings and the rest of the rules still get their say.
    /// </remarks>
    private static List<Match> Matches(Regex pattern, string text)
    {
        var found = new List<Match>();
        try
        {
            for (Match m = pattern.Match(text); m.Success; m = m.NextMatch())
                if (m.Length > 0) found.Add(m);
            return found;
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
    }

    /// <summary>
    /// Full-width to half-width, Ⅱ to II, every kind of dash to a hyphen, and every line
    /// break to <c>\n</c>.
    /// </summary>
    private static string Fold(string text)
    {
        text = text.ReplaceLineEndings("\n");

        string folded;
        try
        {
            folded = text.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // Not valid UTF-16: left as it stands.
            folded = text;
        }

        if (folded.AsSpan().IndexOfAny(HyphenLike) < 0) return folded;

        char[] chars = folded.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (HyphenLike.Contains(chars[i])) chars[i] = '-';
        return new string(chars);
    }

    private sealed record Rule(string Module, Regex Pattern, bool Fallback);

    private sealed record DataRule(string Module, ExclusivePattern Pattern);

    /// <summary>
    /// What a song's own messages say, the rule listed first winning
    /// (<see cref="ScanData(MidiSequence)"/>).
    /// </summary>
    /// <remarks>
    /// Built in rather than read from the file, as there is nothing to adjust: each is the
    /// maker and model bytes of a family's parameter messages, answering with the family.
    /// </remarks>
    private static readonly DataRule[] DataRules =
    [
        // XG parameter change (XG System On and others)
        new("XG", ExclusivePattern.Parse("F0 43 1? 4C")),
        // GS parameter set (GS Reset, the SC-88's mode setting and others)
        new("GS", ExclusivePattern.Parse("F0 41 1? 42 12")),
        // MT-32 / CM family (LA synthesis) parameter set
        new("CM-64", ExclusivePattern.Parse("F0 41 1? 16 12")),
    ];

    /// <summary>
    /// The start of an exclusive message, as hex bytes; <c>?</c> stands for any one digit.
    /// </summary>
    /// <remarks>
    /// A prefix rather than the whole message, because what tells the family apart is the
    /// maker and model bytes at the front: <c>F0 41 1? 42 12</c> is any GS parameter, from
    /// whichever device ID.
    /// </remarks>
    private sealed class ExclusivePattern
    {
        private readonly byte[] _value;
        private readonly byte[] _mask;

        private ExclusivePattern(byte[] value, byte[] mask)
        {
            _value = value;
            _mask = mask;
        }

        public bool Matches(ReadOnlySpan<byte> message)
        {
            if (message.Length < _value.Length) return false;
            for (int i = 0; i < _value.Length; i++)
                if ((message[i] & _mask[i]) != _value[i]) return false;
            return true;
        }

        public static ExclusivePattern Parse(string text)
        {
            string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var value = new byte[tokens.Length];
            var mask = new byte[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                foreach (char c in tokens[i])
                {
                    value[i] = (byte)(value[i] << 4 | (c == '?' ? 0 : Convert.ToByte(c.ToString(), 16)));
                    mask[i] = (byte)(mask[i] << 4 | (c == '?' ? 0 : 0xF));
                }
            }
            return new ExclusivePattern(value, mask);
        }
    }

    // The shape of the file. Public setters because the YAML reader fills them in. Nullable
    // where a key left without a value reads as null.

    private sealed class DefineFile
    {
        public int Version { get; set; }

        public List<ModuleEntry?>? Modules { get; set; }

        public NameDetectionEntry? NameDetection { get; set; }

        public DataDetectionEntry? DataDetection { get; set; }
    }

    private sealed class ModuleEntry
    {
        public string? Name { get; set; }

        public List<string?>? Patterns { get; set; }

        public bool Fallback { get; set; }

        public string? InitializeType { get; set; }
    }

    private sealed class NameDetectionEntry
    {
        public int MaxDocumentLength { get; set; }
    }

    private sealed class DataDetectionEntry
    {
        public int MaxMessages { get; set; }
    }
}
