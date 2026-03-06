namespace chat;

public class Utils
{
    public class AppSettings
    {
        public ConnectionStringsSettings ConnectionStrings { get; set; } = new();
        public OllamaSettings Ollama { get; set; } = new();
    }

    public class ConnectionStringsSettings
    {
        public string PostgreSQL { get; set; } = "";
    }

    public class OllamaSettings
    {
        public string Model { get; set; } = "";
        public string BaseUrl { get; set; } = "";
    }

}