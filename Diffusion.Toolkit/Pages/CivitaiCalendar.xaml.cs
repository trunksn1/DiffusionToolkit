using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Diffusion.Common;
using Diffusion.Toolkit.Classes;
using Diffusion.Toolkit.Common;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Services;
using Diffusion.Toolkit.Thumbnails;

namespace Diffusion.Toolkit.Pages
{
    /// <summary>
    /// Interaction logic for CivitaiCalendar.xaml — a month calendar of the
    /// user's CivitAI posts (published + queued), with a day-detail view and
    /// an in-app image preview. Local files can be dragged onto future days
    /// to schedule new posts.
    /// </summary>
    public partial class CivitaiCalendar : NavigationPage
    {
        private readonly CivitaiCalendarModel _model = new();
        private CivitaiPostsService _service => ServiceLocator.CivitaiPostsService;
        private List<ResolvedPostImage> _resolved = new();
        private bool _isLoaded;
        private System.Threading.CancellationTokenSource? _fetchCts;
        private GridLength _rightColumnWidth = new(400);
        private Point _previewDragStart;

        public CivitaiCalendar() : base("calendar")
        {
            InitializeComponent();

            _model.PrevMonthCommand = new RelayCommand<object>(_ => ShiftMonth(-1));
            _model.NextMonthCommand = new RelayCommand<object>(_ => ShiftMonth(1));
            _model.TodayCommand = new RelayCommand<object>(_ => GoToMonth(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)));
            _model.FetchUpcomingCommand = new RelayCommand<object>(async _ => await FetchUpcomingAsync());
            _model.RecoverHistoryCommand = new RelayCommand<object>(_ => RecoverHistory());
            _model.CancelFetchCommand = new RelayCommand<object>(_ => _fetchCts?.Cancel());
            _model.DownloadMissingCommand = new RelayCommand<object>(async _ => await DownloadMissingAsync());

