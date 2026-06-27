using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Services;
using Diffusion.Toolkit.Windows;

namespace Diffusion.Toolkit.Pages
{
    public partial class Search
    {
        /// <summary>
        /// Loads the user-metadata overlay for the given image (off the UI thread for hashing) and
        /// populates its observable collection on the dispatcher.
        /// </summary>
        private async void LoadUserMetadata(ImageViewModel imageViewModel)
        {
            if (imageViewModel == null) return;

            try
            {
                var items = await ServiceLocator.UserMetadataService.LoadForImageAsync(imageViewModel);

                // The selected image may have changed while we were loading; only apply if still current.
                if (!ReferenceEquals(_model.CurrentImage, imageViewModel)) return;

                Dispatcher.Invoke(() =>
                {
                    imageViewModel.UserMetadata.Clear();
                    foreach (var item in items)
                    {
                        imageViewModel.UserMetadata.Add(item);
                    }
                    imageViewModel.HasUserMetadata = imageViewModel.UserMetadata.Count > 0;
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to load user metadata: {ex.Message}");
            }
        }

        private void AddUserMetadata(ImageViewModel imageViewModel)
        {
            if (imageViewModel == null) return;

            var window = new UserMetadataEditWindow
            {
                Owner = ServiceLocator.WindowService.CurrentWindow
            };

            if (window.ShowDialog() == true)
            {
                if (ServiceLocator.UserMetadataService.Save(imageViewModel, window.ResultKey, window.ResultValue))
                {
                    LoadUserMetadata(imageViewModel);
                }
                else
                {
                    ServiceLocator.ToastService.Toast(GetLocalizedText("UserMetadata.NoHash"), "");
                }
            }
        }

        private void EditUserMetadata(ImageViewModel imageViewModel, UserMetadataItemViewModel item)
        {
            if (imageViewModel == null || item == null) return;

            var window = new UserMetadataEditWindow(item.Key, item.Value)
            {
                Owner = ServiceLocator.WindowService.CurrentWindow
            };

            if (window.ShowDialog() == true)
            {
                // If the key was renamed, remove the old entry so we don't leave a stale row behind.
                if (!string.Equals(item.Key, window.ResultKey, StringComparison.Ordinal))
                {
                    ServiceLocator.UserMetadataService.Delete(imageViewModel, item.Key);
                }

                if (ServiceLocator.UserMetadataService.Save(imageViewModel, window.ResultKey, window.ResultValue, item.Source, item.SourceUrl))
                {
                    LoadUserMetadata(imageViewModel);
                }
            }
        }

        private void DeleteUserMetadata(ImageViewModel imageViewModel, UserMetadataItemViewModel item)
        {
            if (imageViewModel == null || item == null) return;

            ServiceLocator.UserMetadataService.Delete(imageViewModel, item.Key);

            imageViewModel.UserMetadata.Remove(item);
            imageViewModel.HasUserMetadata = imageViewModel.UserMetadata.Count > 0;
        }

        private Task FetchUserMetadataFromCivitai(ImageViewModel imageViewModel)
        {
            if (imageViewModel == null) return Task.CompletedTask;

            return ServiceLocator.CivitaiImageService.FetchForImageAsync(imageViewModel, imageViewModel.UserMetadata);
        }

        private void OpenUserMetadataSourceUrl(UserMetadataItemViewModel item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.SourceUrl)) return;

            try
            {
                Process.Start("explorer.exe", $"\"{item.SourceUrl}\"");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to open URL: {ex.Message}");
            }
        }
    }
}
