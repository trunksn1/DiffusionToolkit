using System.Windows.Media.Imaging;

namespace Diffusion.Toolkit.Models;

public class TagManagerItemView : BaseNotify
{
    public int Id { get; set; }

    public string Name
    {
        get;
        set => SetField(ref field, value);
    }

    public string? Icon
    {
        get;
        set => SetField(ref field, value);
    }

    public int ImageCount { get; set; }

    public BitmapImage? IconPreview
    {
        get;
        set => SetField(ref field, value);
    }
}
