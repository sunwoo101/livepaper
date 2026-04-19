using System.Net.Http;

namespace livepaper;

public static class HttpClientProvider
{
    public const string UserAgent = "Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0";

    private static readonly HttpClientHandler _handler = new()
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    };

    private static readonly HttpClient _client = new(_handler);

    public static HttpClient Client => _client;
}
