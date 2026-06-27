using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.Civitai;
using Diffusion.Civitai.Models;
using Diffusion.Toolkit.Localization;
using Diffusion.Toolkit.Models;

namespace Diffusion.Toolkit.Services;

/// <summary>
/// Fetches generation metadata for an image from its Civitai page and stores it in the user-metadata
/// overlay (Source = "civitai"). Uses Civitai's unofficial tRPC endpoint via <see cref="CivitaiClient"/>;
/// on any failure it falls back to letting the user enter data manually.
/// </summary>
public class CivitaiImageService
{
    // Matches the Civitai image id embedded in download filenames, e.g.
    // 2026-06-23_05-10-56_8435__CIV_ID__134578059.png  ->  134578059
    private static readonly Regex FilenameIdRegex =
        new(@"CIV_ID__(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private string GetLocalizedText(string key)
    {
        return (string)JsonLocalizationProvider.Instance.GetLocalizedObject(key, null, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Derives the image's Civitai id from its filename (the <c>CIV_ID__&lt;id&gt;</c> marker added by the
    /// collections scraper), fetches its generation data, asks the user to confirm, saves the fields into
    /// the overlay, then refreshes the supplied collection. Images without the marker are not from Civitai
    /// and are rejected.
    /// </summary>
    public async Task FetchForImageAsync(ImageViewModel image, ObservableCollection<UserMetadataItemViewModel> target)
    {
        if (image == null) return;

        if (!TryGetCivitaiImageId(image.Path, out var imageId))
        {
            await ServiceLocator.MessageService.Show(
                GetLocalizedText("UserMetadata.Fetch.NotCivitai"),
                GetLocalizedText("UserMetadata.Fetch.Title"),
                PopupButtons.OK);
            return;
        }

        var url = $"https://civitai.com/images/{imageId}";

        CivitaiImageGenerationData? data = null;
        string? fetchError = null;
        try
        {
            using var client = new CivitaiClient();
            data = await client.FetchImageGenerationDataAsync(imageId, CancellationToken.None);
            fetchError = client.LastError;
        }
        catch (Exception ex)
        {
            fetchError = ex.Message;
        }

        var pairs = data?.ToOrderedPairs();

        if (pairs == null || pairs.Count == 0)
        {
            // Record why nothing came back so a future endpoint/header change is debuggable rather
            // than surfacing only the generic "no data" message to the user.
            if (!string.IsNullOrEmpty(fetchError))
            {
                Logger.Log($"CivitAI fetch for {url} returned no data: {fetchError}");
            }

            await ServiceLocator.MessageService.Show(
                GetLocalizedText("UserMetadata.Fetch.NoData"),
                GetLocalizedText("UserMetadata.Fetch.Title"),
                PopupButtons.OK);
            return;
        }

        // Confirm before writing. The user can delete any unwanted rows afterwards via the per-row
        // delete button, so we import the whole set on confirmation.
        var preview = new StringBuilder();
        preview.AppendLine(GetLocalizedText("UserMetadata.Fetch.ConfirmMessage"));
        preview.AppendLine();
        foreach (var pair in pairs)
        {
            var value = pair.Value.Length > 120 ? pair.Value.Substring(0, 120) + "…" : pair.Value;
            preview.AppendLine($"• {pair.Key}: {value}");
        }

        var confirm = await ServiceLocator.MessageService.ShowMedium(
            preview.ToString(),
            GetLocalizedText("UserMetadata.Fetch.Title"),
            PopupButtons.YesNo);

        if (confirm != PopupResult.Yes)
        {
            return;
        }

        foreach (var pair in pairs)
        {
            ServiceLocator.UserMetadataService.Save(image, pair.Key, pair.Value, "civitai", url);
        }

        // Refresh the overlay view for this image.
        var refreshed = await ServiceLocator.UserMetadataService.LoadForImageAsync(image);

        ServiceLocator.Dispatcher.Invoke(() =>
        {
            target.Clear();
            foreach (var item in refreshed)
            {
                target.Add(item);
            }
            image.HasUserMetadata = target.Count > 0;
        });

        ServiceLocator.ToastService.Toast(
            GetLocalizedText("UserMetadata.Fetch.Success").Replace("{count}", $"{pairs.Count}"),
            GetLocalizedText("UserMetadata.Fetch.Title"));
    }

    /// <summary>
    /// Extracts the Civitai image id from a file path by reading the <c>CIV_ID__&lt;id&gt;</c> marker in
    /// the filename. Returns false when the path is empty or has no marker (i.e. not a Civitai image).
    /// </summary>
    private static bool TryGetCivitaiImageId(string? path, out long imageId)
    {
        imageId = 0;

        if (string.IsNullOrWhiteSpace(path)) return false;

        var fileName = Path.GetFileNameWithoutExtension(path);
        var match = FilenameIdRegex.Match(fileName);

        return match.Success && long.TryParse(match.Groups["id"].Value, out imageId);
    }
}
