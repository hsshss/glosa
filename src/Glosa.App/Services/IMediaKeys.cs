namespace Glosa.App.Services;

/// <summary>
/// The system's media keys — play, pause, stop, next and previous — and the system's own
/// display of what is playing, as one platform offers them.
/// </summary>
public interface IMediaKeys : IDisposable
{
    /// <summary>A media key was pressed.</summary>
    /// <remarks>
    /// On whatever thread the system calls from, which need not be the UI's. A single
    /// play/pause key arrives as <see cref="MediaKey.Pause"/> while the last
    /// <see cref="Show"/> said playing, and as <see cref="MediaKey.Play"/> otherwise.
    /// </remarks>
    event Action<MediaKey>? Pressed;

    /// <summary>
    /// Tells the system what the player is doing, and the title of the song while there is
    /// one on the transport.
    /// </summary>
    void Show(MediaStatus status, string? title);
}

/// <summary>A media key, as <see cref="IMediaKeys.Pressed"/> reports it.</summary>
public enum MediaKey
{
    Play,
    Pause,
    Stop,
    Next,
    Previous,
}

/// <summary>What the player is doing, as <see cref="IMediaKeys.Show"/> tells the system.</summary>
public enum MediaStatus
{
    Stopped,
    Playing,
    Paused,
}
