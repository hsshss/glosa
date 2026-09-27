using Glosa.Core.Emulation;

namespace Glosa.Tests;

public class DefinitionOverrideTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"glosa-define-{Guid.NewGuid():N}");

    public DefinitionOverrideTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Shipped(string text)
    {
        string path = Path.Combine(_root, ModuleDefinition.FileName);
        File.WriteAllText(path, text);
        return path;
    }

    private string Settings => Path.Combine(_root, "settings");

    private string Sample => Path.Combine(Settings, ModuleDefinition.SampleFileName);

    [Fact]
    public void TheSampleIsACopyOfTheShippedFile()
    {
        ModuleDefinition.WriteSample(Shipped("modules: []"), Settings);

        Assert.Equal("modules: []", File.ReadAllText(Sample));
    }

    [Fact]
    public void TheSampleIsWrittenAfreshOverTheLastOne()
    {
        Directory.CreateDirectory(Settings);
        File.WriteAllText(Sample, "an older release's");

        ModuleDefinition.WriteSample(Shipped("this release's"), Settings);

        Assert.Equal("this release's", File.ReadAllText(Sample));
    }

    [Fact]
    public void WritingTheSampleLeavesTheOverrideAlone()
    {
        Directory.CreateDirectory(Settings);
        string custom = Path.Combine(Settings, ModuleDefinition.OverrideFileName);
        File.WriteAllText(custom, "edited");

        ModuleDefinition.WriteSample(Shipped("shipped"), Settings);

        Assert.Equal("edited", File.ReadAllText(custom));
    }

    [Fact]
    public void TheOverrideIsFoundByItsName()
    {
        Directory.CreateDirectory(Settings);
        string custom = Path.Combine(Settings, ModuleDefinition.OverrideFileName);
        File.WriteAllText(custom, "modules: []");

        Assert.Equal(custom, ModuleDefinition.FindOverride(Settings));
    }

    [Fact]
    public void TheSampleIsNoOverrideUntilItIsRenamed()
    {
        ModuleDefinition.WriteSample(Shipped("modules: []"), Settings);

        Assert.Null(ModuleDefinition.FindOverride(Settings));
    }

    [Fact]
    public void AMissingSettingsFolderHasNoOverride()
        => Assert.Null(ModuleDefinition.FindOverride(Settings));

    private static ModuleDefinition Define(int? version, params string[] modules)
        => ModuleDefinition.Parse(
            (version is { } v ? $"version: {v}\n" : "")
            + "modules:\n" + string.Concat(modules.Select(m => $"  - name: {m}\n")));

    [Fact]
    public void TheFileSaysWhichVersionOfTheFormatItIs()
    {
        Assert.Equal(3, Define(3, "THRU").Version);
        Assert.Equal(0, Define(null, "THRU").Version);
    }

    [Fact]
    public void TheModulesDetectionAnswersWithAreAddedThruFirstAndTheFamiliesLast()
    {
        ModuleDefinition own = Define(1, "SC-55", "MU2000").WithOwnAnswers();

        Assert.Equal(["THRU", "SC-55", "MU2000", "CM-64", "GS", "XG"], own.Modules);
    }

    [Fact]
    public void AnAddedFamilyIsResetAsItsFamily()
    {
        ModuleDefinition own = Define(1, "SC-55").WithOwnAnswers();

        Assert.Null(own.InitializeTypeOf("THRU"));
        Assert.Equal(InitializeType.MT32, own.InitializeTypeOf("CM-64"));
        Assert.Equal(InitializeType.GS, own.InitializeTypeOf("GS"));
        Assert.Equal(InitializeType.XG, own.InitializeTypeOf("XG"));
    }

    [Fact]
    public void OneTheFileListsKeepsItsPlaceAndWhatTheFileSaysOfIt()
    {
        ModuleDefinition own = ModuleDefinition.Parse("""
            modules:
              - name: gs
                initializeType: SC88
              - name: SC-55
              - name: thru
            """).WithOwnAnswers();

        Assert.Equal(["gs", "SC-55", "thru", "CM-64", "XG"], own.Modules);
        Assert.Equal(InitializeType.SC88, own.InitializeTypeOf("GS"));
    }

    [Fact]
    public void AnAddedModuleChangesNoDetection()
    {
        ModuleDefinition own = ModuleDefinition.Parse("""
            modules:
              - name: SC-55
                patterns: ['55']
            """).WithOwnAnswers();

        Assert.Equal("SC-55", own.Scan("GS XG THRU 55")?.Module);
        Assert.Null(own.Scan("GS XG THRU"));
    }

    [Fact]
    public void TheShippedFileListsWhatWouldBeAddedAlready()
    {
        string shipped = Path.Combine(AppContext.BaseDirectory, ModuleDefinition.FileName);
        ModuleDefinition file = ModuleDefinition.Load(shipped);

        Assert.Same(file, file.WithOwnAnswers());
    }

    /// <summary>
    /// The shipped file, and so the sample an override is made from, is in the format this
    /// player reads: an override made from it is read.
    /// </summary>
    [Fact]
    public void TheShippedFileIsInTheFormatThisPlayerReads()
    {
        string shipped = Path.Combine(AppContext.BaseDirectory, ModuleDefinition.FileName);
        Assert.Equal(ModuleDefinition.FormatVersion, ModuleDefinition.Load(shipped).Version);
    }
}
