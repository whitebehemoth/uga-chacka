using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomographResolver;

public sealed class FoundryLocalLlmClient : ILlmClient, IDisposable
{
    private readonly IOptionsMonitor<LlmSettings> _settings;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private OpenAIChatClient? _chatClient;
    private Model? _model;

    public FoundryLocalLlmClient(IOptionsMonitor<LlmSettings> settings)
    {
        _settings = settings;
    }

    public async Task<LlmChoice> ResolveHomographAsync(
        string context, string word, List<HomographVariant> variants,
        CancellationToken ct = default)
    {
        var settings = _settings.CurrentValue;
        var userPrompt = LlmPromptBuilder.BuildUserPrompt(context, word, variants);
        var chatClient = await GetChatClientAsync(ct);

        chatClient.Settings.Temperature = (float)settings.Temperature;

        var response = await chatClient.CompleteChatAsync(new[]
        {
            ChatMessage.FromSystem(settings.SystemPrompt),
            ChatMessage.FromUser(userPrompt)
        });

        var content = response.Choices?[0].Message.Content;
        if (string.IsNullOrWhiteSpace(content))
            return new LlmChoice { Index = 0, Confidence = 0.0 };

        try
        {
            var jsonStart = content.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonStr = content[jsonStart..(jsonEnd + 1)];
                var choice = System.Text.Json.JsonSerializer.Deserialize<LlmChoice>(jsonStr);
                if (choice != null && choice.Index >= 0 && choice.Index <= variants.Count)
                    return choice;
            }
        }
        catch
        {
        }

        return new LlmChoice { Index = 0, Confidence = 0.0 };
    }

    private async Task<OpenAIChatClient> GetChatClientAsync(CancellationToken ct)
    {
        if (_chatClient != null)
            return _chatClient;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_chatClient != null)
                return _chatClient;

            if (!FoundryLocalManager.IsInitialized)
            {
                await FoundryLocalManager.CreateAsync(
                    new Configuration { AppName = "uga-chacka" },
                    NullLogger.Instance);
            }

            var settings = _settings.CurrentValue;
            var catalog = await FoundryLocalManager.Instance.GetCatalogAsync();
            var mmm = await catalog.ListModelsAsync();
            var model = mmm.FirstOrDefault(m => m.Id.Equals(settings.FoundryModel, StringComparison.InvariantCultureIgnoreCase));

            if (model is null)
                throw new InvalidOperationException($"Модель '{settings.FoundryModel}' не найдена в Foundry Local.");

            await model.DownloadAsync();
            await model.LoadAsync();

            _model = model;
            _chatClient = await model.GetChatClientAsync();
            return _chatClient;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public void Dispose()
    {
        _model = null;
        if (FoundryLocalManager.IsInitialized)
            FoundryLocalManager.Instance.Dispose();
    }
}
