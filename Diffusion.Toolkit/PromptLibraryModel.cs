using System.Collections.ObjectModel;
using System.Windows.Input;
using Diffusion.Database.Models;
using Diffusion.Toolkit.Models;

namespace Diffusion.Toolkit;

public class PromptLibraryModel : BaseNotify
{
    public ICommand Escape { get; set; }

    public string? SearchQuery
    {
        get;
        set => SetField(ref field, value);
    }

    public string? SelectedCategory
    {
        get;
        set => SetField(ref field, value);
    }

    public ObservableCollection<string> Categories
    {
        get;
        set => SetField(ref field, value);
    } = new();

    public ObservableCollection<PromptTemplate> Templates
    {
        get;
        set => SetField(ref field, value);
    } = new();

    public PromptTemplate? SelectedTemplate
    {
        get;
        set => SetField(ref field, value);
    }
}
