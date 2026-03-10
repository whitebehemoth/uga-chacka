namespace WhiteBehemoth.Resolver.Models
{
    public sealed record FoundryModelStatus(string Id, string? Alias, bool IsCached, long? SizeBytes);
}
