using System.Text.RegularExpressions;
using HomographResolver;

namespace WhiteBehemoth.Resolver;

public static partial class TextAnalyzer
{
    [GeneratedRegex(@"[а-яА-ЯёЁ]+")]
    public static partial Regex WordRegex();

    [GeneratedRegex(@"[.!?…]+")]
    private static partial Regex SentenceEndRegex();

    public static List<HomographMatch> FindHomographs(string text, HomographDictionary dictionary)
    {
        var results = new List<HomographMatch>();
        var sentences = SplitSentences(text);

        foreach (Match m in WordRegex().Matches(text))
        {
            if (!dictionary.TryGetVariants(m.Value.ToLowerInvariant(), out var variants))
                continue;

            var (sentIdx, sentStart, sentEnd) = FindSentence(sentences, m.Index);
            var context = text[sentStart..sentEnd];

            if (CountWords(context) < 5 && sentIdx > 0)
            {
                var prev = text[sentences[sentIdx - 1].Start..sentences[sentIdx - 1].End];
                context = prev.TrimEnd() + " " + context;
            }

            results.Add(new HomographMatch
            {
                Start = m.Index,
                Length = m.Length,
                Word = m.Value,
                Variants = variants,
                SentenceContext = context
            });
        }

        return results;
    }

    public static int CountWords(string text) => WordRegex().Matches(text).Count;

    public static int CountSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        int count = 0;
        foreach (Match m in SentenceEndRegex().Matches(text))
        {
            int end = m.Index + m.Length;
            if (end >= text.Length || char.IsWhiteSpace(text[end]))
                count++;
        }

        return count == 0 ? 1 : count;
    }

    public static int CountHomographs(string text, HomographDictionary dictionary)
    {
        int count = 0;
        foreach (Match m in WordRegex().Matches(text))
        {
            if (dictionary.TryGetVariants(m.Value.ToLowerInvariant(), out _))
                count++;
        }
        return count;
    }

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
}
