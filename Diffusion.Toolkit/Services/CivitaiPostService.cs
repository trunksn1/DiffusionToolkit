using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Windows;

namespace Diffusion.Toolkit.Services;

public class CivitaiPostService
{
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
