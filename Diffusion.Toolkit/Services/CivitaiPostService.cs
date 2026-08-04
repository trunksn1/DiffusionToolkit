using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Windows;

namespace Diffusion.Toolkit.Services;

public class CivitaiPostService
{
    /// <summary>
    /// Posts the given images to CivitAI through the API, with no browser
    /// window: one post containing every selected file, published immediately.
    ///
    /// This is the same pipeline the calendar's scheduling uses (create draft,
    /// upload, attach, publish), so it authenticates with the OAuth token,
    /// reports progress in-app, returns structured errors, and adds the images
    /// to the "Posted" album. The WebView2 window in <see cref="PostImages"/>
    /// remains the default because CivitAI's own editor offers resource
    /// tagging and NSFW rating that this path does not.
    /// </summary>
    public async Task PostImagesInApp(IEnumerable<ImageEntry> images, string? title = null)
    {
        var imageList = images.Where(i => i.EntryType == EntryType.File && File.Exists(i.Path)).ToList();
        if (!imageList.Any())
        {
            ServiceLocator.ToastService.Toast("Invalid selection", "Please select at least one image file");
            return;
        }

        var toast = ServiceLocator.ToastService;
        toast.Toast(imageList.Count == 1
            ? "Uploading 1 image to CivitAI…"
            : $"Uploading {imageList.Count} images to CivitAI as one post…", "CivitAI");

        try
        {
            // No progress callback: it fires per uploaded image from a
            // background thread, and a toast per image would bury the result.
            var result = await ServiceLocator.CivitaiPostsService.PostNowAsync(
                imageList.Select(i => i.Path), title);

            if (result.IsAuthFailure)
            {
                toast.Toast(CivitaiPostsService.AuthFailureMessage, "CivitAI sign-in needed");
                return;
            }
            if (!result.Success)
            {
                // The message carries the orphaned-draft URL when a failed
                // upload could not be rolled back, so show it in full.
                toast.Toast(result.Message ?? "The post could not be created.", "CivitAI post failed");
                return;
            }

            var tagged = ServiceLocator.CivitaiPostsService.MarkImagesAsPosted(
                imageList.Where(i => i.Id > 0).Select(i => i.Id));

            toast.Toast(
                $"Posted {imageList.Count} image(s) to CivitAI" +
                (tagged > 0 ? $" and added them to the '{CivitaiPostsService.PostedAlbumName}' album" : "") + ".",
                "CivitAI");
        }
        catch (Exception ex)
        {
            Logger.Log($"CivitaiPostService: in-app post failed: {ex}");
            toast.Toast(ex.Message, "CivitAI post failed");
        }
    }

    /// <summary>
    /// Posts a single image to CivitAI using embedded WebView2 browser
    /// </summary>
    public void PostImage(ImageEntry image)
    {
        if (image == null || image.EntryType != EntryType.File)
        {
            ServiceLocator.ToastService.Toast("Invalid selection", "Please select an image file");
            return;
        }

        if (!File.Exists(image.Path))
        {
            ServiceLocator.ToastService.Toast("File not found", $"Image file does not exist: {image.Path}");
            return;
        }

        try
        {
            // Open WebView2 window with automatic upload
            var uploadWindow = new CivitaiUploadWindow(image.Path);
            uploadWindow.Show();

            ServiceLocator.ToastService.Toast(
                "Opening CivitAI Upload",
                "Browser window opened. Image will upload automatically.");
        }
        catch (Exception ex)
        {
            ServiceLocator.ToastService.Toast("Error posting to CivitAI", ex.Message);
        }
    }

    /// <summary>
    /// Posts multiple images to CivitAI
    /// </summary>
    public void PostImages(IEnumerable<ImageEntry> images)
    {
        var imageList = images.Where(i => i.EntryType == EntryType.File).ToList();

        if (!imageList.Any())
        {
            ServiceLocator.ToastService.Toast("Invalid selection", "Please select at least one image file");
            return;
        }

        try
        {
            if (imageList.Count == 1)
            {
                PostImage(imageList[0]);
            }
            else
            {
                // For multiple images, upload first one
                var uploadWindow = new CivitaiUploadWindow(imageList[0].Path);
                uploadWindow.Show();

                ServiceLocator.ToastService.Toast(
                    $"Posting {imageList.Count} images to CivitAI",
                    $"Uploading first image. Repeat for remaining {imageList.Count - 1} images.");
            }
        }
        catch (Exception ex)
        {
            ServiceLocator.ToastService.Toast("Error posting to CivitAI", ex.Message);
        }
    }
}
