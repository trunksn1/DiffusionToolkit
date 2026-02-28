using System.Windows.Media.Imaging;

namespace Diffusion.Toolkit.Models;

public class BulkImageTagView : BaseNotify
{
    public int Id { get; set; }
    public string Name { get; set; }

    public bool? OriginalState { get; set; }

    public bool IsReadOnly { get; set; }

    public bool IsEnabled => !IsReadOnly;

    public BitmapImage? IconPreview { get; set; }

    public bool? IsChecked
    {
        get;
        set => SetField(ref field, value);
    }
}
