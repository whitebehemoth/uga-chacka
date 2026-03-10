namespace WhiteBehemoth.Resolver.Llm;

public sealed class LlmClientFactory : ILlmClientFactory
{
    private readonly OpenAiLlmClient _openAi;
    private readonly FoundryLocalLlmClient _foundry;
    private readonly Func<LlmSettings> _getSettings;

    public LlmClientFactory(
        OpenAiLlmClient openAi,
        FoundryLocalLlmClient foundry,
        Func<LlmSettings> getSettings)
    {
        _openAi = openAi;
        _foundry = foundry;
        _getSettings = getSettings;
    }

    public ILlmClient CreateClient() =>
        _getSettings().Type == "FoundryLocal" ? _foundry : _openAi;
}
