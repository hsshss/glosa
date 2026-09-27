#if LINUX
using System.Text;
using Tmds.DBus.Protocol;

namespace Glosa.App.Services;

/// <summary>
/// The media keys on Linux: MPRIS, the D-Bus interface through which a desktop talks to
/// media players.
/// </summary>
/// <remarks>
/// The desktop's calls come in on the connection's own thread; the player tells the status
/// from the UI thread, so what is shown is kept under a lock.
/// </remarks>
internal sealed class MprisMediaKeys : IMediaKeys, IPathMethodHandler
{
    private const string BusName = "org.mpris.MediaPlayer2.glosa";
    private const string ObjectPath = "/org/mpris/MediaPlayer2";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    /// <summary>The track id MPRIS reserves for there being none.</summary>
    private const string NoTrack = "/org/mpris/MediaPlayer2/TrackList/NoTrack";

    private readonly DBusConnection _connection;
    private readonly Lock _gate = new();

    private MediaStatus _status = MediaStatus.Stopped;
    private string? _title;
    private int _track;
    private bool _disposed;

    public event Action<MediaKey>? Pressed;

    private MprisMediaKeys(DBusConnection connection) => _connection = connection;

    /// <summary>
    /// The media keys, or null when there is no session bus — which leaves the player
    /// without media keys and nothing more.
    /// </summary>
    /// <remarks>
    /// Connecting and taking the name finish later, on their own; until they do, and if they
    /// fail, the keys do nothing.
    /// </remarks>
    public static MprisMediaKeys? Create()
    {
        if (!OperatingSystem.IsLinux() || DBusAddress.Session is not { } address) return null;

        var keys = new MprisMediaKeys(new DBusConnection(address));
        _ = keys.StartAsync();
        return keys;
    }

    private async Task StartAsync()
    {
        try
        {
            await _connection.ConnectAsync();
            _connection.AddMethodHandler(this);
            // A second player on other settings takes a name of its own, as MPRIS asks.
            if (!await _connection.TryRequestNameAsync(BusName, RequestNameOptions.None))
                await _connection.TryRequestNameAsync($"{BusName}.instance{Environment.ProcessId}",
                                                      RequestNameOptions.None);
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or ObjectDisposedException)
        {
            // No bus to be had: no media keys.
        }
    }

    /// <remarks>
    /// Each change is announced (<c>PropertiesChanged</c>): that is how the desktop learns the
    /// player is playing, and so which player the keys go to.
    /// </remarks>
    public void Show(MediaStatus status, string? title)
    {
        KeyValuePair<string, VariantValue>[] changed;
        lock (_gate)
        {
            if (_disposed || (status == _status && title == _title)) return;
            if (title != _title) _track++;
            _status = status;
            _title = title;
            changed = [new("PlaybackStatus", PlaybackStatus()), new("Metadata", Metadata())];
        }

        using MessageWriter writer = _connection.GetMessageWriter();
        writer.WriteSignalHeader(null, ObjectPath, PropertiesInterface, "PropertiesChanged", "sa{sv}as");
        writer.WriteString(PlayerInterface);
        writer.WriteDictionary(changed);
        writer.WriteArray(Array.Empty<string>());
        // Not connected yet: the desktop reads everything when the name appears.
        _connection.TrySendMessage(writer.CreateMessage());
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Closing the connection gives up the name, and the desktop forgets the player.
        _connection.Dispose();
    }

    string IPathMethodHandler.Path => ObjectPath;

    bool IPathMethodHandler.HandlesChildPaths => false;

    ValueTask IPathMethodHandler.HandleMethodAsync(MethodContext context)
    {
        Message request = context.Request;
        string member = request.MemberAsString ?? "";

        if (context.IsDBusIntrospectRequest)
        {
            // The library adds Introspectable and Peer itself.
            context.ReplyIntrospectXml([IntrospectionXml.DBusProperties, Introspection], ReadOnlySpan<string>.Empty);
        }
        else if (context.IsPropertiesInterfaceRequest)
        {
            HandleProperties(context, member);
        }
        else if (request.InterfaceAsString == PlayerInterface && IsPlayerMethod(member))
        {
            if (KeyFor(member) is { } key) Pressed?.Invoke(key);
            ReplyEmpty(context);
        }
        else if (request.InterfaceAsString == RootInterface && member is "Raise" or "Quit")
        {
            ReplyEmpty(context);   // CanRaise and CanQuit say these do nothing
        }
        else
        {
            context.ReplyUnknownMethodError();
        }
        return default;
    }

    private static bool IsPlayerMethod(string member)
        => member is "Play" or "Pause" or "PlayPause" or "Stop" or "Next" or "Previous"
                  or "Seek" or "SetPosition" or "OpenUri";

    /// <summary>
    /// The key a Player method stands for; null for the ones that are answered but do nothing
    /// (seeking, opening a file).
    /// </summary>
    private MediaKey? KeyFor(string member) => member switch
    {
        "Play" => MediaKey.Play,
        "Pause" => MediaKey.Pause,
        // Which one it means depends on the status last told.
        "PlayPause" => CurrentStatus() == MediaStatus.Playing ? MediaKey.Pause : MediaKey.Play,
        "Stop" => MediaKey.Stop,
        "Next" => MediaKey.Next,
        "Previous" => MediaKey.Previous,
        _ => null,
    };

    private MediaStatus CurrentStatus()
    {
        lock (_gate) return _status;
    }

