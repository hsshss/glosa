namespace Glosa.App.Services;

/// <summary>Where a device stands with the player.</summary>
public enum OutputState
{
    /// <summary>Not open: nothing has asked for it since playback last came to rest.</summary>
    Closed,

    /// <summary>Open, and held until playback comes to rest.</summary>
    Open,

    /// <summary>Asked for, and would not open.</summary>
    Failed,
}
