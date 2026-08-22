using System.Text.Json;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

public class InferenceRegistryTests
{
    [Fact]
    public void Local_inference_registry_points_to_replaceable_provider_profiles()
    {
        var root = FindRepositoryRoot();
        var registryPath = Path.Combine(root, "distro", "models", "registry.json");
        using var registryJson = JsonDocument.Parse(File.ReadAllText(registryPath));

        var defaultProviderId = registryJson.RootElement.GetProperty("defaultProviderId").GetString();
        var providers = registryJson.RootElement.GetProperty("providers").EnumerateArray().ToList();

        Assert.False(string.IsNullOrWhiteSpace(defaultProviderId));
        Assert.True(providers.Count >= 2);
        Assert.Contains(providers, provider => provider.GetProperty("id").GetString() == defaultProviderId);

        foreach (var provider in providers)
        {
            var manifest = provider.GetProperty("manifest").GetString();
            Assert.False(string.IsNullOrWhiteSpace(manifest));
            Assert.True(File.Exists(Path.Combine(root, "distro", manifest!)));
        }
    }

    [Fact]
    public void File_registry_loads_active_provider_without_coupling_to_vendor()
    {
        var registry = new FileLocalInferenceRegistry(FindRepositoryRoot());

        var status = registry.GetStatus();

        Assert.Equal("prism-bonsai-4b", status.ActiveProviderId);
        Assert.Equal("http://127.0.0.1:5148/v1", status.StableEndpoint);
        Assert.Equal("prism-ml/Ternary-Bonsai-4B-gguf", status.ActiveProvider.Model.UpstreamId);
        Assert.Equal("prism-bonsai-4b", status.ActiveProvider.ProviderId);
        Assert.Equal("local-only", status.ActiveProvider.Runtime.NetworkScope);
        Assert.Equal("llama-server", status.ActiveProvider.Runtime.Executable);
        Assert.Contains("-m", status.ActiveProvider.Runtime.Args);
        Assert.Contains(status.ActiveProvider.Runtime.Args, argument =>
            argument.StartsWith("/opt/workspace-runtime/models/", StringComparison.Ordinal));
        Assert.Equal("4e0bf8b737b0431552f8c2c97695ab7c0cb214c94bcdeb4f5f267e67ddf28b8b", status.ActiveProvider.Model.Artifact?.Sha256);
        Assert.Equal("9ca265a57f85f2117942490f421f64a226dd9847", status.ActiveProvider.Runtime.Source?.Revision);
        Assert.Contains(status.AvailableProviders, provider => provider.Id == "qwen3-4b");
    }

    [Fact]
    public async Task Router_forwards_to_active_provider_openai_compatible_endpoint()
    {
        var registry = new FileLocalInferenceRegistry(FindRepositoryRoot());
        var handler = new RecordingHandler();
        var router = new LocalInferenceRouter(registry, new HttpClient(handler));

        var response = await router.ChatAsync(new LocalChatRequest(
            null,
            new[] { new ChatMessageDto("user", "Draft a spreadsheet plan.") }), CancellationToken.None);

        Assert.True(response.Forwarded);
        Assert.Equal("prism-bonsai-4b", response.ProviderId);
        Assert.Equal("routed response", response.Content);
        Assert.Equal("http://127.0.0.1:8080/v1/chat/completions", handler.RequestUri?.ToString());
        Assert.Contains("prism-ml/Ternary-Bonsai-4B-gguf", handler.Body);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "WorkspaceRuntime.sln")) &&
               !File.Exists(Path.Combine(directory.FullName, "WorkspaceRuntime.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "choices": [
                        {
                          "message": {
                            "role": "assistant",
                            "content": "routed response"
                          }
                        }
                      ]
                    }
                    """)
            };
        }
    }
}
