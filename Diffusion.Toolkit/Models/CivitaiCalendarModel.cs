using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit.Models;

public class CivitaiCalendarModel : BaseNotify
{
    public CivitaiCalendarModel()
    {
        var today = DateTime.Today;
        MinMonth = new DateTime(today.Year, today.Month, 1).AddYears(-4);
        MaxMonth = new DateTime(today.Year, today.Month, 1).AddMonths(3);
    }

    public DateTime MinMonth { get; }
    public DateTime MaxMonth { get; }

    private DateTime _currentMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    public DateTime CurrentMonth
    {
        get => _currentMonth;
        set
        {
            if (SetField(ref _currentMonth, value, false))
            {
                OnPropertyChanged(nameof(MonthLabel));
                OnPropertyChanged(nameof(CanGoPrev));
                OnPropertyChanged(nameof(CanGoNext));
            }
        }
    }

    public string MonthLabel => CurrentMonth.ToString("MMMM yyyy");
    public bool CanGoPrev => CurrentMonth > MinMonth;
    public bool CanGoNext => CurrentMonth < MaxMonth;

    public ObservableCollection<CalendarDayModel> Days { get; } = new();

    private CalendarDayModel? _selectedDay;
    public CalendarDayModel? SelectedDay
    {
        get => _selectedDay;
        set
        {
            if (SetField(ref _selectedDay, value, false))
            {
                OnPropertyChanged(nameof(HasSelectedDay));
            }
        }
    }

    public bool HasSelectedDay => _selectedDay != null;

    private string? _lastUpdated;
    public string? LastUpdated
    {
        get => _lastUpdated;
        set => SetField(ref _lastUpdated, value, false);
    }

    private bool _isRefreshing;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        set => SetField(ref _isRefreshing, value, false);
    }

    private string? _statusText;
    public string? StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value, false);
    }

    private bool _hasCache;
    public bool HasCache
    {
        get => _hasCache;
        set => SetField(ref _hasCache, value, false);
    }

    private ResolvedPostImage? _selectedImage;
    public ResolvedPostImage? SelectedImage
    {
        get => _selectedImage;
        set
        {
            if (SetField(ref _selectedImage, value, false))
            {
                OnPropertyChanged(nameof(HasSelectedImage));
                OnPropertyChanged(nameof(SelectedImageHasLocal));
            }
        }
    }

    public bool HasSelectedImage => _selectedImage != null;
    public bool SelectedImageHasLocal => _selectedImage?.LocalPath != null;

    private ImageSource? _previewImage;
    public ImageSource? PreviewImage
    {
        get => _previewImage;
        set => SetField(ref _previewImage, value, false);
    }

    private bool _showDayPanel = true;
    public bool ShowDayPanel
    {
        get => _showDayPanel;
        set => SetField(ref _showDayPanel, value, false);
    }

    private bool _showPreviewPanel = true;
    public bool ShowPreviewPanel
    {
        get => _showPreviewPanel;
        set => SetField(ref _showPreviewPanel, value, false);
    }

    public ICommand? PrevMonthCommand { get; set; }
    public ICommand? NextMonthCommand { get; set; }
    public ICommand? TodayCommand { get; set; }
    public ICommand? FetchUpcomingCommand { get; set; }
    public ICommand? RecoverHistoryCommand { get; set; }
    public ICommand? CancelFetchCommand { get; set; }
    public ICommand? DownloadMissingCommand { get; set; }
}

public class CalendarDayModel : BaseNotify
{
    public DateTime Date { get; init; }
    public bool IsCurrentMonth { get; init; }
    public bool IsToday { get; init; }
    public bool IsFuture { get; init; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value, false);
    }

    public List<ResolvedPostImage> Images { get; init; } = new();
    public int ImageCount => Images.Count;
    public bool HasImages => Images.Count > 0;
    public bool HasScheduled { get; init; }

    public int DayNumber => Date.Day;

    public ObservableCollection<ImageSource> Thumbnails { get; } = new();
}
