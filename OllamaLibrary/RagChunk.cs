namespace OllamaLibrary;

public class RagChunk
{
    public long Id { get; set; }
    public string Content { get; set; }
    public Dictionary<string, object>? Metadata { get; set; } = new();
}
