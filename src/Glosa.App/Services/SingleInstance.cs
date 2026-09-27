using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Glosa.App.Services;

/// <summary>
/// Keeps one player per user and settings folder, and hands a later start's command line to
/// the one already running.
/// </summary>
/// <remarks>
/// Two players on the same settings would each open the same MIDI devices and each write the
/// settings and playlists on the way out, the last one over the other. A second start —
/// from the command line, or a file opened with the player — is instead a request to the
/// first: its working folder and arguments go over a pipe, and it exits.
///
/// Which start is first is settled by a named mutex rather than by the pipe: the pipe only
/// exists once the first one has got as far as listening, and two starts at once would both
/// find it missing. The mutex is held for the life of the process; the system lets go of it
/// if the process dies. A start that loses the mutex keeps trying to reach the pipe, and
/// takes the mutex itself if the first one has meanwhile exited.
///
/// A different <c>--config</c> is a different player: the folder is part of the name.
/// </remarks>
internal sealed partial class SingleInstance : IDisposable
{
    /// <summary>How long a later start keeps trying to reach the first before starting anyway.</summary>
    private static readonly TimeSpan HandOverTimeout = TimeSpan.FromSeconds(10);

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<Request> _waiting = new();
    private readonly Lock _lock = new();
    private Action<Request>? _handler;
    private bool _refusing;

    /// <summary>A later start's command line, with the folder it was started in.</summary>
    public sealed record Request(string WorkingDirectory, string[] Arguments);

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>
    /// Becomes the player for <paramref name="configDirectory"/>, or hands
    /// <paramref name="args"/> to the one that already is.
    /// </summary>
    /// <param name="handedOver">
    /// True when the arguments went to the player already running, and this run should exit.
    /// </param>
    /// <returns>This run's claim, to keep until it exits; null when there is none.</returns>
    /// <remarks>
    /// When the running player cannot be reached in time — hung, or stuck shutting down —
    /// this run starts all the same, without a claim, rather than drop what was asked of it.
    /// </remarks>
    public static SingleInstance? Claim(string configDirectory, string[] args, out bool handedOver)
    {
        string name = "glosa-" + NameFor(configDirectory);
        Mutex mutex = CreateMutex(name);
        DateTime deadline = DateTime.UtcNow + HandOverTimeout;

        handedOver = false;
        while (true)
        {
            if (TryOwn(mutex))
            {
                var instance = new SingleInstance(mutex, name);
                instance.Listen();
                return instance;
            }
            if (TryHandOver(name, args)) handedOver = true;
            if (handedOver || DateTime.UtcNow >= deadline)
            {
                mutex.Dispose();
                return null;
            }
        }
    }

    /// <summary>
    /// Where requests go once there is somewhere to put them; those that came before are
    /// handed over first, in order. The handler is called on the pipe's own thread.
    /// </summary>
    public void OnRequest(Action<Request> handler)
    {
        lock (_lock)
        {
            _handler = handler;
            while (_waiting.TryDequeue(out Request? request)) handler(request);
        }
    }

    /// <summary>
    /// Takes no more requests, for when the player starts closing: one taken now would be
    /// answered as received and then lost with the player.
    /// </summary>
    /// <remarks>
    /// A later start that is left unanswered keeps trying, and becomes the player itself once
    /// this one has gone (<see cref="Claim"/>).
    /// </remarks>
    public void Refuse()
    {
        lock (_lock) _refusing = true;
        _stop.Cancel();
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { }   // not held on this thread: the system will let go
        _mutex.Dispose();
    }

    /// <summary>
    /// A short name for the user and the settings folder: a pipe on Unix is a socket file,
    /// and its path has a length limit.
    /// </summary>
    private static string NameFor(string configDirectory)
    {
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configDirectory));
        // Windows folders are the same whatever the case.
        if (OperatingSystem.IsWindows()) folder = folder.ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "\n" + folder));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>The mutex that says which start is first.</summary>
    /// <remarks>
    /// Elsewhere than on Windows it is made for the user across sessions, not for the
    /// terminal's session alone; on Windows the default already covers the user's logon.
    /// </remarks>
    private static Mutex CreateMutex(string name)
        => OperatingSystem.IsWindows()
            ? new Mutex(false, name)
            : new Mutex(false, name, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });

    private static bool TryOwn(Mutex mutex)
    {
        try { return mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }   // its owner died holding it
    }

    private static bool TryHandOver(string pipeName, string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                                                       PipeOptions.CurrentUserOnly);
            pipe.Connect(500);
            LetForward(pipe);

            using var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);
            writer.Write(Environment.CurrentDirectory);
            writer.Write(args.Length);
            foreach (string arg in args) writer.Write(arg);
            writer.Flush();

            // Only gone once the player has it: exiting sooner could take the request with us.
            return pipe.ReadByte() == 1;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Listen() => Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);

                Request request;
                using (var reader = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true))
                {
                    string folder = reader.ReadString();
                    var args = new string[reader.ReadInt32()];
                    for (int i = 0; i < args.Length; i++) args[i] = reader.ReadString();
                    request = new Request(folder, args);
                }
                // Answered only when it is taken, which it is not once the player is closing.
                lock (_lock)
                {
                    if (_refusing) return;
                    pipe.WriteByte(1);
                    pipe.Flush();
                    if (_handler is { } handler) handler(request);
                    else _waiting.Enqueue(request);
                }
            }
            catch (OperationCanceledException) { return; }
            // A start that gave up halfway, or sent something unreadable: wait for the next.
            // A pipe that could not be made at all is tried again after a pause, not in a spin.
            catch (Exception ex) when (ex is IOException or EndOfStreamException)
            {
                try { await Task.Delay(200, _stop.Token); }
                catch (OperationCanceledException) { return; }
            }
        }
    });

    /// <summary>
    /// Lets the running player at the other end of <paramref name="pipe"/> bring its window
    /// forward. Windows only gives that right to the process the user just started, so this
    /// one passes it on before asking.
    /// </summary>
    static partial void LetForward(NamedPipeClientStream pipe);
}
