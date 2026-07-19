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
        /// The files the Schedule tab acts on: the thumbnail multi-selection when
        /// there is one, otherwise the image currently shown in the preview.
        /// </summary>
        private List<string> GetScheduleTargets()
        {
            var selected = ServiceLocator.MainModel?.SelectedImages;
            var paths = selected is { Count: > 0 }
                ? selected.Where(entry => entry.EntryType == EntryType.File).Select(entry => entry.Path)
                : CurrentImage?.Path is { } current ? new[] { current } : Enumerable.Empty<string>();

            return paths
                .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
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
            ScheduleFilesList.ItemsSource = files.Select(Path.GetFileName).ToList();
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

            var title = string.IsNullOrWhiteSpace(ScheduleTitle.Text) ? null : ScheduleTitle.Text.Trim();

            ScheduleButton.IsEnabled = false;
            ScheduleStatusText.Text = files.Count == 1
                ? "Scheduling post…"
                : $"Scheduling post with {files.Count} images…";
            try
            {
                void OnProgress(string message) =>
                    Dispatcher.BeginInvoke(() => ScheduleStatusText.Text = message);

                var result = await ServiceLocator.CivitaiPostsService.SchedulePostAsync(files, publishAt, title, OnProgress);
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

                ScheduleStatusText.Text = $"Scheduled for {publishAt:g}. It will appear in the CivitAI Calendar after the next Fetch Upcoming.";
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
