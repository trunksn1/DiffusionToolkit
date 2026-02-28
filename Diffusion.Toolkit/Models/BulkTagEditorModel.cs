using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Diffusion.Toolkit.Models;

public class BulkTagEditorModel : BaseNotify
{
    public ObservableCollection<BulkImageTagView> Tags
    {
        get;
        set => SetField(ref field, value);
    }

    public string NewTagText
    {
        get;
        set => SetField(ref field, value);
    }

    public string StatusText
    {
        get;
        set => SetField(ref field, value);
    }

    public bool HasChanges
    {
        get;
        set => SetField(ref field, value, false);
    }

    public ICommand OkCommand { get; set; }
    public ICommand CancelCommand { get; set; }
    public ICommand EscapeCommand { get; set; }
    public ICommand AddTagCommand { get; set; }
}
