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
        int nextRequestInMs = 500,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var runningTasks = new Task<ResolvedHomograph>[matches.Count];
        var nextYieldIndex = 0;

        async Task<ResolvedHomograph> ResolveSingleAsync(HomographMatch match, int index)
        {
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
                    if (!await onLlmError(index + 1, matches.Count))
                    {
                        throw new OperationCanceledException();
                    }
                    else
                    {
                        choice = new LlmChoice() { Reasoning = "Ошибка LLM", Confidence = 0, Ref = "<error>" };
                    }
                }
            }

            var chosen = match.Variants.FirstOrDefault(v => choice.Ref.Contains(v.Ref) && (string.IsNullOrEmpty(choice.Lemma) || choice.Lemma.Contains(v.Lemma)));
            if (chosen == null)
            {
                choice.Reasoning = "Ошибка LLM, вариант не найден";
                choice.Confidence = 0;
                chosen = match.Variants[0];
            }

            return new ResolvedHomograph
            {
                OriginalWord = match.Word,
                StressedWord = chosen.Target,
                ChosenIndex = choice.Ref,
                Reasoning = choice.Reasoning,
                Confidence = choice.Confidence,
                OriginalPosition = match.Start,
                OriginalLength = match.Length,
                Variants = match.Variants
                .GroupBy(v => v.Target)
                .Select(g => g.First())
                .OrderBy(v => v.Target.IndexOf('+'))
                .ThenBy(v => v.Target.IndexOf('ё'))
                .ToList()
            };
        }

        for (int i = 0; i < matches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            runningTasks[i] = ResolveSingleAsync(matches[i], i);

            while (nextYieldIndex <= i && runningTasks[nextYieldIndex].IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                yield return await runningTasks[nextYieldIndex];
                nextYieldIndex++;
            }

            if (i < matches.Count - 1 && nextRequestInMs > 0)
            {
                var throttleDelay = Task.Delay(nextRequestInMs, ct);
                while (!throttleDelay.IsCompleted)
                {
                    ct.ThrowIfCancellationRequested();

                    if (nextYieldIndex <= i && runningTasks[nextYieldIndex].IsCompleted)
                    {
                        yield return await runningTasks[nextYieldIndex];
                        nextYieldIndex++;
                        continue;
                    }

                    if (nextYieldIndex <= i)
                        await Task.WhenAny(throttleDelay, runningTasks[nextYieldIndex]);
                    else
                        await throttleDelay;
                }

                await throttleDelay;
            }
        }

        while (nextYieldIndex < runningTasks.Length)
        {
            ct.ThrowIfCancellationRequested();
            yield return await runningTasks[nextYieldIndex];
            nextYieldIndex++;
        }
    }
}
