using Diffusion.Toolkit.Classes;

namespace Diffusion.Toolkit.Models;

public class SmartAlbumViewModel : BaseNotify
{
    public int Id
    {
        get;
        set => SetField(ref field, value);
    }

    public string Name
    {
        get;
        set => SetField(ref field, value);
    }

    public string RulesJson
    {
        get;
        set => SetField(ref field, value);
    }

    public bool MatchAll
    {
        get;
        set => SetField(ref field, value);
    }
}
