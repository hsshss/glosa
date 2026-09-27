using CommunityToolkit.Mvvm.ComponentModel;

namespace Glosa.App.ViewModels;

/// <summary>
/// One line of the port map window's list of target modules: which, and whether the map
/// being edited claims it.
/// </summary>
public sealed partial class ModuleClaimViewModel : ObservableObject
{
    private readonly Action<ModuleClaimViewModel> _changed;

    public ModuleClaimViewModel(string module, bool claimed, Action<ModuleClaimViewModel> changed)
    {
        Module = module;
        Claimed = claimed;
        // Set after the first value, so taking it up is not a change.
        _changed = changed;
    }

    public string Module { get; }

    [ObservableProperty]
    public partial bool Claimed { get; set; }

    partial void OnClaimedChanged(bool value) => _changed?.Invoke(this);
}
