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
        foreach (var v in variants)
        {
            sb.Append($"{v.Ref}.");
            sb.Append($" — грамматика: {string.Join("; ", v.GramDef)}");
            if (v.LemmatDef.Count > 0)
                sb.Append($" — значение леммы: {string.Join("; ", v.LemmatDef)}");
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("Какой вариант правильный?");
        return sb.ToString();
    }
}
