using Glosa.Core.Definition;

namespace Glosa.Core.Emulation;

/// <param name="Key">The <c>use:target</c> key that matched, empty when nothing did.</param>
/// <param name="Commands">Space-separated command section names.</param>
/// <param name="Use">The alias-resolved output module the match was found for.</param>
/// <param name="Target">The alias-resolved data module the match was found for.</param>
public readonly record struct ConvIndexResult(
    string Key, string Commands, string Use, string Target)
{
    public bool Found => Key.Length > 0;
}

/// <summary>
/// Looks up the command list for an output module / data module pair.
/// </summary>
/// <remarks>
/// The lookup walks two chains. The target is climbed through <c>[group]</c> until it stops
/// changing; only then is the output module climbed one step, and the target is reset to its
/// original value before the inner walk starts again. That reset is what lets a rule written
/// for a specific pair win over a more general one.
/// </remarks>
public static class EmulationResolver
{
    private const int MaxSteps = 64;

    public static ConvIndexResult Resolve(DefDocument def, string useModule, string targetModule)
    {
        DefSection? alias = def["alias"];
        DefSection? group = def["group"];
        DefSection? convIndex = def["convindex"];

        string use = Alias(alias, useModule);

        for (int outer = 0; outer < MaxSteps; outer++)
        {
            string target = Alias(alias, targetModule);

            for (int inner = 0; inner < MaxSteps; inner++)
            {
                string key = $"{use}:{target}";
                string? commands = convIndex?.Get(key);
                if (!string.IsNullOrEmpty(commands))
                    return new ConvIndexResult(key, commands, use, target);

                string parent = Lookup(group, target);
                if (Same(parent, target)) break;
                target = parent;
            }

            string useParent = Lookup(group, use);
            if (Same(useParent, use)) break;
            use = useParent;
        }

        return new ConvIndexResult(string.Empty, string.Empty, use, Alias(alias, targetModule));
    }

    /// <summary>Single-step alias resolution; the value defaults to the name itself.</summary>
    public static string Alias(DefSection? alias, string name) => Lookup(alias, name);

    private static string Lookup(DefSection? section, string name)
    {
        string? value = section?.Get(name);
        return string.IsNullOrEmpty(value) ? name : value;
    }

    private static bool Same(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
