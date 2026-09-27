using Glosa.Core.Definition;
using Glosa.Core.Playback;

namespace Glosa.Core.Emulation;

/// <summary>Everything a resolved output-module / data-module pair produces.</summary>
public sealed class EmulationSetup
{
    internal EmulationSetup(
        ConvIndexResult resolution,
        EmulationSettings settings,
        PatchMapSet patches,
        IReadOnlyList<ScriptAction> initActions,
        bool initCut)
    {
        Resolution = resolution;
        Settings = settings;
        Patches = patches;
        InitActions = initActions;
        InitCut = initCut;
    }

    public ConvIndexResult Resolution { get; }

    public EmulationSettings Settings { get; }

    public PatchMapSet Patches { get; }

    /// <summary>Messages, waits and resets to run before playback starts, in order.</summary>
    public IReadOnlyList<ScriptAction> InitActions { get; }

    /// <summary>
    /// Whether the scripts came to more than <see cref="MidiScript.MaxCost"/>, and
    /// <see cref="InitActions"/> stops where they did.
    /// </summary>
    public bool InitCut { get; }
}

/// <summary>Told each step <see cref="EmulationBuilder"/> takes, in the order it takes them.</summary>
public interface IEmulationBuildObserver
{
    void ConvIndex(string key, string commands);

    void Section(string name, string comment);

    void Port(ScriptPort target, byte[] bytes);

    void PatchMap(string name);

    void PatchDrumMap(string name);

    /// <summary>
    /// A note about the DEF itself, so a line that would be misread silently can be spotted.
    /// </summary>
    void Warning(string message);
}

/// <summary>
/// Turns a DEF plus an output/data module pair into an <see cref="EmulationSetup"/>.
/// </summary>
public sealed class EmulationBuilder(DefDocument definition)
{
    private const int MaxSectionDepth = 8;

    private readonly DefDocument _def = definition;

    /// <summary>
    /// How many ports a <c>MIDI=</c> key reaches, which is all of them.
    /// </summary>
    /// <remarks>
    /// Settable so a test can ask for a short expansion; nothing in the player sets it.
    /// </remarks>
    public int PortCount { get; set; } = IEventSink.PortCount;

    /// <param name="useModule">The module actually being played through.</param>
    /// <param name="targetModule">The module the data was written for.</param>
    /// <param name="filterSection">
    /// Name from <c>[filterindex]</c>, applied after the resolved commands even when it names
    /// no section.
    /// </param>
    /// <param name="observer">Told each step taken; nothing in the player asks.</param>
    public EmulationSetup Build(string useModule, string targetModule,
                                string? filterSection = null,
                                IEmulationBuildObserver? observer = null)
    {
        ConvIndexResult resolution = EmulationResolver.Resolve(_def, useModule, targetModule);

        var settings = new EmulationSettings();
        var patches = new PatchMapSet();
        var actions = new List<ScriptAction>();
        _budget = MidiScript.MaxCost;
        observer?.ConvIndex(resolution.Key, resolution.Commands);

        foreach (string command in SplitCommands(resolution.Commands))
            ReadSection(command, settings, patches, actions, observer, depth: 0);

        if (filterSection is not null)
            ReadSection(filterSection, settings, patches, actions, observer, depth: 0);

        bool cut = _budget < 0;
        if (cut) observer?.Warning(string.Format(Strings.InitCut, MidiScript.MaxCost));
        return new EmulationSetup(resolution, settings, patches, actions, cut);
    }

    /// <summary>
    /// What is left for the scripts of the setup being built: one budget for all of them,
    /// since a section can be named any number of times.
    /// </summary>
    private int _budget;

