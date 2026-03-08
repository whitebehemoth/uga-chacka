namespace HomographResolver;

public interface ILlmClient
{
    Task<LlmChoice> ResolveHomographAsync(
        string context,
        string word,
        List<HomographVariant> variants,
        CancellationToken ct = default);
}
