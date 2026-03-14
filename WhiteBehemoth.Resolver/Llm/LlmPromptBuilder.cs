using System.Text;
using WhiteBehemoth.Resolver.Models;

namespace WhiteBehemoth.Resolver.Llm;

public static class LlmPromptBuilder
{
    public static string BuildUserPrompt(
        string context, string word, List<HomographVariant> variants)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Предложение: \"{context}\"");
        sb.AppendLine($"Слово-омограф: \"{word}\"");
        sb.AppendLine();
        sb.AppendLine("Варианты:");
        foreach (var v in variants.GroupBy(v => v.Target))
        {
            sb.Append($"ref: [{v.Key}]");
            sb.Append($" - лемма: {v.Select(v => v.Lemma).Aggregate((a, b) => a + "; " + b)}");
            sb.Append($" - грамматика: {v.SelectMany(v => v.GramDef).Aggregate((a, b) => a + "; " + b)}");
            if (v.SelectMany(v => v.LemmatDef).Any())
                sb.Append($" - значение леммы: {v.SelectMany(v => v.LemmatDef).Aggregate((a, b) => a + "; " + b)}");
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("Какой вариант правильный?");
        return sb.ToString();
    }
}
