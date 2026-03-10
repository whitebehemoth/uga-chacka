namespace WhiteBehemoth.Resolver.Llm;

public sealed record LlmSettings(
    string Type = "OpenAI",
    string Url = "",
    string Model = "",
    string ApiKey = "",
    string FoundryModel = "",
    double Temperature = 0.3,
    string SystemPrompt = ""
);
