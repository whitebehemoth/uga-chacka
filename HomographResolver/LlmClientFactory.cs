using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HomographResolver;

public sealed class LlmClientFactory : ILlmClientFactory
{
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<LlmSettings> _settings;

    public LlmClientFactory(IServiceProvider services, IOptionsMonitor<LlmSettings> settings)
    {
        _services = services;
        _settings = settings;
    }

    public ILlmClient CreateClient()
    {
        var type = _settings.CurrentValue.Type;
        return type == "FoundryLocal"
            ? _services.GetRequiredService<FoundryLocalLlmClient>()
            : _services.GetRequiredService<OpenAiLlmClient>();
    }
}