    /// <summary>
    /// Splits a command line on whitespace, stopping at a token that opens a comment.
    /// </summary>
    /// <remarks>
    /// TMIDI's splitter also treats <c>0x79</c> ('y') as whitespace, meaning tab
    /// (<c>0x09</c>). No shipped command name contains a 'y', so that is not reproduced.
    /// </remarks>
    internal static IEnumerable<string> SplitCommands(string commands)
    {
        foreach (string token in commands.Split(
                     [' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith(';')) yield break;
            yield return token;
        }
    }

    private void ReadSection(
        string name,
        EmulationSettings settings,
        PatchMapSet patches,
        List<ScriptAction> actions,
        IEmulationBuildObserver? observer,
        int depth)
    {
        if (depth > MaxSectionDepth) return;

        // Sections starting with SMFK hold the SMF knife's settings, not emulation commands,
        // so they are dropped before the observer hears of them.
        if (name.StartsWith("SMFK", StringComparison.OrdinalIgnoreCase)) return;

        DefSection? section = _def[name];
        observer?.Section(name, section?.Get("comment") ?? string.Empty);
        if (section is null) return;

        // Undocumented, but TMIDI reads it: inherit another section first.
        string? baseSection = section.Get("BaseSection");
        if (!string.IsNullOrEmpty(baseSection))
            ReadSection(baseSection, settings, patches, actions, observer, depth + 1);

        DefSection? messages = _def["midimessage"];

        AddScript(section.Get("MIDI"), ScriptPort.All, messages, actions, observer);
        AddScript(section.Get("MIDI_A"), ScriptPort.A, messages, actions, observer);
        AddScript(section.Get("MIDI_B"), ScriptPort.B, messages, actions, observer);
        AddScript(section.Get("MIDI_C"), ScriptPort.C, messages, actions, observer);

        foreach (string map in SplitCommands(section.Get("PatchMap", string.Empty)))
        {
            PatchMapAddition added = patches.AddMelodic(_def[map]);
            observer?.PatchMap(map);
            WarnAboutUnreadableRules(added, observer);
        }

        foreach (string map in SplitCommands(section.Get("PatchDrumMap", string.Empty)))
        {
            PatchMapAddition added = patches.AddDrum(_def[map]);
            observer?.PatchDrumMap(map);
            WarnAboutUnreadableRules(added, observer);
        }

        ApplyFlags(section, settings);
    }

    /// <summary>
    /// Reports the lines a patch section contributed that do not read as rules. They are
    /// still registered, as <c>0:0:0</c>, as TMIDI does.
    /// </summary>
    private static void WarnAboutUnreadableRules(
        PatchMapAddition added, IEmulationBuildObserver? observer)
    {
        if (observer is null) return;
        foreach (string key in added.Unreadable)
            observer.Warning(string.Format(Strings.RuleUnreadable, key));
    }

    /// <summary>
    /// Expands a script once, then repeats each message and reset on every destination port.
    /// Waits are emitted once: TMIDI ticks them per port, but they elapse together.
    /// </summary>
    private void AddScript(
        string? script, ScriptPort target, DefSection? messages,
        List<ScriptAction> actions, IEmulationBuildObserver? observer)
    {
        if (string.IsNullOrEmpty(script)) return;

        ScriptPort[] ports = Expand(target);

        foreach (ScriptAction action in MidiScript.Expand(script, ports[0], messages, ref _budget))
        {
            switch (action.Kind)
            {
                case ScriptActionKind.Message:
                    foreach (ScriptPort port in ports)
                    {
                        actions.Add(ScriptAction.Message(port, action.Bytes));
                        observer?.Port(port, action.Bytes);
                    }
                    break;

                case ScriptActionKind.Reset:
                    foreach (ScriptPort port in ports)
                        actions.Add(ScriptAction.Reset(port, action.ResetKind));
                    break;

                case ScriptActionKind.ChannelBroadcast:
                    // Ports on the outside, channels on the inside, matching TMIDI's
                    // broadcast routine.
                    foreach (ScriptPort port in ports)
                    {
                        for (int channel = 0; channel < 16; channel++)
                        {
                            byte[] message = (byte[])action.Bytes.Clone();
                            message[0] = (byte)((message[0] & 0xF0) | channel);
                            actions.Add(ScriptAction.Message(port, message));
                            observer?.Port(port, message);
                        }
                    }
                    break;

                default:
                    actions.Add(action);
                    break;
            }
        }
    }

    private ScriptPort[] Expand(ScriptPort target)
    {
        if (target != ScriptPort.All) return [target];

        var ports = new ScriptPort[Math.Max(1, PortCount)];
        for (int i = 0; i < ports.Length; i++) ports[i] = (ScriptPort)i;
        return ports;
    }

    /// <summary>
    /// Reads each flag using its current value as the default, so sections accumulate.
    /// </summary>
    private static void ApplyFlags(DefSection section, EmulationSettings s)
    {
        s.GsToGmEmu = section.GetBool("GSToGMEmu", s.GsToGmEmu);
        s.XgToGmEmu = section.GetBool("XGToGMEmu", s.XgToGmEmu);
        s.GsToXgEmu = section.GetBool("GSToXGEmu", s.GsToXgEmu);
        s.XgToGsEmu = section.GetBool("XGToGSEmu", s.XgToGsEmu);
        s.GsToX5dEmu = section.GetBool("GSToX5DEmu", s.GsToX5dEmu);
        s.XgToX5dEmu = section.GetBool("XGToX5DEmu", s.XgToX5dEmu);
        s.ProSetting = section.GetBool("88ProSetting", s.ProSetting);
        s.AdjustMk2 = section.GetBool("AdjustMK2", s.AdjustMk2);
        s.CmToGsInit = section.GetBool("CMToGSInit", s.CmToGsInit);
        s.DrumTrack = section.GetInt("DrumTrack", s.DrumTrack);
        s.DefaultBankSelectLsb = section.GetInt("DefaultBankSelectLSB", s.DefaultBankSelectLsb);
        s.DisableBankSelectLsb = section.GetBool("DisableBankSelectLSB", s.DisableBankSelectLsb);
        s.DisableBankSelectMsb = section.GetBool("DisableBankSelectMSB", s.DisableBankSelectMsb);
        s.DisableExclusive = section.GetBool("DisableExclusive", s.DisableExclusive);
        s.DisableResetExclusive =
            section.GetBool("DisableResetExclusive", s.DisableResetExclusive);
        s.MapSelect = section.GetInt("MapSelect", s.MapSelect);
    }
}
