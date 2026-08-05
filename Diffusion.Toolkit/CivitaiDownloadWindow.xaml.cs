using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit
{
    /// <summary>
    /// Asks what to fetch from CivitAI and over what period, replacing the three
    /// separate header buttons the calendar used to carry. One dialog rather
    /// than a fourth button: the choices are not independent — engagement
    /// counters are fetched over the same period as the posts they belong to —
    /// and the expensive one needs its cost spelled out before it is picked.
    /// </summary>
    public partial class CivitaiDownloadWindow : BorderlessWindow
    {
        /// <summary>Fetch the post list (dates, titles, queue state).</summary>
        public bool FetchPosts { get; private set; }

        /// <summary>Also fetch per-image engagement counters (--image-stats).</summary>
        public bool FetchEngagement { get; private set; }

        /// <summary>Download image files that have no local match.</summary>
        public bool DownloadMissingFiles { get; private set; }

        /// <summary>Discard the cached history instead of merging into it.</summary>
        public bool Rebuild { get; private set; }

        /// <summary>Oldest post date to fetch, in local time.</summary>
        public DateTime FromDate { get; private set; }

        private record PeriodOption(string Label, Func<DateTime>? From)
        {
            public override string ToString() => Label;
        }

        // "Everything" and "Custom" both resolve through From == null: the first
        // clamps to CivitAI's founding, the second reads the date picker.
        private static readonly PeriodOption[] Periods =
        {
            new("Last 7 days", () => DateTime.Today.AddDays(-7)),
            new("Last 30 days", () => DateTime.Today.AddDays(-30)),
            new("Last 6 months", () => DateTime.Today.AddMonths(-6)),
            new("Last year", () => DateTime.Today.AddYears(-1)),
            new("Everything (since November 2022)", () => CivitaiPostsService.CivitaiFounding),
            new("From a date I choose…", null),
        };

        private const int CustomIndex = 5;

        /// <summary>
        /// <paramref name="missingCount"/> is how many posted images currently
        /// have no local file, so the dialog can say whether that option has
        /// anything to do rather than offering an action that does nothing.
        /// </summary>
        public CivitaiDownloadWindow(int missingCount = 0)
        {
            InitializeComponent();

            foreach (var period in Periods) PeriodCombo.Items.Add(period);
            PeriodCombo.SelectedIndex = 1;   // Last 30 days

            var founding = CivitaiPostsService.CivitaiFounding;
            CustomDate.DisplayDateStart = founding;
            CustomDate.DisplayDateEnd = DateTime.Today;
            CustomDate.SelectedDate = DateTime.Today.AddYears(-1) < founding
                ? founding
                : DateTime.Today.AddYears(-1);

            _missingCount = missingCount;
            if (missingCount == 0)
            {
                MissingFilesCheck.IsEnabled = false;
                MissingFilesCheck.ToolTip = "Every posted image already has a local file.";
            }

            UpdateSummary();
        }

        private readonly int _missingCount;

        private DateTime ResolveFromDate()
        {
            var period = PeriodCombo.SelectedItem as PeriodOption ?? Periods[1];
            var from = period.From?.Invoke()
                       ?? CustomDate.SelectedDate?.Date
                       ?? CivitaiPostsService.CivitaiFounding;
            return from < CivitaiPostsService.CivitaiFounding
                ? CivitaiPostsService.CivitaiFounding
                : from;
        }

        private void Period_Changed(object sender, SelectionChangedEventArgs e)
        {
            // The picker only exists for the custom option; leaving it visible
            // and inert next to "Last 30 days" would read as a contradiction.
            CustomDate.Visibility = PeriodCombo.SelectedIndex == CustomIndex
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateSummary();
        }

        private void Option_Changed(object sender, RoutedEventArgs e) => UpdateSummary();

        private void UpdateSummary()
        {
            // Called from Checked handlers that fire during InitializeComponent,
            // before the fields they read exist.
            if (SummaryText == null || PeriodCombo == null) return;

            bool posts = PostsCheck.IsChecked == true;
            bool engagement = EngagementCheck.IsChecked == true;
            bool files = MissingFilesCheck.IsChecked == true && MissingFilesCheck.IsEnabled;

            DownloadButton.IsEnabled = posts || engagement || files;

            if (!DownloadButton.IsEnabled)
            {
                SummaryText.Text = "Nothing selected.";
                return;
            }

            var lines = new List<string>();
            if (posts || engagement)
            {
                var from = ResolveFromDate();
                var span = engagement
                    ? "posts and their per-image engagement counters"
                    : "posts";
                lines.Add($"Fetching {span} published since {from:d}"
                          + (Rebuilding ? ", rebuilt from scratch" : "")
                          + ", plus the whole future queue.");
                if (engagement)
                {
                    var months = (DateTime.Today.Year - from.Year) * 12 + DateTime.Today.Month - from.Month;
                    lines.Add(months >= 6
                        ? $"That is roughly {months} months of posts, and engagement counters cost one "
                          + "request each — expect this to take a while."
                        : "One extra request per post for the counters.");
                }
            }
            if (files)
            {
                lines.Add($"Downloading {_missingCount} image file(s) with no local copy into the "
                          + $"'{CivitaiPostsService.PostedFolderName}' folder. Rescan your folders "
                          + "afterwards to import them.");
            }

            SummaryText.Text = string.Join("\n", lines);
        }

        private bool Rebuilding => RebuildCheck?.IsChecked == true;

        private void Download_Click(object sender, RoutedEventArgs e)
        {
            FetchPosts = PostsCheck.IsChecked == true;
            FetchEngagement = EngagementCheck.IsChecked == true;
            DownloadMissingFiles = MissingFilesCheck.IsChecked == true && MissingFilesCheck.IsEnabled;
            Rebuild = Rebuilding;
            FromDate = ResolveFromDate();

            if (!FetchPosts && !FetchEngagement && !DownloadMissingFiles)
            {
                MessageBox.Show(this, "Tick at least one thing to download.", "Download",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
