namespace Diffusion.Civitai.Models;

/// <summary>
/// Generation data for a single Civitai image, as returned by the (unofficial) tRPC
/// <c>image.getGenerationData</c> endpoint. Parsed defensively: any field may be absent.
/// </summary>
public class CivitaiImageGenerationData
{
    public string? Prompt { get; set; }
    public string? NegativePrompt { get; set; }
    public string? Sampler { get; set; }
    public string? CfgScale { get; set; }
    public string? Steps { get; set; }
    public string? Seed { get; set; }
    public string? Model { get; set; }
    public string? Size { get; set; }
    public string? ClipSkip { get; set; }

    /// <summary>Any additional scalar fields present in the meta object, keyed by their original name.</summary>
    public Dictionary<string, string> Extras { get; } = new();

    /// <summary>
    /// Models/LoRAs/embeddings Civitai associates with the image. Present even when <c>meta</c> is null
    /// (many images expose resources but no prompt/parameters), so this is often the only data available.
    /// </summary>
    public List<CivitaiResource> Resources { get; } = new();

    /// <summary>
    /// Flattens the known and extra fields into an ordered list of key/value pairs suitable for the
    /// overlay editor / confirmation dialog. Empty values are omitted.
    /// </summary>
    public List<KeyValuePair<string, string>> ToOrderedPairs()
    {
        var pairs = new List<KeyValuePair<string, string>>();

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                pairs.Add(new KeyValuePair<string, string>(key, value!.Trim()));
            }
        }

        Add("Prompt", Prompt);
        Add("Negative Prompt", NegativePrompt);
        Add("Steps", Steps);
        Add("Sampler", Sampler);
        Add("CFG Scale", CfgScale);
        Add("Seed", Seed);
        Add("Model", Model);
        Add("Size", Size);
        Add("Clip Skip", ClipSkip);

        foreach (var extra in Extras)
        {
            Add(extra.Key, extra.Value);
        }

        // Resources (checkpoint, LoRAs, …). Key them by type, numbering when a type repeats, so the
        // overlay's key/value rows stay unique. Value: "Name (BaseModel) @ Strength".
        var typeCounts = Resources
            .GroupBy(r => NormalizeType(r.ModelType))
            .ToDictionary(g => g.Key, g => g.Count());
        var typeIndex = new Dictionary<string, int>();

        foreach (var resource in Resources)
        {
            if (string.IsNullOrWhiteSpace(resource.ModelName)) continue;

            var type = NormalizeType(resource.ModelType);
            var key = type;
            if (typeCounts.TryGetValue(type, out var count) && count > 1)
            {
                var n = typeIndex.TryGetValue(type, out var i) ? i + 1 : 1;
                typeIndex[type] = n;
                key = $"{type} {n}";
            }

            var value = resource.ModelName!.Trim();
            if (!string.IsNullOrWhiteSpace(resource.BaseModel)) value += $" ({resource.BaseModel!.Trim()})";
            if (!string.IsNullOrWhiteSpace(resource.Strength)) value += $" @ {resource.Strength!.Trim()}";

            Add(key, value);
        }

        return pairs;
    }

    private static string NormalizeType(string? modelType)
    {
        if (string.IsNullOrWhiteSpace(modelType)) return "Resource";
        // Civitai uses "LORA" / "Checkpoint" / "TextualInversion" etc.; present them more readably.
        return modelType.Trim().ToUpperInvariant() switch
        {
            "LORA" => "LoRA",
            "LOCON" => "LyCORIS",
            "CHECKPOINT" => "Checkpoint",
            "TEXTUALINVERSION" => "Embedding",
            "VAE" => "VAE",
            _ => modelType.Trim()
        };
    }
}

/// <summary>A model/LoRA/embedding associated with a Civitai image (from the response's <c>resources</c>).</summary>
public class CivitaiResource
{
    public string? ModelName { get; set; }
    public string? ModelType { get; set; }
    public string? BaseModel { get; set; }
    public string? VersionName { get; set; }
    public string? Strength { get; set; }
}
