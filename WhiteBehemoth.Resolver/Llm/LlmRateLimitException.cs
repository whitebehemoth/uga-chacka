namespace WhiteBehemoth.Resolver.Llm;

public sealed class LlmRateLimitException(string message, Exception? inner = null)
    : Exception(message, inner);
