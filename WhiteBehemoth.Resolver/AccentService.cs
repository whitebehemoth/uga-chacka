using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhiteBehemoth.Resolver;

public static class AccentService
{
    public static List<KeyValuePair<string, string>> LoadStressEntries(
        string path, HashSet<string> words)
    {
        var results = new List<KeyValuePair<string, string>>();

        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!words.Contains(prop.Name)) continue;
            if (prop.Value.ValueKind != JsonValueKind.String) continue;

            var stressed = prop.Value.GetString();
            if (stressed != null && stressed.Contains('+'))
                results.Add(new KeyValuePair<string, string>(prop.Name, stressed));
        }

        return results;
    }

    public static string ApplyStressMarks(string text, Dictionary<string, string> stressMap)
    {
        var matches = TextAnalyzer.WordRegex().Matches(text);
        if (matches.Count == 0) return text;

        var sb = new StringBuilder(text);
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            if (match.Value.Contains('+'))
                continue;

            var key = match.Value.ToLowerInvariant();
            if (!stressMap.TryGetValue(key, out var stressed))
                continue;

            int plusPos = stressed.IndexOf('+');
            if (plusPos < 0) continue;

            sb.Insert(match.Index + plusPos, "+");
        }

        return sb.ToString();
    }

    public static Dictionary<string, string> LoadStressPhraseEntries(string path)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
                continue;

            var stressed = prop.Value.GetString();
            if (string.IsNullOrWhiteSpace(stressed) || !stressed.Contains('+'))
                continue;

            results[prop.Name] = stressed;
        }

        return results;
    }

    public static string ApplyStressPhrases(string text, IReadOnlyDictionary<string, string> phraseMap)
    {
        if (string.IsNullOrWhiteSpace(text) || phraseMap.Count == 0)
            return text;

        var orderedKeys = phraseMap.Keys
            .OrderByDescending(k => k.Length)
            .Select(Regex.Escape)
            .ToArray();

        if (orderedKeys.Length == 0)
            return text;

        var pattern = $@"(?<![\p{{L}}\p{{Nd}}_+])(?:{string.Join("|", orderedKeys)})(?![\p{{L}}\p{{Nd}}_+])";
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return regex.Replace(text, match =>
        {
            if (!phraseMap.TryGetValue(match.Value, out var stressed))
                return match.Value;

            return PreserveFirstLetterCase(match.Value, stressed);
        });
    }

    private static string PreserveFirstLetterCase(string original, string replacement)
    {
        if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(replacement))
            return replacement;

        var originalFirst = original[0];
        var replacementFirst = replacement[0];
        if (!char.IsLetter(replacementFirst) && replacementFirst != '+')
            return replacement;

        var targetIndex = replacementFirst == '+' && replacement.Length > 1 ? 1 : 0;
        if (targetIndex >= replacement.Length)
            return replacement;

        if (!char.IsLetter(replacement[targetIndex]))
            return replacement;

        if (char.IsUpper(originalFirst))
        {
            var chars = replacement.ToCharArray();
            chars[targetIndex] = char.ToUpper(chars[targetIndex]);
            return new string(chars);
        }

        return replacement;
    }
}

