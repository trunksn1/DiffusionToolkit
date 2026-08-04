using Diffusion.Toolkit.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Diffusion.Common;
using Diffusion.Database.Models;
using Diffusion.Toolkit.Configuration;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit.Controls
{
    /// <summary>
    /// Interaction logic for MetadataPanel.xaml
    /// </summary>
    public partial class MetadataPanel : UserControl
    {
        public static readonly DependencyProperty CurrentImageProperty = DependencyProperty.Register(
            nameof(CurrentImage),
            typeof(ImageViewModel),
            typeof(MetadataPanel),
            new PropertyMetadata(default(ImageEntry))
        );

        public ImageViewModel CurrentImage
        {
            get => (ImageViewModel)GetValue(CurrentImageProperty);
            set => SetValue(CurrentImageProperty, value);
        }

        public static readonly DependencyProperty MetadataSectionProperty = DependencyProperty.Register(
            nameof(MetadataSection),
            typeof(MetadataSection),
            typeof(MetadataPanel),
            new PropertyMetadata(default(ImageEntry))
        );

        public MetadataSection MetadataSection
        {
            get => (MetadataSection)GetValue(MetadataSectionProperty);
            set => SetValue(MetadataSectionProperty, value);
        }

        public MetadataPanel()
        {
            InitializeComponent();
            InitializeScheduleTab();
        }

        // --- Schedule tab -----------------------------------------------------

        private void InitializeScheduleTab()
        {
            for (int h = 0; h < 24; h++) ScheduleHour.Items.Add(h.ToString("D2"));
            foreach (var m in new[] { 0, 15, 30, 45 }) ScheduleMinute.Items.Add(m.ToString("D2"));

            ScheduleDate.DisplayDateStart = DateTime.Today;
            // CivitAI rejects publish dates beyond its scheduling window.
            ScheduleDate.DisplayDateEnd = CivitaiPostsService.LastSchedulableDate;
            ScheduleDate.SelectedDate = DateTime.Today.AddDays(1);
            ScheduleHour.SelectedIndex = 12;
            ScheduleMinute.SelectedIndex = 0;
        }

        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Only react to the panel's own TabControl, not bubbled ComboBox events.
            if (!ReferenceEquals(e.OriginalSource, sender)) return;
            if (ScheduleTab.IsSelected)
            {
                RefreshScheduleSelection();
            }
        }

        /// <summary>
        /// The images the Schedule tab acts on: the thumbnail multi-selection
        /// when there is one, otherwise the image currently shown in the
        /// preview. Ids are kept alongside paths for Posted-album tagging.
        /// </summary>
        private List<(string Path, int Id)> GetScheduleTargets()
        {
            IEnumerable<(string Path, int Id)> entries;
            var selected = ServiceLocator.MainModel?.SelectedImages;
            if (selected is { Count: > 0 })
            {
                entries = selected
                    .Where(entry => entry.EntryType == EntryType.File)
                    .Select(entry => (entry.Path, entry.Id));
            }
            else if (CurrentImage?.Path is { } current)
            {
                entries = new[] { (current, CurrentImage.Id) };
            }
            else
            {
                entries = Enumerable.Empty<(string, int)>();
            }

            return entries
                .Where(t => !string.IsNullOrWhiteSpace(t.Path) && File.Exists(t.Path))
                .GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private void RefreshScheduleSelection()
        {
            var files = GetScheduleTargets();
            ScheduleSelectionText.Text = files.Count switch
            {
                0 => "No image selected",
                1 => "1 image will be posted",
                _ => $"{files.Count} images will be posted together"
            };
            ScheduleFilesList.ItemsSource = files.Select(f => Path.GetFileName(f.Path)).ToList();
            ScheduleButton.IsEnabled = files.Count > 0;
        }

        private async void ScheduleButton_Click(object sender, RoutedEventArgs e)
        {
            var files = GetScheduleTargets();
            RefreshScheduleSelection();
            if (files.Count == 0)
            {
                ScheduleStatusText.Text = "Select at least one image first.";
                return;
            }

            if (ScheduleDate.SelectedDate == null)
            {
                ScheduleStatusText.Text = "Please choose a date.";
                return;
            }

            int hour = ScheduleHour.SelectedIndex >= 0 ? ScheduleHour.SelectedIndex : 12;
            int minute = ScheduleMinute.SelectedItem is string ms && int.TryParse(ms, out var m) ? m : 0;
            var publishAt = ScheduleDate.SelectedDate.Value.Date.AddHours(hour).AddMinutes(minute);
            if (publishAt <= DateTime.Now)
            {
                ScheduleStatusText.Text = "The scheduled time must be in the future.";
                return;
            }
            if (publishAt.Date > CivitaiPostsService.LastSchedulableDate)
            {
                ScheduleStatusText.Text =
                    $"CivitAI only accepts posts up to {CivitaiPostsService.MaxScheduleDaysAhead} days ahead " +
                    $"(through {CivitaiPostsService.LastSchedulableDate:d}).";
                return;
            }

            var title = string.IsNullOrWhiteSpace(ScheduleTitle.Text) ? null : ScheduleTitle.Text.Trim();

            ScheduleButton.IsEnabled = false;
            ScheduleStatusText.Text = files.Count == 1
                ? "Scheduling post…"
                : $"Scheduling post with {files.Count} images…";
            try
            {
                void OnProgress(string message) =>
                    Dispatcher.BeginInvoke(() => ScheduleStatusText.Text = message);

                var result = await ServiceLocator.CivitaiPostsService.SchedulePostAsync(
                    files.Select(f => f.Path).ToList(), publishAt, title, OnProgress);
                if (result.IsAuthFailure)
                {
                    ScheduleStatusText.Text = "Authentication failed.";
                    MessageBox.Show(Window.GetWindow(this), CivitaiPostsService.AuthFailureMessage,
                        "Authentication Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!result.Success)
                {
                    ScheduleStatusText.Text = $"Failed: {result.Message}";
                    return;
                }

                // Tag the scheduled images in the Posted album right away —
                // no need to wait for the calendar's matcher to find them.
                var tagged = ServiceLocator.CivitaiPostsService.MarkImagesAsPosted(
                    files.Select(f => f.Id).Where(id => id > 0));

                ScheduleStatusText.Text = $"Scheduled for {publishAt:g} — it's now in the CivitAI Calendar"
                    + (tagged > 0 ? $" and {tagged} image(s) were added to the '{CivitaiPostsService.PostedAlbumName}' album." : ".");
                ServiceLocator.ToastService?.Toast(
                    $"Scheduled post with {files.Count} image(s) for {publishAt:g}", "CivitAI");
            }
            catch (Exception ex)
            {
                Logger.Log($"MetadataPanel: schedule post failed: {ex.Message}");
                ScheduleStatusText.Text = $"Failed: {ex.Message}";
            }
            finally
            {
                ScheduleButton.IsEnabled = true;
            }
        }

        private void CollapseAll_Click(object sender, RoutedEventArgs e)
        {
            SetMetadataState(AccordionState.Collapsed);
        }

        private void ExpandAll_Click(object sender, RoutedEventArgs e)
        {
            SetMetadataState(AccordionState.Expanded);
        }

        private void SetMetadataState(AccordionState state)
        {
            PromptMetadata.State = state;
            NegativePromptMetadata.State = state;
            SeedMetadata.State = state;
            SamplerMetadata.State = state;
            OtherMetadata.State = state;
            ModelMetadata.State = state;
            PathMetadata.State = state;
            AlbumMetadata.State = state;
            DateMetadata.State = state;
        }

        private void AlbumName_OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            var album = ((Album)((TextBox)sender).DataContext);
            CurrentImage.OpenAlbumCommand?.Execute(album);
        }

        private void UIElement_OnGotFocus(object sender, RoutedEventArgs e)
        {
            Keyboard.ClearFocus();
        }

        private void AddTagButton_OnClick(object sender, RoutedEventArgs e)
        {
            var tagName = AddTagText.Text.Trim();
            if (tagName.Length > 0)
            {
                ServiceLocator.DataStore.CreateTag(tagName);
                AddTagText.Text = "";
                CurrentImage.ImageTags = ServiceLocator.TagService.GetImageTagViews(CurrentImage.Id);
            }
        }
    }
}
