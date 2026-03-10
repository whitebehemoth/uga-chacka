using System.Runtime.CompilerServices;
using WhiteBehemoth.Resolver.Llm;
using WhiteBehemoth.Resolver.Models;

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
        Func<int, int, Task<bool>> onLlmError,
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
            catch (OperationCanceledException) { throw; }
            catch
            {
                // First failure → retry once after 500ms
                try
                {
                    await Task.Delay(500, ct);
                    choice = await llmClient.ResolveHomographAsync(
                        match.SentenceContext, match.Word, match.Variants, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // Second failure → ask user
                    if (!await onLlmError(i + 1, matches.Count))
                    {
                        throw new OperationCanceledException();
                    }
                    else
                    {
                        choice = new LlmChoice() { Reasoning = "Ошибка LLM", Confidence = 0, Ref = "cч" };
                    }
                }
            }

            var chosen = match.Variants.FirstOrDefault(v => v.Ref == choice.Ref)
                         ?? match.Variants[0];

            yield return new ResolvedHomograph
            {
                OriginalWord = match.Word,
                StressedWord = chosen.Target,
                ChosenIndex = choice.Ref,
                Reasoning = choice.Reasoning,
                Confidence = choice.Confidence,
                OriginalPosition = match.Start,
                OriginalLength = match.Length,
                Variants = match.Variants.OrderBy(v => v.Target.IndexOf('+')).ToList()
            };
        }
    }
}
