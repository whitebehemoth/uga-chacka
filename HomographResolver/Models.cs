using System.Text.Json.Serialization;

namespace HomographResolver;

public class HomographVariant
{
    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("gram_def")]
    public List<string> GramDef { get; set; } = [];

    [JsonPropertyName("frequency")]
    public int Frequency { get; set; }

    [JsonPropertyName("lemma")]
    public string Lemma { get; set; } = "";

    [JsonPropertyName("stress_pos")]
    public int StressPos { get; set; }

    [JsonPropertyName("lemma_def")]
    public List<string> LemmatDef { get; set; } = [];
}
public class ResolvedHomograph
{
    public string OriginalWord { get; set; } = "";
    public string StressedWord { get; set; } = "";
    public int ChosenIndex { get; set; }
    public double Confidence { get; set; }
    public string Reasoning { get; set; } = "";
    public int OriginalPosition { get; set; }
    public int OriginalLength { get; set; }
    public int AbsolutePosition { get; set; }
    public int Length { get; set; }
    public List<HomographVariant> Variants { get; set; } = [];
}

public class LlmChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("reasoning")]
    public string Reasoning { get; set; } = "";
}

public class LlmSettings
{
    public string Type { get; set; } = "OpenAI";
    public string Url { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-4o-mini";
    public string ApiKey { get; set; } = "";
    public double Temperature { get; set; } = 0.1;

    public string SystemPrompt { get; set; } =
        "Ты — эксперт-лингвист по русскому языку. " +
        "Тебе предоставляется предложение с омографом и варианты произношения " +
        "с грамматическими и смысловыми пояснениями.\n\n" +
        "Выбери правильный вариант ударения на основе контекста предложения.\n" +
        "Ответь СТРОГО в формате JSON без какого-либо дополнительного текста:\n" +
        "{\"index\": <номер выбранного варианта>, \"confidence\": <уверенность от 0.0 до 1.0>}";
}
