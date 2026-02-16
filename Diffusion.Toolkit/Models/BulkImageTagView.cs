namespace Diffusion.Toolkit.Models;

public class BulkImageTagView : BaseNotify
{
    public int Id { get; set; }
    public string Name { get; set; }

    public bool? OriginalState { get; set; }

    public bool IsReadOnly { get; set; }

    public bool IsEnabled => !IsReadOnly;

    public bool? IsChecked
    {
        get;
        set => SetField(ref field, value);
    }
}
