using OllamaClient;

namespace OllamaLibraryTester
{
    public class OllamaLibraryTester
    {
        static async Task Main()
        {
            // Example usage for text generation:
            //    OllamaClient client = new("qwen:latest", "http://localhost:11434");

            //    var question = "What is the capital of France?";
            //    var answer = await client.GenerateAsync(question);

            //    Console.WriteLine(answer);



            // Example usage for text embedding:
            var client = new OllamaLibrary.OllamaEmbedding("qwen3-embedding:8b", "http://192.168.107.37:11434");

            // 0.121037655
            var emb1 = await client.EmbedAsync("Kusonóżka (Brachypelma) – rodzaj ptaszników naziemnych występujących w Ameryce Środkowej i Ameryce Południowej. Zamieszkują zarówno wilgotne lasy tropikalne, jak i rejony półpustynne (okolice Meksyku).");
            var emb2 = await client.EmbedAsync("Protokół transferu plików, FTP (od ang. File Transfer Protocol) – protokół komunikacyjny typu klient-serwer wykorzystujący protokół sterowania transmisją (TCP) według modelu TCP/IP (krótko: połączenie TCP), umożliwiający dwukierunkowy transfer plików w układzie serwer FTP–klient FTP.");

            //Console.WriteLine($"Długość wektora: {emb.Length}");

            //for (int i = 0; i < Math.Min(20, emb.Length); i++)
            //{
            //    Console.WriteLine($"{i}: {emb[i]}");
            //}

            Console.WriteLine(VectorMath.Dot(emb1, emb2));
        }
    }
}