    private void HandleProperties(MethodContext context, string member)
    {
        Reader reader = context.Request.GetBodyReader();
        string @interface = reader.ReadString();

        switch (member)
        {
            case "Get":
            {
                string name = reader.ReadString();
                if (Properties(@interface).FirstOrDefault(p => p.Key == name) is { Key: not null } property)
                {
                    using MessageWriter writer = context.CreateReplyWriter("v");
                    writer.WriteVariant(property.Value);
                    context.Reply(writer.CreateMessage());
                }
                else
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"no property {name}");
                }
                break;
            }
            case "GetAll":
            {
                using MessageWriter writer = context.CreateReplyWriter("a{sv}");
                writer.WriteDictionary(Properties(@interface));
                context.Reply(writer.CreateMessage());
                break;
            }
            case "Set":
                // LoopStatus, Shuffle, Rate and Volume are not offered, so nothing can be set.
                context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", "read-only");
                break;
            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    /// <summary>The properties of <paramref name="interface"/>, as they are now.</summary>
    private KeyValuePair<string, VariantValue>[] Properties(string @interface)
    {
        switch (@interface)
        {
            case RootInterface:
                return
                [
                    new("CanQuit", VariantValue.Bool(false)),
                    new("CanRaise", VariantValue.Bool(false)),
                    new("HasTrackList", VariantValue.Bool(false)),
                    new("Identity", VariantValue.String("Glosa")),
                    new("SupportedUriSchemes", VariantValue.Array(Array.Empty<string>())),
                    new("SupportedMimeTypes", VariantValue.Array(Array.Empty<string>())),
                ];
            case PlayerInterface:
                lock (_gate)
                {
                    return
                    [
                        new("PlaybackStatus", PlaybackStatus()),
                        new("Metadata", Metadata()),
                        new("Rate", VariantValue.Double(1)),
                        new("MinimumRate", VariantValue.Double(1)),
                        new("MaximumRate", VariantValue.Double(1)),
                        new("Volume", VariantValue.Double(1)),
                        new("Position", VariantValue.Int64(0)),
                        new("CanGoNext", VariantValue.Bool(true)),
                        new("CanGoPrevious", VariantValue.Bool(true)),
                        new("CanPlay", VariantValue.Bool(true)),
                        new("CanPause", VariantValue.Bool(true)),
                        new("CanSeek", VariantValue.Bool(false)),
                        new("CanControl", VariantValue.Bool(true)),
                    ];
                }
            default:
                return [];
        }
    }

    /// <summary>Under <see cref="_gate"/>.</summary>
    private VariantValue PlaybackStatus() => VariantValue.String(_status switch
    {
        MediaStatus.Playing => "Playing",
        MediaStatus.Paused => "Paused",
        _ => "Stopped",
    });

    /// <summary>
    /// The title, under an id of its own for each song, or only <see cref="NoTrack"/> when
    /// there is no song.
    /// </summary>
    /// <remarks>Under <see cref="_gate"/>.</remarks>
    private VariantValue Metadata()
    {
        var metadata = new Dict<string, VariantValue>();
        if (_title is not null)
        {
            metadata.Add("mpris:trackid", VariantValue.ObjectPath($"/io/github/glosa/track/{_track}"));
            metadata.Add("xesam:title", VariantValue.String(_title));
        }
        else
        {
            metadata.Add("mpris:trackid", VariantValue.ObjectPath(NoTrack));
        }
        return metadata;
    }

    private static void ReplyEmpty(MethodContext context)
    {
        if (context.NoReplyExpected) return;
        using MessageWriter writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    /// <summary>The two MPRIS interfaces, for a tool that asks the object what it offers.</summary>
    private static readonly ReadOnlyMemory<byte> Introspection = Encoding.UTF8.GetBytes("""
        <interface name="org.mpris.MediaPlayer2">
          <method name="Raise"/>
          <method name="Quit"/>
          <property name="CanQuit" type="b" access="read"/>
          <property name="CanRaise" type="b" access="read"/>
          <property name="HasTrackList" type="b" access="read"/>
          <property name="Identity" type="s" access="read"/>
          <property name="SupportedUriSchemes" type="as" access="read"/>
          <property name="SupportedMimeTypes" type="as" access="read"/>
        </interface>
        <interface name="org.mpris.MediaPlayer2.Player">
          <method name="Next"/>
          <method name="Previous"/>
          <method name="Pause"/>
          <method name="PlayPause"/>
          <method name="Stop"/>
          <method name="Play"/>
          <method name="Seek"><arg name="Offset" type="x" direction="in"/></method>
          <method name="SetPosition">
            <arg name="TrackId" type="o" direction="in"/>
            <arg name="Position" type="x" direction="in"/>
          </method>
          <method name="OpenUri"><arg name="Uri" type="s" direction="in"/></method>
          <signal name="Seeked"><arg name="Position" type="x"/></signal>
          <property name="PlaybackStatus" type="s" access="read"/>
          <property name="Rate" type="d" access="read"/>
          <property name="Metadata" type="a{sv}" access="read"/>
          <property name="Volume" type="d" access="read"/>
          <property name="Position" type="x" access="read"/>
          <property name="MinimumRate" type="d" access="read"/>
          <property name="MaximumRate" type="d" access="read"/>
          <property name="CanGoNext" type="b" access="read"/>
          <property name="CanGoPrevious" type="b" access="read"/>
          <property name="CanPlay" type="b" access="read"/>
          <property name="CanPause" type="b" access="read"/>
          <property name="CanSeek" type="b" access="read"/>
          <property name="CanControl" type="b" access="read"/>
        </interface>
        """);
}
#endif
