using System.Collections.ObjectModel;
using System.Windows.Input;
using Diffusion.Database.Models;
using Diffusion.Toolkit.Models;

namespace Diffusion.Toolkit;

public class SmartAlbumRuleViewModel : BaseNotify
{
    public SmartAlbumField Field
    {
        get;
        set => SetField(ref field, value);
    }

    public SmartAlbumOperator Operator
    {
        get;
        set => SetField(ref field, value);
    }

    public string Value
    {
        get;
        set => SetField(ref field, value);
    } = "";

    public ObservableCollection<SmartAlbumOperator> AvailableOperators
    {
        get;
        set => SetField(ref field, value);
    } = new();
}

public class SmartAlbumEditorModel : BaseNotify
{
    public ICommand Escape { get; set; }

    public string Name
    {
        get;
        set => SetField(ref field, value);
    } = "";

    public bool MatchAll
    {
        get;
        set => SetField(ref field, value);
    } = true;

    public ObservableCollection<SmartAlbumRuleViewModel> Rules
    {
        get;
        set => SetField(ref field, value);
    } = new();

    public ObservableCollection<SmartAlbum> SmartAlbums
    {
        get;
        set => SetField(ref field, value);
    } = new();

    public SmartAlbum? SelectedSmartAlbum
    {
        get;
        set => SetField(ref field, value);
    }

    public int PreviewCount
    {
        get;
        set => SetField(ref field, value);
    }

    public string Status
    {
        get;
        set => SetField(ref field, value);
    } = "Ready";
}
