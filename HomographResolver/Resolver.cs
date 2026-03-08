using System.Text;
using System.Text.RegularExpressions;

namespace HomographResolver;

public static partial class Resolver
{
    [GeneratedRegex(@"[а-яА-ЯёЁ]+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"[.!?…]+")]
    private static partial Regex SentenceEndRegex();

    public static async Task<(string ResultText, List<ResolvedHomograph> Homographs)> ResolveAsync(
        string text,
        HomographDictionary dictionary,
        ILlmClient llmClient,
        IProgress<(int Current, int Total)>? progress = null,
        CancellationToken ct = default,
        Func<int, int, Task<bool>>? onLlmError = null)
    {
        // Step 1: find every word that appears in the homograph dictionary
        var matches = new List<(int Start, int Length, string Word, List<HomographVariant> Variants)>();
        foreach (Match m in WordRegex().Matches(text))
        {
            var lower = m.Value.ToLowerInvariant();
            if (dictionary.TryGetVariants(lower, out var variants))
                matches.Add((m.Index, m.Length, m.Value, variants));
        }

        if (matches.Count == 0)
            return (text, []);

        // Step 2: split text into sentences for context
        var sentences = SplitSentences(text);

        // Step 3: resolve each homograph via LLM
        var resolved = new List<ResolvedHomograph>();
        for (int i = 0; i < matches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var match = matches[i];

            var (sentIdx, sentStart, sentEnd) = FindSentence(sentences, match.Start);
            var context = text[sentStart..sentEnd];

            // short sentence (<5 words) → prepend the previous one
            if (CountWords(context) < 5 && sentIdx > 0)
            {
                var prev = text[sentences[sentIdx - 1].Start..sentences[sentIdx - 1].End];
                context = prev.TrimEnd() + " " + context;
            }

            LlmChoice choice;
            try
            {
                choice = await llmClient.ResolveHomographAsync(context, match.Word, match.Variants, ct);
            }
            catch
            {
                bool shouldContinue = true;
                if (onLlmError != null)
                {
                    shouldContinue = await onLlmError(i + 1, matches.Count);
                    if (!shouldContinue)
                        throw new OperationCanceledException("Пользователь отменил обработку после ошибки LLM.");
                }
                choice = new LlmChoice 
                { 
                    Index = 0, 
                    Confidence = 0.0 
                };
            }

            var chosen = match.Variants.FirstOrDefault(v => v.Index == choice.Index)
                         ?? match.Variants[0];

            resolved.Add(new ResolvedHomograph
            {
                OriginalWord = match.Word,
                StressedWord = chosen.Target,
                ChosenIndex = choice.Index,
                Reasoning = choice.Reasoning,
                Confidence = choice.Confidence,
                OriginalPosition = match.Start,
                OriginalLength = match.Length,
                Variants = match.Variants.OrderBy(v => v.Target.IndexOf("+")).ToList()
            });

            progress?.Report((i + 1, matches.Count));
        }

        // Step 4: build result text (replace from end so earlier positions stay valid)
        var sb = new StringBuilder(text);
        for (int i = resolved.Count - 1; i >= 0; i--)
        {
            var h = resolved[i];
            sb.Remove(h.OriginalPosition, h.OriginalLength);
            sb.Insert(h.OriginalPosition, h.StressedWord);
        }
        var resultText = sb.ToString();

        // Step 5: compute absolute positions in the result text
        int shift = 0;
        for (int i = 0; i < resolved.Count; i++)
        {
            resolved[i].AbsolutePosition = resolved[i].OriginalPosition + shift;
            resolved[i].Length = resolved[i].StressedWord.Length;
            shift += resolved[i].StressedWord.Length - resolved[i].OriginalLength;
        }

        return (resultText, resolved);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static List<(int Start, int End)> SplitSentences(string text)
    {
        var sentences = new List<(int Start, int End)>();
        int start = 0;

        foreach (Match m in SentenceEndRegex().Matches(text))
        {
            int end = m.Index + m.Length;
            if (end >= text.Length || char.IsWhiteSpace(text[end]))
            {
                sentences.Add((start, end));
                start = end;
                while (start < text.Length && char.IsWhiteSpace(text[start]))
                    start++;
            }
        }

        if (start < text.Length)
            sentences.Add((start, text.Length));

        if (sentences.Count == 0)
            sentences.Add((0, text.Length));

        return sentences;
    }

    private static (int Index, int Start, int End) FindSentence(
        List<(int Start, int End)> sentences, int position)
    {
        for (int i = 0; i < sentences.Count; i++)
        {
            if (position >= sentences[i].Start && position < sentences[i].End)
                return (i, sentences[i].Start, sentences[i].End);
        }
        var last = sentences[^1];
        return (sentences.Count - 1, last.Start, last.End);
    }

    private static int CountWords(string text) => WordRegex().Matches(text).Count;
}