            _model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(CivitaiCalendarModel.ShowDayPanel)
                    or nameof(CivitaiCalendarModel.ShowPreviewPanel))
                {
                    UpdatePanelLayout();
                }
            };

            DataContext = _model;
            UpdatePanelLayout();

            ServiceLocator.NavigatorService.OnNavigate += (sender, args) =>
            {
                if (this != args.TargetPage) return;
                if (!_isLoaded)
                {
                    _isLoaded = true;
                    _ = LoadCacheAndBuildAsync();
                }
                else if (!_model.IsRefreshing && CacheFileTimeUtc() != _cacheStampUtc)
                {
                    // The cache changed outside this page (e.g. a post was
                    // scheduled from the metadata panel's Schedule tab).
                    _ = LoadCacheAndBuildAsync();
                }
            };
        }

        private DateTime _cacheStampUtc;

        private static DateTime CacheFileTimeUtc()
        {
            var path = CivitaiPostsService.CachePath;
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }

        // --- Panel layout (show/hide/resize) --------------------------------

        private void UpdatePanelLayout()
        {
            bool day = _model.ShowDayPanel;
            bool preview = _model.ShowPreviewPanel;

            if (!day && !preview)
            {
                if (RightColumn.Width.Value > 0) _rightColumnWidth = RightColumn.Width;
                RightColumn.Width = new GridLength(0);
                RightSplitter.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (RightColumn.Width.Value == 0) RightColumn.Width = _rightColumnWidth;
                RightSplitter.Visibility = Visibility.Visible;
            }

            DayViewRow.Height = day ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            PreviewRow.Height = preview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            PreviewSplitter.Visibility = day && preview ? Visibility.Visible : Visibility.Collapsed;
        }

        // --- Cache load + month build ---------------------------------------

        private async Task LoadCacheAndBuildAsync()
        {
            // Stamp before reading: a write racing the read just means one
            // harmless extra reload on the next navigation.
            _cacheStampUtc = CacheFileTimeUtc();

            // One cache read, matching, and Posted-album tagging off the UI
            // thread; the await continuation resumes on the UI thread.
            var (cache, resolved) = await Task.Run(() =>
            {
                var c = _service.LoadCache();
                if (c == null) return ((CivitaiPostsCache?)null, (List<ResolvedPostImage>?)null);
                var r = _service.ResolveMatches(c);
                var tagged = _service.MarkMatchedAsPosted(r);
                if (tagged > 0)
                {
                    Logger.Log($"CivitaiCalendar: {tagged} matched images ensured in '{CivitaiPostsService.PostedAlbumName}' album");
                }
                return (c, r);
            });

            if (cache == null || resolved == null)
            {
                // No data yet: still render the (empty) month so the page looks
                // like a calendar instead of a blank area with floating text.
                _resolved = new List<ResolvedPostImage>();
                _model.HasCache = false;
                _model.LastUpdated = null;
            }
            else
            {
                _resolved = resolved;
                _model.HasCache = true;
                _model.LastUpdated = cache.GeneratedAt != null
                    ? $"Last updated: {FormatUtc(cache.GeneratedAt)}"
                    : null;
            }
            BuildMonth();
        }

        private static string FormatUtc(string iso)
        {
            return DateTimeOffset.TryParse(iso, out var dto)
                ? dto.LocalDateTime.ToString("g")
                : iso;
        }

        private void ShiftMonth(int delta)
        {
            GoToMonth(_model.CurrentMonth.AddMonths(delta));
        }

        private void GoToMonth(DateTime month)
        {
            if (month < _model.MinMonth) month = _model.MinMonth;
            if (month > _model.MaxMonth) month = _model.MaxMonth;
            _model.CurrentMonth = month;
            BuildMonth();
        }

        private void BuildMonth()
        {
            var selectedDate = _model.SelectedDay?.Date;
            _model.Days.Clear();

            var first = _model.CurrentMonth;
            // Monday-first grid: how many leading days from the previous month.
            int offset = ((int)first.DayOfWeek + 6) % 7;
            var start = first.AddDays(-offset);
            var today = DateTime.Today;

            // Bucket resolved images by local post date.
            var byDay = _resolved
                .GroupBy(r => r.PublishedAtUtc == DateTime.MinValue
                    ? DateTime.MinValue
                    : DateTime.SpecifyKind(r.PublishedAtUtc, DateTimeKind.Utc).ToLocalTime().Date)
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.PublishedAtUtc).ToList());

            CalendarDayModel? reselect = null;
            for (int i = 0; i < 42; i++)
            {
                var date = start.AddDays(i);
                byDay.TryGetValue(date.Date, out var images);
                images ??= new List<ResolvedPostImage>();

                var day = new CalendarDayModel
                {
                    Date = date,
                    IsCurrentMonth = date.Month == first.Month,
                    IsToday = date.Date == today,
                    IsFuture = date.Date > today,
                    Images = images,
                    // Queue markers only make sense while the post is still
                    // waiting to go live; past posts are public already.
                    HasScheduled = images.Any(im => im.IsQueued),
                    CellImages = images.Take(MaxCellThumbnails).ToList(),
                };
                _model.Days.Add(day);
                if (selectedDate != null && date.Date == selectedDate.Value.Date)
                {
                    reselect = day;
                }

                LoadThumbnails(day.CellImages);
            }

            SelectDay(reselect);
        }

        private void SelectDay(CalendarDayModel? day)
        {
            if (_model.SelectedDay != null) _model.SelectedDay.IsSelected = false;
            _model.SelectedDay = day;
            if (day != null)
            {
                day.IsSelected = true;
                LoadThumbnails(day.Images);
            }

            // The preview belongs to the selected day. Moving to a day that
            // doesn't contain it - an empty day, or any other day before its
            // own image is picked - must empty the panel, or the old image
            // reads as if it were posted on the newly selected day.
            if (_model.SelectedImage != null
                && (day == null || !day.Images.Contains(_model.SelectedImage)))
            {
                ClearImageSelection();
            }
        }

        /// <summary>Cap on mini-thumbnails inside a month cell; the rest shows as a "+N" badge.</summary>
        private const int MaxCellThumbnails = 8;

        /// <summary>
        /// Thumbnails, loaded once per image and shared between the month
        /// cells and the day-view rows: matched images from the local file,
        /// unmatched ones straight from the CivitAI CDN (WPF downloads the
        /// BitmapImage URI itself). Images that fail both ways keep a null
        /// Thumbnail, which the XAML renders as a "?" placeholder.
        /// </summary>
        private void LoadThumbnails(IEnumerable<ResolvedPostImage> images)
        {
            foreach (var image in images.Where(im => im.Thumbnail == null && !im.ThumbnailPending))
            {
                var target = image;
                if (target.LocalPath != null && File.Exists(target.LocalPath))
                {
                    LoadLocalThumbnail(target);
                }
                else if (!string.IsNullOrWhiteSpace(target.Url))
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new System.Uri(CivitaiPostsService.CdnThumbnailUrl(target.Url!, target.Name));
                        bmp.CacheOption = BitmapCacheOption.OnDemand;
                        bmp.EndInit();
                        // On download failure fall back to the "?" placeholder
                        // instead of leaving a silently blank tile.
                        bmp.DownloadFailed += (_, args) =>
                        {
                            Logger.Log($"CivitaiCalendar: CDN thumbnail download failed for {target.CivitaiImageId}: {args.ErrorException?.Message}");
                            target.Thumbnail = null;
                        };
                        target.Thumbnail = bmp;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"CivitaiCalendar: CDN thumbnail failed for {target.CivitaiImageId}: {ex.Message}");
                    }
                }
            }
        }

        // Limits concurrent local decodes; mirrors ThumbnailService's own parallelism.
        private static readonly System.Threading.SemaphoreSlim ThumbnailWorkers = new(2);

        /// <summary>
        /// Loads a local file thumbnail directly instead of through
        /// ThumbnailService.QueueAsync: the service's queue silently drops any
        /// job whose BatchId doesn't match the search page's current batch, so
        /// calendar jobs queued from here would never complete. Uses the same
        /// per-folder dt_thumbnails.db cache as the main views.
        /// </summary>
        private void LoadLocalThumbnail(ResolvedPostImage target)
        {
            target.ThumbnailPending = true;
            var path = target.LocalPath!;
            _ = Task.Run(async () =>
            {
                await ThumbnailWorkers.WaitAsync();
                try
                {
                    var service = ServiceLocator.ThumbnailService;
                    var size = service.Size;
                    BitmapSource? thumbnail = null;
                    if (!(service.EnableCache && ThumbnailCache.Instance.TryGetThumbnail(path, size, out thumbnail)))
                    {
                        thumbnail = service.GetThumbnailImmediate(path, target.Width ?? 0, target.Height ?? 0, size);
                        if (service.EnableCache && thumbnail is BitmapImage bitmapImage)
                        {
                            ThumbnailCache.Instance.AddThumbnail(path, size, bitmapImage);
                        }
                    }
                    if (thumbnail != null)
                    {
                        await Dispatcher.BeginInvoke(() => target.Thumbnail = thumbnail);
                    }
                }
                catch (Exception ex)
                {
                    target.ThumbnailPending = false;
                    Logger.Log($"CivitaiCalendar: local thumbnail failed for {path}: {ex.Message}");
                }
                finally
                {
                    ThumbnailWorkers.Release();
                }
            });
        }

        // --- Fetching --------------------------------------------------------

        private Task FetchUpcomingAsync()
        {
            return RunFetchAsync(
                (onProgress, ct) => _service.FetchUpcomingAsync(onProgress, ct),
                "Checking for upcoming posts…");
        }

        private void RecoverHistory()
        {
            var dialog = new HistoryRangeWindow { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            var from = dialog.FromDate;
            _ = RunFetchAsync(
                (onProgress, ct) => _service.RecoverHistoryAsync(from, onProgress, ct),
                $"Recovering history from {from:MMMM yyyy}…");
        }

        /// <summary>
        /// Shared fetch runner: disables the buttons, streams the Python script's
        /// live progress into the status line, then reloads and rebuilds the month.
        /// Cancel kills the Python process; the run then unwinds silently.
        /// </summary>
        private async Task RunFetchAsync(
            Func<Action<string>, System.Threading.CancellationToken, Task<PythonResult>> run,
            string initialStatus)
        {
            if (_model.IsRefreshing) return;
            using var cts = new System.Threading.CancellationTokenSource();
            _fetchCts = cts;
            _model.IsRefreshing = true;
            _model.StatusText = initialStatus;
            try
            {
                // BeginInvoke: progress arrives from the process-reader thread;
                // never let a busy UI thread stall the pipe read.
                void OnProgress(string message) =>
                    Dispatcher.BeginInvoke(() => _model.StatusText = message);

                var result = await run(OnProgress, cts.Token);
                if (result.IsCanceled)
                {
                    return;
                }
                if (result.IsAuthFailure)
                {
                    MessageBox.Show(Window.GetWindow(this), CivitaiPostsService.AuthFailureMessage,
                        "Authentication Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!result.Success)
                {
                    MessageBox.Show(Window.GetWindow(this),
                        $"Failed to fetch posts:\n{result.Message}",
                        "Fetch Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _model.StatusText = "Matching to your library…";
                await LoadCacheAndBuildAsync();
            }
            finally
            {
                _fetchCts = null;
                _model.IsRefreshing = false;
                _model.StatusText = null;
            }
        }

        /// <summary>
        /// Downloads posted images that have no local match into
        /// "&lt;first root folder&gt;\Posted", then prompts for a folder rescan
        /// so they enter the library (the next refresh then matches + tags them).
        /// </summary>
        private async Task DownloadMissingAsync()
        {
            if (_model.IsRefreshing) return;
            var missing = _resolved.Count(r => r.IsUnmatched && !string.IsNullOrWhiteSpace(r.Url));
            if (missing == 0)
            {
                ServiceLocator.ToastService?.Toast("No missing images to download — everything is matched locally.", "CivitAI");
                return;
            }
            if (MessageBox.Show(Window.GetWindow(this),
                    $"Download {missing} posted image(s) that are not in your library into the 'Posted' subfolder of your first root folder?",
                    "Download Missing", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            using var cts = new System.Threading.CancellationTokenSource();
            _fetchCts = cts;
            _model.IsRefreshing = true;
            _model.StatusText = "Downloading missing images…";
            try
            {
                void OnProgress(string message) =>
                    Dispatcher.BeginInvoke(() => _model.StatusText = message);

                var (downloaded, skipped, failed, folder) =
                    await _service.DownloadMissingAsync(_resolved, OnProgress, cts.Token);

                if (folder == null)
                {
                    MessageBox.Show(Window.GetWindow(this),
                        "No root folder is configured — add one in Settings first.",
                        "Download Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                ServiceLocator.ToastService?.Toast(
                    $"Downloaded {downloaded} image(s) to {folder}" +
                    (skipped > 0 ? $", {skipped} already present" : "") +
                    (failed > 0 ? $", {failed} failed" : "") +
                    ". Rescan your folders to import them.", "CivitAI");
            }
            catch (OperationCanceledException)
            {
                // user pressed Cancel
            }
            finally
            {
                _fetchCts = null;
                _model.IsRefreshing = false;
                _model.StatusText = null;
            }
        }

        // --- Day + image selection ------------------------------------------

        private void DayCell_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is CalendarDayModel day)
            {
                // SelectDay drops any preview that doesn't belong to this day,
                // so an empty day leaves the preview panel empty.
                SelectDay(day);
                var firstImage = day.Images.FirstOrDefault();
                if (firstImage != null)
                {
                    _ = SelectImageAsync(firstImage);
                }
            }
        }

        private void DayImage_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ResolvedPostImage image)
            {
                _ = SelectImageAsync(image);
            }
        }

        /// <summary>
        /// Empties the preview panel and drops the current image selection.
        /// </summary>
        private void ClearImageSelection()
        {
            if (_model.SelectedImage != null) _model.SelectedImage.IsSelected = false;
            _model.SelectedImage = null;
            _model.PreviewImage = null;
        }

        /// <summary>
        /// Selects an image and loads its local file (or, for ambiguous matches,
        /// the best candidate) into the preview panel.
        /// </summary>
        private async Task SelectImageAsync(ResolvedPostImage image)
        {
            if (_model.SelectedImage != null) _model.SelectedImage.IsSelected = false;
            image.IsSelected = true;
            _model.SelectedImage = image;

            var path = image.LocalPath ?? image.Candidates.FirstOrDefault()?.Path;
            if (path == null || !File.Exists(path))
            {
                _model.PreviewImage = null;
                return;
            }

            var bitmap = await Task.Run(() =>
            {
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    var stream = new MemoryStream(bytes);
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    b.StreamSource = stream;
                    b.EndInit();
                    b.Freeze();
                    return (ImageSource?)b;
                }
                catch (Exception ex)
                {
                    Logger.Log($"CivitaiCalendar: preview load failed for {path}: {ex.Message}");
                    return null;
                }
            });

            // Only apply if the selection hasn't moved on meanwhile.
            if (ReferenceEquals(_model.SelectedImage, image))
            {
                _model.PreviewImage = bitmap;
            }
        }

        // --- Open actions ----------------------------------------------------

        private void OpenOnCivitai_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ResolvedPostImage image)
            {
                OpenUrl(image.CivitaiImageId > 0 ? image.CivitaiImageUrl : image.CivitaiPostUrl);
            }
            e.Handled = true;
        }

        private void ShowInSearch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ResolvedPostImage image)
            {
                ShowInSearch(image);
            }
            e.Handled = true;
        }

        private void OpenLocal_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ResolvedPostImage image)
            {
                OpenLocalFile(image);
            }
            e.Handled = true;
        }

        private void OpenSelectedOnCivitai_Click(object sender, RoutedEventArgs e)
        {
            var image = _model.SelectedImage;
            if (image != null)
            {
                OpenUrl(image.CivitaiImageId > 0 ? image.CivitaiImageUrl : image.CivitaiPostUrl);
            }
        }

        private void ShowSelectedInSearch_Click(object sender, RoutedEventArgs e)
        {
            var image = _model.SelectedImage;
            if (image != null)
            {
                ShowInSearch(image);
            }
        }

        private void OpenSelectedLocal_Click(object sender, RoutedEventArgs e)
        {
            var image = _model.SelectedImage;
            if (image != null)
            {
                OpenLocalFile(image);
            }
        }

        /// <summary>
        /// Reveals the matched local file in the Search page, filtered down to
        /// that one image, so the full library UI (large preview, metadata,
        /// albums, tagging) is available for it. The Search page is private to
        /// MainWindow, so this goes through SearchService like every other
        /// cross-page action.
        /// </summary>
        private static void ShowInSearch(ResolvedPostImage image)
        {
            if (image.LocalPath == null || !File.Exists(image.LocalPath))
            {
                ServiceLocator.ToastService?.Toast(
                    "This image has no local file to show.", "Show in Search");
                return;
            }

            // Navigate first: Search.Navigate re-runs SearchImages on arrival,
            // which would discard a query set beforehand.
            ServiceLocator.NavigatorService.Goto("search");
            ServiceLocator.SearchService.ExecuteShowImagePath(image.LocalPath);
        }

        /// <summary>Hands the file to the Windows default image viewer.</summary>
        private static void OpenLocalFile(ResolvedPostImage image)
        {
            if (image.LocalPath != null && File.Exists(image.LocalPath))
            {
                Process.Start(new ProcessStartInfo(image.LocalPath) { UseShellExecute = true });
            }
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start("explorer.exe", $"\"{url}\"");
            }
            catch (Exception ex)
            {
                Logger.Log($"CivitaiCalendar: failed to open URL {url}: {ex.Message}");
            }
        }

        // --- Drag-to-schedule -------------------------------------------------

        private void DayCell_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;
            if (sender is FrameworkElement fe && fe.Tag is CalendarDayModel day
                && day.Date.Date >= DateTime.Today && !day.IsBeyondScheduleLimit
                && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            e.Handled = true;
        }

        private void DayCell_Drop(object sender, DragEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not CalendarDayModel day) return;
            if (day.Date.Date < DateTime.Today) return;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (day.IsBeyondScheduleLimit)
            {
                // DragOver already refuses the drop; this covers the drag
                // sources that ignore the effect and drop anyway.
                ServiceLocator.ToastService?.Toast(day.ScheduleLimitTooltip, "CivitAI");
                return;
            }

            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var file = files?.FirstOrDefault();
            if (file == null || !File.Exists(file)) return;

            var dialog = new SchedulePostWindow(file, day.Date) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true)
            {
                _ = SchedulePostAsync(file, dialog.PublishAt, dialog.PostTitle);
            }
        }

        // The preview image itself is a drag source: drag it onto a future day
        // to schedule that local file as a new post.
        private void PreviewImage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _previewDragStart = e.GetPosition(this);
        }

        private void PreviewImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var path = _model.SelectedImage?.LocalPath;
            if (path == null || !File.Exists(path)) return;

            var position = e.GetPosition(this);
            if (Math.Abs(position.X - _previewDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(position.Y - _previewDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
        }

        private async Task SchedulePostAsync(string file, DateTime publishAt, string? title)
        {
            bool scheduled = false;
            _model.IsRefreshing = true;
            _model.StatusText = "Scheduling post…";
            try
            {
                var result = await _service.SchedulePostAsync(file, publishAt, title);
                if (result.IsAuthFailure)
                {
                    MessageBox.Show(Window.GetWindow(this), CivitaiPostsService.AuthFailureMessage,
                        "Authentication Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!result.Success)
                {
                    MessageBox.Show(Window.GetWindow(this),
                        $"Failed to schedule the post:\n{result.Message}",
                        "Schedule Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                ServiceLocator.ToastService?.Toast($"Scheduled post for {publishAt:g}", "CivitAI");
                scheduled = true;
            }
            finally
            {
                _model.IsRefreshing = false;
                _model.StatusText = null;
            }

            // Refresh outside the guard so the shared runner can take over cleanly.
            if (scheduled)
            {
                await FetchUpcomingAsync();
            }
        }
    }
}
