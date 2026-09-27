using CommunityToolkit.Mvvm.ComponentModel;
using Glosa.Core.Emulation;

namespace Glosa.App.ViewModels;

/// <summary>
/// One part in the performance monitor: what the song has done to it. Read only.
/// </summary>
public sealed partial class PartRowViewModel : ViewModelBase
{
    private readonly DisplayPart _part;

    public PartRowViewModel(int port, int channel, DisplayPart part)
    {
        Port = port;
        Channel = channel;
        _part = part;
        Refresh();
    }

    public int Port { get; }

    public int Channel { get; }

    public string Label => $"{(char)('A' + Port)}{Channel + 1:00}";

    public byte[] Notes => _part.Notes;

    public byte[] Velocity => _part.NoteVelocity;

    [ObservableProperty]
    public partial string Instrument { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int Volume { get; set; }

    [ObservableProperty]
    public partial int Pan { get; set; } = 0x40;

    [ObservableProperty]
    public partial int Expression { get; set; }

    [ObservableProperty]
    public partial int Sounding { get; set; }

    [ObservableProperty]
    public partial int ReverbSend { get; set; }

    [ObservableProperty]
    public partial int ChorusSend { get; set; }

    [ObservableProperty]
    public partial bool Rhythm { get; set; }

    [ObservableProperty]
    public partial int Revision { get; set; }

    /// <summary>
    /// Fades the parts the song never touches. They stay on the list, as a module shows all
    /// sixteen whether or not they are in use.
    /// </summary>
    public double RowOpacity =>
        _part.NotesPlayed > 0 || _part.Volume > 0 || _part.Program > 0 ? 1.0 : 0.4;

    public void Refresh()
    {
        Instrument = GeneralMidi.PartLabel(_part.BankMsb, _part.BankLsb, _part.Program,
                                           _part.Rhythm);
        Volume = _part.Volume;
        Pan = _part.Panpot;
        Expression = _part.Expression;
        Sounding = _part.Sounding;
        ReverbSend = _part.ReverbSend;
        ChorusSend = _part.ChorusSend;
        Rhythm = _part.Rhythm;

        Revision++;
        OnPropertyChanged(nameof(RowOpacity));
    }
}
