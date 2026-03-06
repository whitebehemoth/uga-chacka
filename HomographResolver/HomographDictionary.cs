using System.Text.Json;

namespace HomographResolver;

public class HomographDictionary
{
    private readonly Dictionary<string, List<HomographVariant>> _entries = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<HomographDictionary> LoadAsync(string path)
    {
        var dic = new HomographDictionary();
        await using var stream = File.OpenRead(path);
        var data = await JsonSerializer.DeserializeAsync<Dictionary<string, List<HomographVariant>>>(stream);
        if (data != null)
        {
            foreach (var kvp in data)
                dic._entries[kvp.Key] = kvp.Value;
        }
        return dic;
    }

    public bool TryGetVariants(string wordLower, out List<HomographVariant> variants)
        => _entries.TryGetValue(wordLower, out variants!);

    public int Count => _entries.Count;
}
