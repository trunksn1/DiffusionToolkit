using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Diffusion.Civitai.Models;

namespace Diffusion.Civitai;

public class CivitaiClient : IDisposable
{
    private readonly string _baseUrl = "https://civitai.com/api/v1";

    private readonly HttpClient _httpClient;

    public string BaseUrl => _baseUrl;

    // Official CivitAI API key. Attached per-request (not as a default header) so
    // the generation-data fetch can retry anonymously when the key is rejected.
    private readonly string? _apiKey;

    // OAuth access token, when the user is signed in. Used for tRPC only: the
    // REST v1 API under _baseUrl rejects OAuth tokens with 401 and accepts API
    // keys only, so the two credentials are not interchangeable.
    private readonly string? _accessToken;

    public CivitaiClient(string? apiKey = null, string? accessToken = null)
    {
        _httpClient = new HttpClient();
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _accessToken = string.IsNullOrWhiteSpace(accessToken) ? null : accessToken.Trim();
    }

    public async Task<Results<LiteModel>?> GetLiteModelsAsync(ModelSearchParameters searchParameters, CancellationToken token)
    {
        string queryString = GetQueryString(searchParameters);

        string apiUrl = $"{_baseUrl}/models{queryString}";

        return await GetResponseResults<Results<LiteModel>>(_httpClient, apiUrl, token);
    }

    public async Task<Results<LiteModel>?> GetLiteModels(string url, CancellationToken token)
    {
        return await GetResponseResults<Results<LiteModel>>(_httpClient, url, token);
    }

    public async Task<Results<Model>?> GetModels(string url, CancellationToken token)
    {
        return await GetResponseResults<Results<Model>>(_httpClient, url, token);
    }


    public async Task<Results<Model>?> GetModelsAsync(ModelSearchParameters searchParameters, CancellationToken token)
    {
        string queryString = GetQueryString(searchParameters);

        string apiUrl = $"{_baseUrl}/models{queryString}";

        return await GetResponseResults<Results<Model>>(_httpClient, apiUrl, token);
    }

    public async Task<ModelVersion2> GetModelVersionsByHashAsync(string hash, CancellationToken token)
    {
        string apiUrl = $"{_baseUrl}/model-versions/by-hash/{hash}";

        return await GetResponseResults<ModelVersion2>(_httpClient, apiUrl, token);
    }

    /// <summary>
    /// Fetches generation data (prompt, sampler, cfg, steps, seed, …) for a single image by its id,
    /// using Civitai's UNOFFICIAL internal tRPC endpoint (the same call the website makes). There is
    /// no documented public REST endpoint to look up an image by id, so this is best-effort: the
    /// response shape is unversioned and may change without notice. Returns null on any failure so
    /// callers can fall back to manual entry. Isolated here so the strategy can be swapped/repaired
    /// in one place.
    /// </summary>
    /// <summary>
    /// Diagnostic message describing the most recent failure of
    /// <see cref="FetchImageGenerationDataAsync"/> (HTTP status, parse miss, timeout, …), or null on
    /// success. The unofficial endpoint never throws, so callers inspect this to log why a fetch
    /// returned no data.
    /// </summary>
    public string? LastError { get; private set; }

    public async Task<CivitaiImageGenerationData?> FetchImageGenerationDataAsync(long imageId, CancellationToken token)
    {
        LastError = null;

        try
        {
            // tRPC superjson input envelope: {"json":{"id":<imageId>}}
            var input = Uri.EscapeDataString($"{{\"json\":{{\"id\":{imageId}}}}}");
            var url = $"https://civitai.com/api/trpc/image.getGenerationData?input={input}";

            HttpRequestMessage BuildRequest(bool withBearer)
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                // Civitai rejects this tRPC endpoint with 401 ("Please use the public API instead")
                // unless the request looks like it came from the website: a real browser User-Agent plus
                // matching Referer/Origin. This is the same origin check the civitai.red scraper fix
                // addressed. Cookies are NOT required for public images.
                req.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                req.Headers.TryAddWithoutValidation("Referer", "https://civitai.com/");
                req.Headers.TryAddWithoutValidation("Origin", "https://civitai.com");
                // Prefer the OAuth token on tRPC: it is the credential that
                // reliably authenticates this endpoint family.
                var bearer = _accessToken ?? _apiKey;
                if (withBearer && bearer != null)
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
                }
                return req;
            }

