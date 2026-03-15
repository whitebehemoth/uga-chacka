using WhiteBehemoth.Resolver.Models;

namespace WhiteBehemoth.Resolver.Llm;

public interface ILlmClient
{
    Task<string> ResolveSentenceStressAsync(
        string context,
        CancellationToken ct = default);

    Task<LlmChoice> ResolveHomographAsync(
        string context,
        string word,
        List<HomographVariant> variants,
        CancellationToken ct = default);
}
