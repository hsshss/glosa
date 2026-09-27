using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Glosa.Core.Playback;

/// <summary>
/// Reads and writes the project's own YAML files.
/// </summary>
/// <remarks>
/// The point of the format is that a person can open the file and fix it, so keys are lower
/// camel case, absent keys fall back to their defaults, and unknown keys are ignored rather
/// than treated as an error. An enum is read by its name only (<see cref="EnumNames"/>).
///
/// What was never set is left out (<see cref="OmitUnspecifiedVisitor"/>), so a file holds only
/// what is its own, unless it is written whole (<see cref="Save{T}"/>).
///
/// Files are UTF-8: the rest of the project reads CP932 because the songs and DEFs it
/// reads are CP932, but these are our own files and YAML is UTF-8.
/// </remarks>
public static class YamlFile
{
    private static readonly IDeserializer Reader = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new EnumNames())
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Writer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithEmissionPhaseObjectGraphVisitor(args => new OmitUnspecifiedVisitor(args.InnerVisitor))
        .Build();

    private static readonly ISerializer WholeWriter = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <exception cref="InvalidDataException">The file is not UTF-8.</exception>
    /// <remarks>
    /// Bytes that are not UTF-8 make the file unreadable rather than being replaced: these
    /// files are written back, and the replacements would be written over what was there.
    /// </remarks>
    public static T Load<T>(string path) where T : new()
    {
        string text;
        try
        {
            text = File.ReadAllText(path, Utf8Strict);
        }
        catch (System.Text.DecoderFallbackException ex)
        {
            throw new InvalidDataException(Strings.NotUtf8, ex);
        }
        return Parse<T>(text);
    }

    private static readonly System.Text.UTF8Encoding Utf8Strict =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The deepest the mappings and lists in a file may nest. Ours go a few levels deep;
    /// text nested far past that would take minutes to read.
    /// </summary>
    public const int MaxDepth = 64;

    /// <exception cref="InvalidDataException">The text nests deeper than <see cref="MaxDepth"/>.</exception>
    public static T Parse<T>(string yaml) where T : new()
    {
        CheckDepth(yaml);
        return Reader.Deserialize<T>(yaml) ?? new T();
    }

    /// <summary>Refuses text that nests too deep, before reading it into anything.</summary>
    /// <remarks>Stops at the first level too many, so a file built to be slow is quick to turn away.</remarks>
    private static void CheckDepth(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        int depth = 0;
        while (parser.MoveNext())
        {
            if (parser.Current is MappingStart or SequenceStart && ++depth > MaxDepth)
                throw new InvalidDataException(string.Format(Strings.YamlTooDeep, MaxDepth));
            if (parser.Current is MappingEnd or SequenceEnd) depth--;
        }
    }

    public static string ToYaml<T>(T value, bool whole = false)
        => (whole ? WholeWriter : Writer).Serialize(value!);

    /// <param name="whole">
    /// Writes every value, set or not. For a file whose defaults may change: a value left out
    /// reads back as whatever the default is by then.
    /// </param>
    public static void Save<T>(T value, string path, bool whole = false)
        => Write(ToYaml(value, whole), path);

    /// <summary>Writes text made by <see cref="ToYaml{T}"/> to a file, as <see cref="Save{T}"/> does.</summary>
    /// <remarks>
    /// Written beside the file and moved into place once it is on the disk, so neither an
    /// interrupted save nor a power cut leaves a truncated file behind. The temporary file
    /// has a name of its own, so two players saving one file do not write into the same one.
    /// A save that fails takes its temporary file with it.
    ///
    /// A link is followed, and the file it leads to is the one replaced. A read-only file is
    /// refused: on Linux and macOS, replacing it would get past its permissions, since only
    /// the folder's are asked.
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">The file is read-only.</exception>
    public static void Write(string yaml, string path)
    {
        if (File.Exists(path))
        {
            if (File.ResolveLinkTarget(path, returnFinalTarget: true) is { } target) path = target.FullName;
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
                throw new UnauthorizedAccessException(Strings.FileReadOnly);
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(new System.Text.UTF8Encoding(false).GetBytes(yaml));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The save's own failure is the one worth reporting.
            }
            throw;
        }
    }

    /// <summary>
    /// Reads an enum by one of its names, whatever the case, and nothing else.
    /// </summary>
    /// <remarks>
    /// The runtime would also take a number, and any number, named or not, so a hand-edited
    /// <c>repeat: 7</c> would stand as a mode that does not exist. A value that is not a name
    /// is an error like any other in the file.
    /// </remarks>
    private sealed class EnumNames : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type.IsEnum;

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            Scalar scalar = parser.Consume<Scalar>();
            string? name = Enum.GetNames(type).FirstOrDefault(
                n => string.Equals(n, scalar.Value.Trim(), StringComparison.OrdinalIgnoreCase));

            return name is not null
                ? Enum.Parse(type, name)
                : throw new YamlException(scalar.Start, scalar.End,
                    $"'{scalar.Value}' is not one of {string.Join(", ", Enum.GetNames(type))}.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
            => emitter.Emit(new Scalar(value?.ToString() ?? string.Empty));
    }
}
