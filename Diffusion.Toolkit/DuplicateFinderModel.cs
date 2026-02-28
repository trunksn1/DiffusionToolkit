using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Diffusion.Toolkit.Models;

namespace Diffusion.Toolkit;

public class DuplicateGroupItem : BaseNotify
{
    public int Id { get; set; }
    public string Path { get; set; }
    public string FileName { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long FileSize { get; set; }
    public int Distance { get; set; }
    public double Similarity { get; set; }
    public DateTime CreatedDate { get; set; }
    public int? Rating { get; set; }
    public bool Favorite { get; set; }
    public string Resolution => $"{Width}x{Height}";
    public string FileSizeFormatted => FormatFileSize(FileSize);

    public bool IsSelected
    {
        get;
        set => SetField(ref field, value);
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}

public class DuplicateGroup : BaseNotify
{
    public int GroupIndex { get; set; }
    public ObservableCollection<DuplicateGroupItem> Items { get; set; } = new();
    public int Count => Items.Count;
}

public class DuplicateFinderModel : BaseNotify
{
    public ICommand Escape { get; set; }
    public ICommand ScanCommand { get; set; }
    public ICommand DeleteSelectedCommand { get; set; }
    public ICommand MarkForDeletionCommand { get; set; }

    public int Threshold
    {
        get;
        set => SetField(ref field, value);
    } = 90;

    public int MaxDistance => (int)((100 - Threshold) / 100.0 * 64);

    public bool IsScanning
    {
        get;
        set => SetField(ref field, value);
    }

    public string Status
    {
        get;
        set => SetField(ref field, value);
    } = "Ready";

    public int Progress
    {
        get;
        set => SetField(ref field, value);
    }

    public int ProgressMax
    {
        get;
        set => SetField(ref field, value);
    } = 100;

    public ObservableCollection<DuplicateGroup> DuplicateGroups
    {
        get;
        set => SetField(ref field, value);
    } = new();

    public int TotalGroups
    {
        get;
        set => SetField(ref field, value);
    }

    public int TotalDuplicates
    {
        get;
        set => SetField(ref field, value);
    }
}
