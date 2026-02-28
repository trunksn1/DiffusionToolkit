using System.Collections.ObjectModel;
using System.Windows.Input;
using Diffusion.Toolkit.Models;

namespace Diffusion.Toolkit;

public class SavePromptTemplateModel : BaseNotify
{
    public ICommand Escape { get; set; }

    public string Name
    {
        get;
        set => SetField(ref field, value);
    }

    public string? Prompt
    {
        get;
        set => SetField(ref field, value);
    }

    public string? NegativePrompt
    {
        get;
        set => SetField(ref field, value);
    }

    public string? Category
    {
        get;
        set => SetField(ref field, value);
    }

    public string? Notes
    {
        get;
        set => SetField(ref field, value);
    }

    public string? Model
    {
        get;
        set => SetField(ref field, value);
    }

    public string? Sampler
    {
        get;
        set => SetField(ref field, value);
    }

    public decimal? CFGScale
    {
        get;
        set => SetField(ref field, value);
    }

    public int? Steps
    {
        get;
        set => SetField(ref field, value);
    }

    public ObservableCollection<string> Categories
    {
        get;
        set => SetField(ref field, value);
    } = new();
}
