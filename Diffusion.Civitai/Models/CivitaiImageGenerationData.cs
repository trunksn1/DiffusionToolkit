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

        return pairs;
    }
}
