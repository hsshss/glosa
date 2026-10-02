using System.Runtime.InteropServices;

namespace Glosa.Midi.Windows;

/// <summary>
/// The Windows MIDI Services API (<c>Windows.Devices.Midi2</c>), called through its vtables
/// rather than a WinRT projection, as the media keys are.
/// </summary>
/// <remarks>
/// The IIDs and slots are those of the API's metadata. IInspectable takes slots 0-5, so an
/// interface's own methods start at 6; a property's getter is a method like any other. The
/// objects are agile, so they are used from whichever thread calls.
/// </remarks>
internal static unsafe class MidiServices
{
    /// <summary>
    /// Names a copy of the API's DLL to use instead of the system's, for trying the backend
    /// before Windows carries the API. Not for a release: the preview DLL may not be shipped.
    /// </summary>
    public const string DllVariable = "GLOSA_MIDI2_DLL";

    private static readonly Guid IidActivationFactory = new("00000035-0000-0000-C000-000000000046");
    private static readonly Guid IidClosable = new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");
    private static readonly Guid IidApiStatics = new("8087b303-0519-c0de-31d1-ee0010000000");
    private static readonly Guid IidPortStatics = new("8087b303-0519-c0de-31d1-ee004001a000");
    private static readonly Guid IidSessionStatics = new("8087b303-0519-c0de-31d1-ee0010009000");
    private static readonly Guid IidSettingsFactory = new("8087b303-0519-c0de-31d1-ff0010007000");
    private static readonly Guid IidConnectionSource = new("8087b303-0519-c0de-31d1-cc001000f030");

    // MidiSendMessageResults
    public const uint Succeeded = 0x8000_0000;
    public const uint BufferFull = 0x0001_0000;

    // MidiApiMode
    private const int LegacyMode = 1;

    // Midi1PortFlow
    private const int Destination = 1;

    private static readonly Lock Gate = new();
    private static bool _started;
    private static string? _unavailable;
    private static delegate* unmanaged[Stdcall]<nint, nint*, int> _dllGetActivationFactory;
    private static nint _portStatics;
    private static nint _sessionStatics;
    private static nint _settingsFactory;

    /// <summary>A MIDI 1.0 destination port, as WinMM lists it.</summary>
    /// <param name="Number">Its WinMM device number.</param>
    /// <param name="Endpoint">
    /// The endpoint it is a group of, or empty for a port the service does not carry (the GS
    /// Wavetable Synth), which only WinMM can open.
    /// </param>
    public readonly record struct Port(uint Number, string Name, string Endpoint, byte Group);

    /// <summary>
    /// Gets the API ready, once; null when it is, otherwise why it cannot be used.
    /// </summary>
    /// <remarks>
    /// Not usable without the API (it comes with Windows from the November 2026 release), with
    /// the service stopped, or with Windows set to Legacy API Mode, where WinMM is the way
    /// Windows itself has been told to use.
    /// </remarks>
    public static string? Start()
    {
        lock (Gate)
        {
            if (_started) return _unavailable;
            _started = true;
            try
            {
                // Threads that never initialised COM (the playback thread) join this MTA.
                ThrowIfFailed(CoIncrementMTAUsage(out _), "CoIncrementMTAUsage");

                if (Environment.GetEnvironmentVariable(DllVariable) is { Length: > 0 } path)
                {
                    nint dll = NativeLibrary.Load(path);
                    _dllGetActivationFactory = (delegate* unmanaged[Stdcall]<nint, nint*, int>)
                        NativeLibrary.GetExport(dll, "DllGetActivationFactory");
                }

                nint api = Statics("Windows.Devices.Midi2.MidiApi", IidApiStatics);
                try
                {
                    byte available;
                    ThrowIfFailed(Call(api, 6, &available), "EnsureServiceAvailable");
                    if (available == 0) return _unavailable = "the MIDI service is not available";

                    int mode;
                    ThrowIfFailed(Call(api, 7, &mode), "GetCurrentlySelectedApiMode");
                    if (mode == LegacyMode) return _unavailable = "Windows is set to Legacy API Mode";
                }
                finally
                {
                    Release(api);
                }

                _portStatics = Statics("Windows.Devices.Midi2.Enumeration.Legacy.MidiLegacyPortDeviceInformation", IidPortStatics);
                _sessionStatics = Statics("Windows.Devices.Midi2.MidiSession", IidSessionStatics);
                _settingsFactory = Statics("Windows.Devices.Midi2.MidiEndpointConnectionSettings", IidSettingsFactory);
                return null;
            }
            catch (Exception ex) when (ex is MidiDeviceException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                return _unavailable = ex.Message;
            }
        }
    }

