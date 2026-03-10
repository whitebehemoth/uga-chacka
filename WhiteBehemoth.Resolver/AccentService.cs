using System.Text;
using System.Text.Json;

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
}

