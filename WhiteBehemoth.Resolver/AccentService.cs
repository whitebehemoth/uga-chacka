using System.Buffers;
using System.Text;
using System.Text.Json;

namespace WhiteBehemoth.Resolver;

public readonly record struct StressEntry(int StressPos, int? StressPos2);

public static class AccentService
{
    public static List<KeyValuePair<string, StressEntry>> LoadStressEntries(
        string path, HashSet<string> words)
    {
        var results = new List<KeyValuePair<string, StressEntry>>();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var state = new JsonReaderState();
        int bytesInBuffer = 0;
        bool isFinalBlock = false;

        try
        {
            using var stream = File.OpenRead(path);
            while (!isFinalBlock)
            {
                int bytesRead = stream.Read(buffer, bytesInBuffer, buffer.Length - bytesInBuffer);
                if (bytesRead == 0)
                    isFinalBlock = true;

                bytesInBuffer += bytesRead;
                var reader = new Utf8JsonReader(
                    new ReadOnlySpan<byte>(buffer, 0, bytesInBuffer), isFinalBlock, state);

                while (reader.Read())
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        continue;

                    var word = reader.GetString();
                    if (!reader.Read())
                        break;

                    if (word != null && words.Contains(word))
                    {
                        if (!TryReadStressEntry(ref reader, out var entry))
                            break;
                        if (entry.StressPos > 0)
                            results.Add(new KeyValuePair<string, StressEntry>(word, entry));
                    }
                    else
                    {
                        if (!reader.TrySkip())
                            break;
                    }
                }

                state = reader.CurrentState;
                int consumed = (int)reader.BytesConsumed;
                bytesInBuffer -= consumed;
                if (bytesInBuffer > 0)
                    Buffer.BlockCopy(buffer, consumed, buffer, 0, bytesInBuffer);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return results;
    }

    public static string ApplyStressMarks(string text, Dictionary<string, StressEntry> stressMap)
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
            if (!stressMap.TryGetValue(key, out var entry))
                continue;

            int pos = entry.StressPos;
            if (pos <= 0 || pos > match.Value.Length)
                continue;

            sb.Insert(match.Index + pos - 1, "+");
        }

        return sb.ToString();
    }

    private static bool TryReadStressEntry(ref Utf8JsonReader reader, out StressEntry entry)
    {
        entry = default;
        if (reader.TokenType != JsonTokenType.StartObject)
            return true;

        int stressPos = 0;
        int? stressPos2 = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (stressPos > 0)
                    entry = new StressEntry(stressPos, stressPos2);
                return true;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var property = reader.GetString();
            if (!reader.Read())
                return false;

            if (property == "stress_pos" && reader.TokenType == JsonTokenType.Number
                && reader.TryGetInt32(out var sv))
                stressPos = sv;
            else if (property == "stress_pos2" && reader.TokenType == JsonTokenType.Number
                     && reader.TryGetInt32(out var sv2))
                stressPos2 = sv2;
            else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                if (!reader.TrySkip())
                    return false;
            }
        }

        return false;
    }
}
