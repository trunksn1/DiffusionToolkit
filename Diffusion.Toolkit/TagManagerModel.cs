using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Diffusion.Toolkit.Models;

namespace Diffusion.Toolkit;

public class TagManagerModel : BaseNotify
{
    public ICommand Escape { get; set; }

    public ObservableCollection<TagManagerItemView> Tags
    {
        get;
        set => SetField(ref field, value);
    } = new();

    public TagManagerItemView? SelectedTag
    {
        get;
        set => SetField(ref field, value);
    }

    public ObservableCollection<BuiltinIconItem> BuiltinIcons
    {
        get;
        set => SetField(ref field, value);
    } = new();

    public string? EmojiText
    {
        get;
        set => SetField(ref field, value);
    }

    public string? SelectedColor
    {
        get;
        set => SetField(ref field, value);
    }
}

public class BuiltinIconItem : BaseNotify
{
    public string Key { get; set; }
    public BitmapImage? Preview { get; set; }

    public bool IsSelected
    {
        get;
        set => SetField(ref field, value);
    }
}
