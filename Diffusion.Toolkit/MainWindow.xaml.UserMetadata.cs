using System;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Services;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace Diffusion.Toolkit
{
    public partial class MainWindow
    {
        private async void ExportUserMetadata()
        {
            using var dialog = new CommonSaveFileDialog
            {
                DefaultFileName = "user-metadata.json",
                DefaultExtension = "json",
                AlwaysAppendDefaultExtension = true
            };
            dialog.Filters.Add(new CommonFileDialogFilter("JSON", "*.json"));

            if (dialog.ShowDialog(ServiceLocator.WindowService.CurrentWindow) != CommonFileDialogResult.Ok)
            {
                return;
            }

            try
            {
                await ServiceLocator.UserMetadataService.ExportAsync(dialog.FileName);

                ServiceLocator.ToastService.Toast(
                    GetLocalizedText("UserMetadata.Export.Success"),
                    GetLocalizedText("Menu.File.ExportUserMetadata"));
            }
            catch (Exception ex)
            {
                await ServiceLocator.MessageService.Show(
                    ex.Message,
                    GetLocalizedText("Menu.File.ExportUserMetadata"),
                    PopupButtons.OK);
            }
        }

        private async void ImportUserMetadata()
        {
            using var dialog = new CommonOpenFileDialog
            {
                EnsureFileExists = true,
                Multiselect = false
            };
            dialog.Filters.Add(new CommonFileDialogFilter("JSON", "*.json"));

            if (dialog.ShowDialog(ServiceLocator.WindowService.CurrentWindow) != CommonFileDialogResult.Ok)
            {
                return;
            }

            try
            {
                var count = await ServiceLocator.UserMetadataService.ImportAsync(dialog.FileName);

                ServiceLocator.ToastService.Toast(
                    GetLocalizedText("UserMetadata.Import.Success").Replace("{count}", $"{count}"),
                    GetLocalizedText("Menu.File.ImportUserMetadata"));
            }
            catch (Exception ex)
            {
                await ServiceLocator.MessageService.Show(
                    ex.Message,
                    GetLocalizedText("Menu.File.ImportUserMetadata"),
                    PopupButtons.OK);
            }
        }
    }
}
