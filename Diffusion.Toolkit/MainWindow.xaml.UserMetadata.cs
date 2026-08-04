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
                    // The batch fetches generation data over tRPC, which prefers the
                    // OAuth token; the key remains for REST and as the fallback.
                    var accessToken = ServiceLocator.CivitaiOAuthService == null
                        ? null
                        : await ServiceLocator.CivitaiOAuthService.TryGetAccessTokenAsync();
                    using var client = new CivitaiClient(ServiceLocator.Settings?.GetCivitaiApiKey(), accessToken);

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

        /// <summary>
        /// Tools &gt; Index User Metadata for Search. Links existing CivitAI-sourced overlay entries to
        /// their images (by writing the file hash onto the Image row, matched via the CIV_ID__ marker)
        /// so they become reachable from the <c>usermeta:</c> search token. Pure database work — fast,
        /// no file hashing, and safe to run repeatedly.
        /// </summary>
        private async Task IndexUserMetadataTask(object o)
        {
            var confirm = await _messagePopupManager.ShowMedium(
                "Make your existing User Metadata searchable.\n\nThis links overlay entries (fetched from CivitAI) to their images so you can find them with the \"usermeta:\" search term. It only updates the database (no files are read) and is safe to run again at any time.\n\nContinue?",
                "Index User Metadata for Search",
                PopupButtons.YesNo);

            if (confirm != PopupResult.Yes)
                return;

            if (!await ServiceLocator.ProgressService.TryStartTask())
                return;

            int linked = 0;

            try
            {
                ServiceLocator.ProgressService.SetStatus("Indexing User Metadata for search…");

                await Task.Run(() =>
                {
                    linked = _dataStore.IndexUserMetadataForSearch();
                });
            }
            finally
            {
                ServiceLocator.ProgressService.ClearStatus();
                ServiceLocator.ProgressService.ClearProgress();
                ServiceLocator.ProgressService.CompleteTask();
            }

            ServiceLocator.ToastService.Toast(
                linked > 0
                    ? $"Linked {linked:#,###} image(s). You can now search with usermeta:…"
                    : "Everything was already indexed. Search with usermeta:…",
                "Index User Metadata for Search");
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
