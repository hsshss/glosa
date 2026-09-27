namespace Glosa.Core.Playback;

/// <summary>
/// Lets a platform backend raise the system timer resolution while playing.
/// Keeps <c>Glosa.Core</c> free of platform code.
/// </summary>
public interface IPlatformTimer
{
    /// <summary>Requests the finest timer resolution until the returned scope is disposed.</summary>
    IDisposable BeginHighResolution();
}

public sealed class NullPlatformTimer : IPlatformTimer
{
    public static readonly NullPlatformTimer Instance = new();
    public IDisposable BeginHighResolution() => NullScope.Instance;

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
