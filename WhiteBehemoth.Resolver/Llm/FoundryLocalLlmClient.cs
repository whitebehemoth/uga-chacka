using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using WhiteBehemoth.Resolver.Models;

namespace WhiteBehemoth.Resolver.Llm;

public sealed class FoundryLocalLlmClient : IFoundryLocalLlmClient
{
    private readonly Func<LlmSettings> _getSettings;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private OpenAIChatClient? _chatClient;
    private Model? _model;

    public FoundryLocalLlmClient(Func<LlmSettings> getSettings)
    {
        _getSettings = getSettings;
    }

    public async Task<LlmChoice> ResolveHomographAsync(
        string context, string word, List<HomographVariant> variants,
        CancellationToken ct = default)
    {
        var settings = _getSettings();
        var userPrompt = LlmPromptBuilder.BuildUserPrompt(context, word, variants);
        var chatClient = await GetChatClientAsync(ct);

        chatClient.Settings.Temperature = (float)settings.Temperature;

        var response = await chatClient.CompleteChatAsync(new[]
        {
            ChatMessage.FromSystem(settings.SystemPrompt),
            ChatMessage.FromUser(userPrompt)
        });
        try
        {
            var content = response.Choices?[0].Message.Content;
            var jsonStart = content!.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}');
            var jsonStr = content[jsonStart..(jsonEnd + 1)];
            var choice = JsonSerializer.Deserialize<LlmChoice>(jsonStr);
            return choice ?? throw new Exception("Ошибка при Deserialize ответа LLM: ");
        }
        catch (Exception ex)
        {
            throw new Exception("Ошибка при парсинге ответа LLM: " + ex.Message, ex);
        }


    }

    public async Task<ModelInfo?> GetSelectedModelInfoAsync(CancellationToken ct = default)
    {
        var model = await GetModelAsync(ct);
        return model?.SelectedVariant?.Info
               ?? model?.Variants.FirstOrDefault()?.Info;
    }

    public Task PrepareAsync(IProgress<float>? progress = null, CancellationToken ct = default)
        => GetChatClientAsync(ct, progress);

    public async Task<FoundryModelStatus?> GetModelStatusAsync(CancellationToken ct = default)
    {
        var model = await GetModelAsync(ct);
        if (model is null)
            return null;

        var isCached = await model.IsCachedAsync();
        var info = model.SelectedVariant?.Info
                   ?? model.Variants.FirstOrDefault()?.Info;
        var sizeBytes = TryGetSizeBytes(info)
                        ?? TryGetSizeBytes(model.SelectedVariant)
                        ?? TryGetSizeBytes(model);

        return new FoundryModelStatus(model.Id, model.Alias, isCached, sizeBytes);
    }

    private async Task<OpenAIChatClient> GetChatClientAsync(CancellationToken ct, IProgress<float>? progress = null)
    {
        if (_chatClient != null)
            return _chatClient;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_chatClient != null)
                return _chatClient;

            var model = await GetModelAsync(ct);
            if (model is null)
                throw new InvalidOperationException($"Модель '{_getSettings().FoundryModel}' не найдена в Foundry Local.");

            await model.DownloadAsync(p => progress?.Report(p), ct);
            await model.LoadAsync(ct);

            _model = model;
            _chatClient = await model.GetChatClientAsync();
            return _chatClient;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<Model?> GetModelAsync(CancellationToken ct)
    {
        if (_model != null)
            return _model;

        if (!FoundryLocalManager.IsInitialized)
        {
            await FoundryLocalManager.CreateAsync(
                new Configuration { AppName = "uga-chacka" },
                NullLogger.Instance, ct);
        }

        var settings = _getSettings();
        var catalog = await FoundryLocalManager.Instance.GetCatalogAsync(ct);
        var models = await catalog.ListModelsAsync(ct);
        var model = models.FirstOrDefault(m =>
            m.Id.Equals(settings.FoundryModel, StringComparison.InvariantCultureIgnoreCase)
            || (!string.IsNullOrWhiteSpace(m.Alias)
                && m.Alias.Equals(settings.FoundryModel, StringComparison.InvariantCultureIgnoreCase)));

        return model;
    }

    private static long? TryGetSizeBytes(object? value)
    {
        if (value is null)
            return null;

        var type = value.GetType();
        var property = type.GetProperty("SizeBytes")
                       ?? type.GetProperty("SizeInBytes")
                       ?? type.GetProperty("ModelSizeBytes")
                       ?? type.GetProperty("Size");
        if (property is null)
            return null;

        var raw = property.GetValue(value);
        return raw switch
        {
            long size => size,
            int size => size,
            ulong size => unchecked((long)size),
            uint size => size,
            double size => (long)size,
            float size => (long)size,
            _ => null
        };
    }
}
