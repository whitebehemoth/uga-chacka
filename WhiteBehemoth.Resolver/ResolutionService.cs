using System.Runtime.CompilerServices;
using HomographResolver;

namespace WhiteBehemoth.Resolver;

/// <summary>
/// Resolves homographs one at a time via IAsyncEnumerable, 
/// enabling progressive UI updates.
/// </summary>
public static class ResolutionService
{
    public static async IAsyncEnumerable<ResolvedHomograph> ResolveAsync(
        List<HomographMatch> matches,
        ILlmClient llmClient,
        Func<int, int, Task<bool>>? onLlmError = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int i = 0; i < matches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var match = matches[i];

            LlmChoice choice;
            try
            {
                choice = await llmClient.ResolveHomographAsync(
                    match.SentenceContext, match.Word, match.Variants, ct);
            }
            catch
            {
                if (onLlmError != null && !await onLlmError(i + 1, matches.Count))
                    throw new OperationCanceledException();

                choice = new LlmChoice { Index = 0, Confidence = 0.0 };
            }

            var chosen = match.Variants.FirstOrDefault(v => v.Index == choice.Index)
                         ?? match.Variants[0];

            yield return new ResolvedHomograph
            {
                OriginalWord = match.Word,
                StressedWord = chosen.Target,
                ChosenIndex = choice.Index,
                Reasoning = choice.Reasoning,
                Confidence = choice.Confidence,
                OriginalPosition = match.Start,
                OriginalLength = match.Length,
                Variants = match.Variants.OrderBy(v => v.Target.IndexOf('+')).ToList()
            };
        }
    }
}
