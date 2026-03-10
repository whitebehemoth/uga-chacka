namespace WhiteBehemoth.Yara.Models;

public sealed class FoundryModelItem
{
    public required string Id { get; init; }
    public required bool IsCached { get; init; }
    public string StatusIcon => IsCached ? "✓" : "✗";
    public string DisplayText => $"{Id} ({(IsCached ? "скачана" : "не скачана")})";
}
