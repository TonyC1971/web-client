namespace ClassicUO.Game.UI.Gumps;

public record MenuGumpItemViewMetadata
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required ushort Graphic { get; init; }
    public required ushort Hue { get; init; }
}