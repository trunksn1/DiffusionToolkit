using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
    // Matches the numeric id in https://civitai.com/images/12345678(?...)
    private static readonly Regex ImageIdRegex =
        new(@"civitai\.com/images/(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private string GetLocalizedText(string key)
    {
        return (string)JsonLocalizationProvider.Instance.GetLocalizedObject(key, null, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Prompts for the image's Civitai URL, fetches its generation data, asks the user to confirm,
    /// saves the fields into the overlay, then refreshes the supplied collection.
    /// </summary>
    public async Task FetchForImageAsync(ImageViewModel image, ObservableCollection<UserMetadataItemViewModel> target)
    {
        if (image == null) return;

        var (inputResult, url) = await ServiceLocator.MessageService.ShowInput(
            GetLocalizedText("UserMetadata.Fetch.Prompt"),
            GetLocalizedText("UserMetadata.Fetch.Title"),
            "https://civitai.com/images/");

        if (inputResult != PopupResult.OK || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var match = ImageIdRegex.Match(url);
        if (!match.Success)
        {
            await ServiceLocator.MessageService.Show(
                GetLocalizedText("UserMetadata.Fetch.InvalidUrl"),
                GetLocalizedText("UserMetadata.Fetch.Title"),
                PopupButtons.OK);
            return;
        }

        if (!long.TryParse(match.Groups["id"].Value, out var imageId))
        {
            return;
        }

        CivitaiImageGenerationData? data;
        try
        {
            using var client = new CivitaiClient();
            data = await client.FetchImageGenerationDataAsync(imageId, CancellationToken.None);
        }
        catch
        {
            data = null;
        }

        var pairs = data?.ToOrderedPairs();

        if (pairs == null || pairs.Count == 0)
        {
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
}
