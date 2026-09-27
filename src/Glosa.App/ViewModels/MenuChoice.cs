using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Glosa.App.ViewModels;

/// <summary>
/// One line of a menu that picks a value: what it says, whether it is the one in force, and
/// what to do when it is chosen.
/// </summary>
/// <remarks>
/// Each line carries its own command and its own test rather than reaching back up the tree
/// for them. A submenu is drawn in a popup of its own, so an ancestor lookup from inside it
/// does not find the window's view model.
/// </remarks>
public sealed partial class MenuChoice : ObservableObject
{
    private readonly Action _select;
    private readonly Func<bool> _isSelected;

    public MenuChoice(string label, Func<bool> isSelected, Action select)
    {
        Label = label;
        _isSelected = isSelected;
        _select = select;
        IsSelected = isSelected();
    }

    public string Label { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Asks again whether this is the line in force.</summary>
    public void Refresh() => IsSelected = _isSelected();

    [RelayCommand]
    private void Select() => _select();
}
