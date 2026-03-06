using System.Text;
using System.Text.Json;

namespace OllamaLibrary;

public class OllamaEmbedding(string model, string baseUrl) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl) };
    private readonly string _model = model;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };


    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var request = new OllamaEmbeddingRequest
        {
            Model = _model,
            Prompt = text
        };

        var json = JsonSerializer.Serialize(request, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("/api/embeddings", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var embedResponse = JsonSerializer.Deserialize<OllamaEmbeddingResponse>(responseJson, _jsonOptions);

        if (embedResponse == null || embedResponse.Embedding == null)
            throw new InvalidOperationException("Pusta odpowiedź embedding z Ollama.");

        return embedResponse.Embedding;
    }

    public class OllamaEmbeddingRequest
    {
        public string Model { get; set; }
        public string Prompt { get; set; }
    }

    public class OllamaEmbeddingResponse
    {
        public string Model { get; set; }
        public float[] Embedding { get; set; }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}