    /// <summary>The MIDI 1.0 destination ports, in WinMM's order.</summary>
    public static List<Port> DestinationPorts()
    {
        nint list;
        ThrowIfFailed(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(_portStatics, 8))(_portStatics, Destination, &list),
                      "MidiLegacyPortDeviceInformation.FindAll");
        try
        {
            uint count;
            ThrowIfFailed(Call(list, 7, &count), "IVectorView.Size");
            var ports = new List<Port>((int)count);
            for (uint i = 0; i < count; i++)
            {
                nint info;
                ThrowIfFailed(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(list, 6))(list, i, &info), "IVectorView.GetAt");
                try
                {
                    nint name, endpoint, group;
                    uint number;
                    byte index;
                    ThrowIfFailed(Call(info, 8, &name), "Name");
                    string portName = TakeString(name);
                    ThrowIfFailed(Call(info, 10, &endpoint), "AssociatedEndpointDeviceId");
                    string endpointId = TakeString(endpoint);
                    ThrowIfFailed(Call(info, 16, &number), "Number");
                    ThrowIfFailed(Call(info, 14, &group), "Group");
                    try
                    {
                        ThrowIfFailed(Call(group, 6, &index), "MidiGroup.Index");
                    }
                    finally
                    {
                        Release(group);
                    }
                    ports.Add(new Port(number, portName, endpointId, index));
                }
                finally
                {
                    Release(info);
                }
            }
            ports.Sort((a, b) => a.Number.CompareTo(b.Number));
            return ports;
        }
        finally
        {
            Release(list);
        }
    }

    public static nint CreateSession(string name)
    {
        nint hName = CreateString(name), session;
        try
        {
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(_sessionStatics, 6))(_sessionStatics, hName, &session),
                          "MidiSession.Create");
        }
        finally
        {
            WindowsDeleteString(hName);
        }
        return session;
    }

    /// <summary>
    /// Opens a connection to <paramref name="endpoint"/> whose sends wait for the endpoint to
    /// take the message, and so fail once it has stopped taking them.
    /// </summary>
    /// <remarks>See IMPLEMENTATION.md, "Windows MIDI Services バックエンド".</remarks>
    public static nint Connect(nint session, string endpoint, out Guid id)
    {
        nint settings;
        ThrowIfFailed(((delegate* unmanaged[Stdcall]<nint, byte, byte, nint*, int>)Slot(_settingsFactory, 7))(_settingsFactory, 1, 0, &settings),
                      "MidiEndpointConnectionSettings.CreateInstance2");

        nint hEndpoint = CreateString(endpoint), connection;
        try
        {
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(session, 11))(session, hEndpoint, settings, &connection),
                          "CreateEndpointConnection");
        }
        finally
        {
            WindowsDeleteString(hEndpoint);
            Release(settings);
        }

        try
        {
            nint source = QueryInterface(connection, IidConnectionSource);
            try
            {
                Guid connectionId;
                ThrowIfFailed(Call(source, 12, &connectionId), "ConnectionId");
                id = connectionId;
            }
            finally
            {
                Release(source);
            }

            byte opened;
            ThrowIfFailed(Call(connection, 8, &opened), "MidiEndpointConnection.Open");
            if (opened == 0) throw new MidiDeviceException("the endpoint would not open");
            return connection;
        }
        catch
        {
            Release(connection);
            throw;
        }
    }

    /// <summary>The most words one send takes.</summary>
    public static int MaxWordsPerSend(nint connection)
    {
        uint max;
        ThrowIfFailed(Call(connection, 26, &max), "GetSupportedMaxMidiWordsPerTransmission");
        return (int)max;
    }

    /// <summary>Sends whole messages, now; the result's flags (MidiSendMessageResults).</summary>
    public static uint Send(nint connection, ReadOnlySpan<uint> words)
    {
        uint result;
        fixed (uint* p = words)
        {
            // SendMultipleMessagesWordArray(timestamp 0 = now, startIndex, wordCount, words[]).
            ThrowIfFailed(((delegate* unmanaged[Stdcall]<nint, ulong, uint, uint, uint, uint*, uint*, int>)Slot(connection, 21))(
                              connection, 0, 0, (uint)words.Length, (uint)words.Length, p, &result),
                          "SendMultipleMessagesWordArray");
        }
        return result;
    }

    /// <summary>What a send's result says went wrong.</summary>
    public static string Describe(uint result)
    {
        var said = new List<string>();
        if ((result & 0x1000_0000) != 0) said.Add("failed");
        if ((result & BufferFull) != 0) said.Add("buffer full");
        if ((result & 0x0004_0000) != 0) said.Add("connection closed or invalid");
        if ((result & 0x0010_0000) != 0) said.Add("invalid message type for word count");
        if ((result & 0x0020_0000) != 0) said.Add("invalid message");
        if ((result & 0x0040_0000) != 0) said.Add("data index out of range");
        if ((result & 0x0080_0000) != 0) said.Add("timestamp out of range");
        if ((result & 0x0100_0000) != 0) said.Add("too many words for one send");
        return said.Count > 0 ? string.Join(", ", said) : $"result 0x{result:X8}";
    }

    /// <summary>Disconnects and releases a connection; what went wrong, or null.</summary>
    public static string? Disconnect(nint session, nint connection, Guid id)
    {
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid, int>)Slot(session, 12))(session, id);
        Release(connection);
        return hr < 0 ? $"DisconnectEndpointConnection failed: 0x{hr:X8}" : null;
    }

    /// <summary>Closes and releases a session; what went wrong, or null.</summary>
    public static string? CloseSession(nint session)
    {
        try
        {
            nint closable = QueryInterface(session, IidClosable);
            try
            {
                int hr = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(closable, 6))(closable);
                return hr < 0 ? $"MidiSession.Close failed: 0x{hr:X8}" : null;
            }
            finally
            {
                Release(closable);
            }
        }
        catch (MidiDeviceException ex)
        {
            return ex.Message;
        }
        finally
        {
            Release(session);
        }
    }

    public static void Release(nint p)
    {
        if (p != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(p, 2))(p);
    }

    private static nint Statics(string className, Guid iid)
    {
        nint name = CreateString(className), factory;
        Guid iidFactory = IidActivationFactory;
        try
        {
            int hr = _dllGetActivationFactory != null
                ? _dllGetActivationFactory(name, &factory)
                : RoGetActivationFactory(name, &iidFactory, &factory);
            ThrowIfFailed(hr, $"activating {className}");
        }
        finally
        {
            WindowsDeleteString(name);
        }

        try
        {
            return QueryInterface(factory, iid);
        }
        finally
        {
            Release(factory);
        }
    }

    private static nint Slot(nint p, int index) => (*(nint**)p)[index];

    /// <summary>A method whose only parameter is its result.</summary>
    private static int Call<T>(nint p, int slot, T* result) where T : unmanaged
        => ((delegate* unmanaged[Stdcall]<nint, T*, int>)Slot(p, slot))(p, result);

    private static nint QueryInterface(nint p, Guid iid)
    {
        nint result;
        ThrowIfFailed(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(p, 0))(p, &iid, &result), $"QueryInterface {iid}");
        return result;
    }

    private static nint CreateString(string s)
    {
        nint h;
        fixed (char* p = s) ThrowIfFailed(WindowsCreateString(p, (uint)s.Length, &h), "WindowsCreateString");
        return h;
    }

    /// <summary>The string an HSTRING holds, which is then deleted.</summary>
    private static string TakeString(nint h)
    {
        uint length;
        char* p = WindowsGetStringRawBuffer(h, &length);
        string s = new(p, 0, (int)length);
        WindowsDeleteString(h);
        return s;
    }

    private static void ThrowIfFailed(int hr, string what)
    {
        if (hr < 0) throw new MidiDeviceException($"{what} failed: 0x{hr:X8}");
    }

    [DllImport("combase.dll")]
    private static extern int CoIncrementMTAUsage(out nint cookie);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString(char* source, uint length, nint* hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll")]
    private static extern char* WindowsGetStringRawBuffer(nint hstring, uint* length);
}
