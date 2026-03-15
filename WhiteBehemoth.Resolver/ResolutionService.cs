using System.Runtime.CompilerServices;
using System.Text;
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
        int maxParallelRequests = 1,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _ = maxParallelRequests;
        var occurrenceMap = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < matches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var match = matches[i];

            var sentenceForStress = match.SentenceContext.ToLowerInvariant();
            var occurrenceKey = $"{sentenceForStress}\u001F{NormalizeWord(match.Word)}";
            var occurrence = occurrenceMap.GetValueOrDefault(occurrenceKey);
            occurrenceMap[occurrenceKey] = occurrence + 1;

            HomographVariant? stage1Variant = null;
            LlmChoice stage2Choice;

            try
            {
                var stage1Response = await llmClient.ResolveSentenceStressAsync(sentenceForStress, ct);
                stage1Variant = ResolveFromUppercaseStress(stage1Response, match, occurrence);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                if (!await onLlmError(i + 1, matches.Count))
                    throw new OperationCanceledException();
            }

            try
            {
                stage2Choice = await llmClient.ResolveHomographAsync(
                    match.SentenceContext,
                    match.Word,
                    match.Variants,
                    ct);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                if (!await onLlmError(i + 1, matches.Count))
                    throw new OperationCanceledException();
                else
                    stage2Choice = new() { Reasoning = "Random value due to an LLM error"};
            }

            var stage2Variant = ChooseVariant(match, stage2Choice);

            var isMatch = stage1Variant != null
                          && stage2Variant != null
                          && string.Equals(stage1Variant.Target, stage2Variant.Target, StringComparison.OrdinalIgnoreCase);

            var chosen = stage2Variant ?? stage1Variant ?? match.Variants[0];

            var confidence = isMatch
                ? stage2Choice.Confidence
                : 0.5;

            var reasoning = isMatch
                ? (stage2Choice.Reasoning ?? "")
                : $"(Конфликт) {stage2Choice.Reasoning}";

            yield return CreateResolved(match, chosen, reasoning, confidence);
        }
    }

    private static HomographVariant? ChooseVariant(HomographMatch match, LlmChoice choice)
    {
        return match.Variants.FirstOrDefault(v =>
                   (!string.IsNullOrWhiteSpace(choice.Ref)
                    && (choice.Ref.Contains(v.Ref, StringComparison.OrdinalIgnoreCase)
                        || choice.Ref.Contains(v.Target, StringComparison.OrdinalIgnoreCase)))
                   && (string.IsNullOrWhiteSpace(choice.Lemma)
                       || choice.Lemma.Contains(v.Lemma, StringComparison.OrdinalIgnoreCase)));
    }

    private static ResolvedHomograph CreateResolved(
        HomographMatch match,
        HomographVariant chosen,
        string reasoning,
        double confidence)
    {
        return new ResolvedHomograph
        {
            OriginalWord = match.Word,
            StressedWord = chosen.Target,
            ChosenIndex = chosen.Ref,
            Reasoning = reasoning,
            Confidence = confidence,
            OriginalPosition = match.Start,
            OriginalLength = match.Length,
            Variants = match.Variants.OrderBy(v => v.Target.IndexOf('+')).ToList()
        };
    }

    private static HomographVariant? ResolveFromUppercaseStress(
        string stressedSentence,
        HomographMatch match,
        int occurrence)
    {
        var token = GetWordOccurrence(stressedSentence, NormalizeWord(match.Word), occurrence);
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var tokenWithPlus = ConvertUppercaseStressToPlus(token);
        if (string.IsNullOrWhiteSpace(tokenWithPlus))
            return null;

        return match.Variants.FirstOrDefault(v =>
            string.Equals(v.Target, tokenWithPlus, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetWordOccurrence(string text, string word, int occurrence)
    {
        var current = 0;
        foreach (var token in EnumerateWordTokens(text))
        {
            if (!string.Equals(NormalizeWord(token), word, StringComparison.OrdinalIgnoreCase))
                continue;

            if (current == occurrence)
                return token;

            current++;
        }

        return null;
    }

    private static string? ConvertUppercaseStressToPlus(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var chars = token.ToCharArray();
        var stressIndex = -1;
        for (int i = 0; i < chars.Length; i++)
        {
            if (IsRussianUpperVowel(chars[i]))
            {
                stressIndex = i;
                break;
            }
        }

        if (stressIndex < 0)
            return null;

        var lowerToken = token.ToLowerInvariant();
        return lowerToken.Insert(stressIndex, "+");
    }

    private static IEnumerable<string> EnumerateWordTokens(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (ch == '+' || IsRussianLetter(ch))
            {
                sb.Append(ch);
                continue;
            }

            if (sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
        }

        if (sb.Length > 0)
            yield return sb.ToString();
    }

    private static string NormalizeWord(string token)
    {
        return token
            .Replace("+", string.Empty)
            .Replace("\u0301", string.Empty)
            .ToLowerInvariant();
    }

    private static bool IsRussianLetter(char c)
        => (c >= 'а' && c <= 'я') || (c >= 'А' && c <= 'Я') || c is 'ё' or 'Ё';

    private static bool IsRussianVowel(char c)
        => c is 'а' or 'е' or 'ё' or 'и' or 'о' or 'у' or 'ы' or 'э' or 'ю' or 'я'
            or 'А' or 'Е' or 'Ё' or 'И' or 'О' or 'У' or 'Ы' or 'Э' or 'Ю' or 'Я';
    private static bool IsRussianUpperVowel(char c)
        => c is 'А' or 'Е' or 'Ё' or 'И' or 'О' or 'У' or 'Ы' or 'Э' or 'Ю' or 'Я';
}
