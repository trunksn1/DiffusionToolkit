namespace Diffusion.Toolkit.Models;

/// <summary>
/// A single user-supplied overlay metadata row shown in the preview panel.
/// Purely a display/edit projection of a Diffusion.Database.Models.UserMetadata record.
/// </summary>
public class UserMetadataItemViewModel : BaseNotify
{
    private string _key;
    private string? _value;
    private string _source = "manual";
    private string? _sourceUrl;

    public string Key
    {
        get => _key;
        set => SetField(ref _key, value);
    }

    public string? Value
    {
        get => _value;
        set => SetField(ref _value, value);
    }

    /// <summary>"manual" or "civitai".</summary>
    public string Source
    {
        get => _source;
        set
        {
            if (SetField(ref _source, value))
            {
                OnPropertyChanged(nameof(IsFromCivitai));
            }
        }
    }

    public string? SourceUrl
    {
        get => _sourceUrl;
        set
        {
            if (SetField(ref _sourceUrl, value))
            {
                OnPropertyChanged(nameof(HasSourceUrl));
            }
        }
    }

    public bool IsFromCivitai => string.Equals(_source, "civitai", System.StringComparison.OrdinalIgnoreCase);

    public bool HasSourceUrl => !string.IsNullOrWhiteSpace(_sourceUrl);
}
