namespace Glosa.App.ViewModels;

/// <summary>A value a combo box offers, under the words it is shown with.</summary>
/// <remarks>
/// The words are also what the item is read out as: UI Automation names a closed combo box's
/// selection by the item's <see cref="ToString"/>, not by what its template shows.
/// </remarks>
public sealed record Choice(object Value, string Label)
{
    public override string ToString() => Label;
}
