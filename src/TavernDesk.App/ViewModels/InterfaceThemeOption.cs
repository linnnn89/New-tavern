namespace TavernDesk.App.ViewModels;

public sealed record InterfaceThemeOption(string Value, string Label)
{
    public override string ToString() => Label;
}