            using var request = BuildRequest(withBearer: true);
            var response = await _httpClient.SendAsync(request, token);

            // If the credential was rejected here, fall back once to the anonymous
            // spoofed-headers request, which works for public images.
            if ((_accessToken ?? _apiKey) != null &&
                (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                 response.StatusCode == System.Net.HttpStatusCode.Forbidden))
            {
                using var anonymousRequest = BuildRequest(withBearer: false);
                response = await _httpClient.SendAsync(anonymousRequest, token);
            }

            if (!response.IsSuccessStatusCode)
            {
                var snippet = await SafeReadSnippetAsync(response, token);
                LastError = $"HTTP {(int)response.StatusCode} {response.StatusCode} from image.getGenerationData for id {imageId}. {snippet}".Trim();
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(token);

            var parsed = ParseGenerationData(body);
            if (parsed == null)
            {
                LastError = $"Response for id {imageId} had no parsable 'meta' generation block (image may have no embedded metadata).";
            }

            return parsed;
        }
        catch (TaskCanceledException)
        {
            LastError = $"Request for id {imageId} timed out or was cancelled.";
            return null;
        }
        catch (Exception ex)
        {
            // Unofficial endpoint: never throw to the caller.
            LastError = $"Request for id {imageId} failed: {ex.Message}";
            return null;
        }
    }

    /// <summary>Reads a short, exception-safe snippet of a (typically error) response body for logging.</summary>
    private static async Task<string> SafeReadSnippetAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(token);
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;
            return body.Length > 200 ? body.Substring(0, 200) + "…" : body;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Defensively parses the tRPC response body into <see cref="CivitaiImageGenerationData"/>.
    /// Expected shape: { result: { data: { json: { meta: { … }, resources: [ … ] } } } } — but every
    /// level is probed with TryGetProperty so a structural change degrades to null rather than throwing.
    /// Many images have <c>meta: null</c> while still exposing <c>resources</c>, so both are parsed and
    /// the method only returns null when neither yields anything usable.
    /// </summary>
    private static CivitaiImageGenerationData? ParseGenerationData(string body)
    {
        using var document = JsonDocument.Parse(body);

        var root = document.RootElement;

        if (!TryGet(root, "result", out var result)) return null;
        if (!TryGet(result, "data", out var data)) return null;
        if (!TryGet(data, "json", out var json)) return null;

        var gen = new CivitaiImageGenerationData();

        // Generation parameters live under "meta" (often null for images uploaded without metadata).
        if (TryGet(json, "meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            gen.Prompt = ReadString(meta, "prompt");
            gen.NegativePrompt = ReadString(meta, "negativePrompt");
            gen.Sampler = ReadString(meta, "sampler");
            gen.CfgScale = ReadString(meta, "cfgScale");
            gen.Steps = ReadString(meta, "steps");
            gen.Seed = ReadString(meta, "seed");
            gen.Model = ReadString(meta, "Model") ?? ReadString(meta, "model");
            gen.Size = ReadString(meta, "Size") ?? ReadString(meta, "size");
            gen.ClipSkip = ReadString(meta, "clipSkip") ?? ReadString(meta, "Clip skip");

            // Capture any remaining simple scalar fields we didn't explicitly map.
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "prompt", "negativePrompt", "sampler", "cfgScale", "steps", "seed",
                "Model", "model", "Size", "size", "clipSkip", "Clip skip"
            };

            foreach (var property in meta.EnumerateObject())
            {
                if (known.Contains(property.Name)) continue;

                var value = ScalarToString(property.Value);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    gen.Extras[property.Name] = value!;
                }
            }
        }

        // Resources (checkpoint, LoRAs, embeddings) — present even when meta is null.
        if (TryGet(json, "resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
        {
            foreach (var res in resources.EnumerateArray())
            {
                if (res.ValueKind != JsonValueKind.Object) continue;

                var resource = new CivitaiResource
                {
                    ModelName = ReadString(res, "modelName"),
                    ModelType = ReadString(res, "modelType"),
                    BaseModel = ReadString(res, "baseModel"),
                    VersionName = ReadString(res, "versionName"),
                    Strength = ReadString(res, "strength")
                };

                if (!string.IsNullOrWhiteSpace(resource.ModelName))
                {
                    gen.Resources.Add(resource);
                }
            }
        }

        // Nothing usable (no meta fields and no resources) — let the caller report "no data".
        return gen.ToOrderedPairs().Count == 0 ? null : gen;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var value) ? ScalarToString(value) : null;
    }

    private static string? ScalarToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private async Task<T> GetResponseResults<T>(HttpClient client, string url, CancellationToken token) where T: class
    {
        T? results = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (_apiKey != null)
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            }

            var response = await client.SendAsync(request, token);

            if (response.IsSuccessStatusCode)
            {
                var options = new JsonSerializerOptions();
                options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.Converters.Add(new JsonStringEnumConverterWithAttributeSupport());

                using (var responseStream = await response.Content.ReadAsStreamAsync(token))
                {
                    results = await JsonSerializer.DeserializeAsync<T>(responseStream, options);
                }
            }
            else
            {
                if (response.Content.Headers.ContentType?.MediaType == "application/json")
                {
                    var body = await response.Content.ReadAsStringAsync();
                    var document = JsonDocument.Parse(body);

                    string message = "Failed to retrieve results";

                    if (document.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        var arr = document.RootElement.EnumerateArray();
                        var err = arr.First();
                        string path = null;

                        if (err.TryGetProperty("message", out var messageElement))
                        {
                            message = messageElement.GetString();
                        }

                        if (err.TryGetProperty("path", out var pathElement))
                        {
                            path = string.Join("/", pathElement.EnumerateArray().Select(p => p.GetString()));
                        }

                        throw new CivitaiRequestException(message, path, body, response.StatusCode);
                    }
                    else
                    {
                        throw new CivitaiRequestException(message, body, response.StatusCode);
                    }


                }

                throw new CivitaiRequestException("Failed to retrieve results", response.StatusCode);
            }

        }
        catch (TaskCanceledException)
        {
        }

        return results;
    }

    static string GetQueryString<T>(T searchParameters)
    {
        var queryString = new StringBuilder("?");

        // Use reflection to get properties and values from ModelSearchParameters
        var properties = searchParameters.GetType().GetProperties();

        foreach (var property in properties)
        {
            var value = property.GetValue(searchParameters);

            if (value != null)
            {
                var propertyName = ToCamelCase(property.Name);

                if (value is IEnumerable listValue)
                {
                    var objectList = listValue.Cast<object>();

                    if (objectList.First() is Enum)
                    {
                        var enumList = listValue.Cast<Enum>().Select(enumValue => $"{propertyName}={EnumToString(enumValue)}");
                        queryString.Append($"{string.Join("&", enumList)}&");
                    }
                    else
                    {
                        var list = objectList.Select(value => $"{propertyName}={value}&");
                        queryString.Append($"{string.Join("&", list)}&");
                    }


                }
                else if (value is Enum enumValue)
                {
                    queryString.Append($"{propertyName}={EnumToString(enumValue)}&");
                }
                else
                {
                    queryString.Append($"{propertyName}={value}&");
                }
            }
        }

        // Remove the trailing "&" if there are any parameters
        if (queryString.Length > 1)
        {
            queryString.Length--; // Remove the last character
        }

        return queryString.ToString();
    }

    public static string ToCamelCase(string name)
    {
        return name[..1].ToLower() + name[1..];
    }

    static string EnumToString(Enum value)
    {
        switch (value)
        {
            case SortOrder sortOrder:
                return sortOrder switch
                {
                    SortOrder.HighestRated => "Highest Rated",
                    SortOrder.MostDownloaded => "Most Downloaded",
                    SortOrder.MostLiked => "Most Liked",
                    SortOrder.MostDiscussed => "Most Discussed",
                    SortOrder.MostCollected => "Most Collected",
                    SortOrder.Newest => "Newest",
                    _ => throw new ArgumentOutOfRangeException()
                };
            default:
                return value.ToString("G").Replace("_", " "); // Replace underscores with spaces
        }

    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

}