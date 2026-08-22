using System.Net.Http.Json;
using System.Text.Json.Serialization;
using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Infrastructure;

public sealed class LocalInferenceRouter : ILocalInferenceRouter
{
    private readonly ILocalInferenceRegistry registry;
    private readonly HttpClient httpClient;

    public LocalInferenceRouter(ILocalInferenceRegistry registry, HttpClient httpClient)
    {
        this.registry = registry;
        this.httpClient = httpClient;
    }

    public async Task<LocalChatResponse> ChatAsync(LocalChatRequest request, CancellationToken cancellationToken)
    {
        var provider = registry.GetActiveProvider();
        var model = string.IsNullOrWhiteSpace(request.Model) ? provider.Model.UpstreamId : request.Model;
        var endpoint = provider.Runtime.OpenAiCompatibleEndpoint.TrimEnd('/') + "/chat/completions";

        var payload = new OpenAiChatRequest(
            model!,
            request.Messages.Select(message => new OpenAiChatMessage(message.Role, message.Content)).ToList(),
            request.Temperature,
            request.MaxTokens,
            request.ResponseFormat);

        try
        {
            using var response = await httpClient.PostAsJsonAsync(endpoint, payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new LocalChatResponse(provider.ProviderId, model!, string.Empty, false, $"Provider returned HTTP {(int)response.StatusCode}.");
            }

            var completion = await response.Content.ReadFromJsonAsync<OpenAiChatResponse>(cancellationToken);
            var content = completion?.Choices.FirstOrDefault()?.Message.Content ?? string.Empty;
            return new LocalChatResponse(provider.ProviderId, model!, content, true, null);
        }
        catch (HttpRequestException exception)
        {
            return new LocalChatResponse(provider.ProviderId, model!, string.Empty, false, $"Local provider unavailable: {exception.Message}");
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new LocalChatResponse(provider.ProviderId, model!, string.Empty, false, $"Local provider timed out: {exception.Message}");
        }
    }

    private sealed record OpenAiChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<OpenAiChatMessage> Messages,
        [property: JsonPropertyName("temperature")] double? Temperature,
        [property: JsonPropertyName("max_tokens")] int? MaxTokens,
        [property: JsonPropertyName("response_format"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] System.Text.Json.JsonElement? ResponseFormat);

    private sealed record OpenAiChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record OpenAiChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<OpenAiChoice> Choices);

    private sealed record OpenAiChoice(
        [property: JsonPropertyName("message")] OpenAiChatMessage Message);
}
