namespace Glosa.Core.Emulation;

/// <summary>
/// The flags a command section can set.
/// </summary>
/// <remarks>
/// Each key is read with the current value as its default, so sections listed later in a
/// command line add to what earlier ones set instead of resetting it.
/// </remarks>
public sealed class EmulationSettings
{
    public bool DisableExclusive { get; set; }
    public int DefaultBankSelectLsb { get; set; }
    public bool DisableBankSelectLsb { get; set; }
    public bool DisableBankSelectMsb { get; set; }
    public bool DisableResetExclusive { get; set; }
    public int MapSelect { get; set; }

    public bool GsToGmEmu { get; set; }
    public bool XgToGmEmu { get; set; }
    public bool GsToXgEmu { get; set; }
    public bool XgToGsEmu { get; set; }
    public bool GsToX5dEmu { get; set; }
    public bool XgToX5dEmu { get; set; }

    /// <summary>The DEF's <c>88ProSetting</c>.</summary>
    public bool ProSetting { get; set; }

    /// <summary>Undocumented in the shipped DEF, but read by TMIDI.</summary>
    public bool AdjustMk2 { get; set; }

    /// <summary>Undocumented in the shipped DEF, but read by TMIDI.</summary>
    public bool CmToGsInit { get; set; }

    /// <summary>Drum channel, zero-based. 9 is MIDI channel 10.</summary>
    public int DrumTrack { get; set; } = 9;

    public EmulationSettings Clone() => (EmulationSettings)MemberwiseClone();
}
