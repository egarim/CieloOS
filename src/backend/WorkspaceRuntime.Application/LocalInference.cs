namespace WorkspaceRuntime.Application;

public sealed record LocalInferenceConfig(
    int Schema,
    string ActiveProviderId,
    string RegistryPath,
    LocalInferenceApiConfig Api,
    LocalInferenceRoutingConfig Routing);

public sealed record LocalInferenceApiConfig(string Compatibility, string InternalBaseUrl);

public sealed record LocalInferenceRoutingConfig(
    bool AllowCloudFallback,
    bool RequireApprovalForCloudFallback,
    bool RequireApprovalForModelDownload);

public sealed record LocalInferenceRegistry(
    int Schema,
    string DefaultProviderId,
    IReadOnlyList<LocalInferenceRegistryEntry> Providers);

public sealed record LocalInferenceRegistryEntry(
    string Id,
    string DisplayName,
    string Provider,
    string Manifest,
    string Role);

public sealed record LocalInferenceProviderProfile(
    int Schema,
    string ProviderId,
    string DisplayName,
    string Vendor,
    LocalInferenceModelInfo Model,
    LocalInferenceRuntimeInfo Runtime,
    LocalInferenceCapabilities Capabilities,
    LocalInferenceHardware Hardware);

public sealed record LocalInferenceModelInfo(
    string UpstreamId,
    string Source,
    string Family,
    string BaseModel,
    string Parameters,
    string Quantization,
    decimal ApproximateSizeGb,
    string License,
    LocalInferenceArtifact? Artifact = null);

public sealed record LocalInferenceArtifact(
    string FileName,
    string Url,
    string Sha256);

public sealed record LocalInferenceRuntimeInfo(
    string Engine,
    string OpenAiCompatibleEndpoint,
    string Executable,
    IReadOnlyList<string> Args,
    string HealthPath,
    int ReadinessTimeoutSeconds,
    string NetworkScope,
    string PullPolicy,
    LocalInferenceRuntimeSource? Source = null);

public sealed record LocalInferenceRuntimeSource(
    string Repository,
    string Revision);

public sealed record LocalInferenceCapabilities(
    bool ToolUse,
    bool MultiStepReasoning,
    bool DocumentSummary,
    bool SpreadsheetAnalysis,
    bool Vision);

public sealed record LocalInferenceHardware(
    bool CpuFallback,
    bool GpuOptional,
    int MinimumMemoryGb,
    int RecommendedMemoryGb);

public sealed record LocalInferenceStatus(
    bool Configured,
    string? ActiveProviderId,
    string? StableEndpoint,
    LocalInferenceProviderProfile? ActiveProvider,
    IReadOnlyList<LocalInferenceRegistryEntry> AvailableProviders);

public sealed record ChatMessageDto(string Role, string Content);

// ResponseFormat is forwarded verbatim to the provider as OpenAI-style
// response_format (e.g. {"type":"json_schema","json_schema":{...}}), which
// llama.cpp honors as constrained decoding — the mechanism that lets a small
// local model emit only structurally valid tool commands.
public sealed record LocalChatRequest(
    string? Model,
    IReadOnlyList<ChatMessageDto> Messages,
    double? Temperature = null,
    int? MaxTokens = null,
    System.Text.Json.JsonElement? ResponseFormat = null);

public sealed record LocalChatResponse(
    string ProviderId,
    string Model,
    string Content,
    bool Forwarded,
    string? Error);

public interface ILocalInferenceRegistry
{
    LocalInferenceStatus GetStatus();
    LocalInferenceProviderProfile GetActiveProvider();
}

public interface ILocalInferenceRouter
{
    Task<LocalChatResponse> ChatAsync(LocalChatRequest request, CancellationToken cancellationToken);
}
