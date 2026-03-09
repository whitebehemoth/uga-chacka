namespace HomographResolver;

public interface IFoundryLocalLlmClient : ILlmClient
{
    Task<FoundryModelStatus?> GetModelStatusAsync(CancellationToken ct = default);
    Task PrepareAsync(IProgress<float>? progress = null, CancellationToken ct = default);
}
