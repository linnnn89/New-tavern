namespace TavernDesk.App.ViewModels;

public sealed record InterfaceScaleOption(int Percent, string Label)
{
    public override string ToString() => Label;
}
