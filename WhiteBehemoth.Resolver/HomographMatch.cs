using WhiteBehemoth.Resolver.Models;

namespace WhiteBehemoth.Resolver;

public sealed class HomographMatch
{
    public required int Start { get; init; }
    public required int Length { get; init; }
    public required string Word { get; init; }
    public required List<HomographVariant> Variants { get; init; }
    public required string SentenceContext { get; init; }
}
