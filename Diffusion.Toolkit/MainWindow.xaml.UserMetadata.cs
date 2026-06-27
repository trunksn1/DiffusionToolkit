using System;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Civitai;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Services;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace Diffusion.Toolkit
{
    public partial class MainWindow
    {
        /// <summary>
        /// Tools &gt; Backfill CivitAI Metadata. Finds DB images downloaded from CivitAI (filename
        /// has the CIV_ID__ marker) whose prompt is empty, and fetches their generation data /
        /// model list from CivitAI into the user-metadata overlay. Idempotent and cancellable.
        /// </summary>
        private async Task BackfillCivitaiMetadataTask(object o)
        {
            var confirm = await _messagePopupManager.ShowMedium(
                "Scan the database for images downloaded from CivitAI (filename contains \"CIV_ID__\") whose prompt is empty, and fetch their generation data and model list from CivitAI into the User Metadata overlay.\n\nThis makes one network request per image and may take a while. Images already fetched are skipped.\n\nContinue?",
                "Backfill CivitAI Metadata",
                PopupButtons.YesNo);

            if (confirm != PopupResult.Yes)
                return;

            if (!await ServiceLocator.ProgressService.TryStartTask())
                return;

            var token = ServiceLocator.ProgressService.CancellationToken;

            int saved = 0, alreadyHad = 0, noData = 0, failed = 0;

            try
            {
                await Task.Run(async () =>
                {
                    var candidates = _dataStore.GetCivitaiBackfillCandidates();

                    ServiceLocator.ProgressService.InitializeProgress(candidates.Count);
                    ServiceLocator.ProgressService.SetStatus($"Found {candidates.Count:#,###} CivitAI image(s) with an empty prompt…");

                    // One shared client for the whole batch so we don't churn HTTP connections.
                    using var client = new CivitaiClient();

                    var processed = 0;
                    foreach (var image in candidates)
                    {
                        if (token.IsCancellationRequested)
                            break;

                        var outcome = await ServiceLocator.CivitaiImageService.BackfillImageAsync(client, image.Path, token);

                        switch (outcome)
                        {
                            case CivitaiBackfillOutcome.Saved: saved++; break;
                            case CivitaiBackfillOutcome.AlreadyHadData: alreadyHad++; break;
                            case CivitaiBackfillOutcome.NoData: noData++; break;
                            default: failed++; break; // NotCivitai / FileMissing / Failed
                        }

                        processed++;
                        ServiceLocator.ProgressService.SetProgress(processed, "Fetching {current} of {total}…");

                        // Be gentle with the unofficial endpoint — only throttle when we actually called it.
                        if (outcome == CivitaiBackfillOutcome.Saved || outcome == CivitaiBackfillOutcome.NoData)
                        {
                            try { await Task.Delay(300, token); }
                            catch (TaskCanceledException) { break; }
                        }
                    }
                });
            }
            finally
            {
                ServiceLocator.ProgressService.ClearStatus();
                ServiceLocator.ProgressService.ClearProgress();
                ServiceLocator.ProgressService.CompleteTask();
            }

            ServiceLocator.ToastService.Toast(
                $"Fetched {saved}, already had data {alreadyHad}, no data {noData}, skipped/failed {failed}.",
                "Backfill CivitAI Metadata");
        }

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
