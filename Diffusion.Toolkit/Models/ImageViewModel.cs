using Diffusion.Database.Models;
using Diffusion.Toolkit.Classes;
using Diffusion.Toolkit.Controls;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Diffusion.Common;
using Diffusion.Toolkit.Services;
using Node = Diffusion.IO.Node;

namespace Diffusion.Toolkit.Models;

public class ImageViewModel : BaseNotify
{
    private string _prompt;
    private string _negativePrompt;
    private string _otherParameters;

    private string _modelHash;

    // CivitAI Extension Data
    private string? _civitaiLoraRiforgiati;
    private string? _civitaiLoraInForge;
    private string? _civitaiReforgedTags;
    private string? _civitaiLoraHashes;
    private string? _civitaiTiHashes;
    private string? _civitaiPicMetadata;
    private string? _civitaiExif;
    private bool _hasCivitaiData;
    private bool _isLoraRiforgiatiEditMode;
    private ICommand? _toggleLoraRiforgiatiEditCommand;
    private ICommand? _saveLoraRiforgiatiCommand;
    private string? _civitaiImageId;
    private ICommand? _openCivitaiPageCommand;

    public ImageViewModel()
    {
        CopyPathCommand = new RelayCommand<object>(ServiceLocator.ContextMenuService.CopyPath);
        CopyPromptCommand = new RelayCommand<object>(ServiceLocator.ContextMenuService.CopyPrompt);
        CopyNegativePromptCommand = new RelayCommand<object>(ServiceLocator.ContextMenuService.CopyNegative);
        //_model.CurrentImage.CopySeed = new RelayCommand<object>(CopySeed);
        //_model.CurrentImage.CopyHash = new RelayCommand<object>(CopyHash);
        CopyOthersCommand = new RelayCommand<object>(ServiceLocator.ContextMenuService.CopyOthers);
        CopyParametersCommand = new RelayCommand<object>(ServiceLocator.ContextMenuService.CopyParameters);
        //ShowInExplorerCommand = new RelayCommand<object>(ServiceLocator.ContextMenuService.ShowInExplorer);
    }

    public MainModel MainModel => ServiceLocator.MainModel;

    public int Id { get; set; }

    public bool IsMessageVisible
    {
        get;
        set => SetField(ref field, value);
    }

    public BitmapSource? Image
    {
        get;
        set => SetField(ref field, value);
    }

    public string Path
    {
        get;
        set => SetField(ref field, value);
    }

    public string? Prompt
    {
        get => _prompt;
        set => SetField(ref _prompt, value);
    }

    public string? NegativePrompt
    {
        get => _negativePrompt;
        set => SetField(ref _negativePrompt, value);
    }

    public string? OtherParameters
    {
        get => _otherParameters;
        set => SetField(ref _otherParameters, value);
    }

    public decimal CFGScale
    {
        get;
        set => SetField(ref field, value);
    }

    public int Height
    {
        get;
        set => SetField(ref field, value);
    }

    public int Width
    {
        get;
        set => SetField(ref field, value);
    }

    public string ModelName
    {
        get;
        set => SetField(ref field, value);
    }


    public string Date
    {
        get;
        set => SetField(ref field, value);
    }

    public ICommand CopyPromptCommand
    {
        get;
        set => SetField(ref field, value);
    }

    public ICommand SearchModelCommand
    {
        get;
        set => SetField(ref field, value);
    }

    public ICommand CopyPathCommand
    {
        get;
        set => SetField(ref field, value);
    }

    public ICommand ShowInExplorerCommand
    {
        get;
        set => SetField(ref field, value);
    }

    public ICommand DeleteCommand
    {
        get;
        set => SetField(ref field, value);
    }

    public ICommand FavoriteCommand
    {
        get;
        set => SetField(ref field, value);
    }


    public ICommand CopyNegativePromptCommand
    {
        get;
        set => SetField(ref field, value);
    }

    public ICommand CopyOthersCommand
    {
        get;
        set => SetField(ref field, value);
    }

    public ICommand CopyParametersCommand
    {
        get;
        set => SetField(ref field, value);
    }

    public bool Favorite
    {
        get;
        set => SetField(ref field, value);
    }

    public int? Rating
    {
        get;
        set => SetField(ref field, value);
    }

    public bool ForDeletion
    {
        get;
        set => SetField(ref field, value);
    }

    public bool NSFW
    {
        get;
        set => SetField(ref field, value);
    }

    public bool HasError
    {
        get;
        set => SetField(ref field, value);
    }

    public ICommand ShowInThumbnails
    {
        get;
        set => SetField(ref field, value);
    }

    public bool IsParametersVisible
    {
        get;
        set => SetField(ref field, value);
    }

    public ICommand ToggleParameters
    {
        get;
        set => SetField(ref field, value);
    }

    public long Seed
    {
        get;
        set => SetField(ref field, value);
    }

    public string? ModelHash
    {
        get => _modelHash;
        set => SetField(ref _modelHash, value);
    }

    public string? AestheticScore
    {
        get;
        set => SetField(ref field, value);
    }

    public IEnumerable<Album> Albums
    {
        get;
        set => SetField(ref field, value);
    }

    public string? Sampler
    {
        get;
        set => SetField(ref field, value);
    }

    public int Steps
    {
        get;
        set => SetField(ref field, value);
    }

    public bool IsLoading
    {
        get;
        set => SetField(ref field, value);
    }

    public string Message
    {
        get;
        set => SetField(ref field, value);
    }

    public ICommand OpenAlbumCommand
    {
        get;
        set => SetField(ref field, value);
    }

    public ICommand RemoveFromAlbumCommand
    {
        get;
        set => SetField(ref field, value);
    }

    public string? Workflow
    {
        get;
        set => SetField(ref field, value);
    }

    public ImageType Type
    {
        get;
        set => SetField(ref field, value);
    }

    public IReadOnlyCollection<Node> Nodes
    {
        get;
        set => SetField(ref field, value);
    }

    public IReadOnlyCollection<ImageTagView> ImageTags
    {
        get;
        set => SetField(ref field, value);
    }
    
    public string ErrorMessage
    {
        get;
        set => SetField(ref field, value);
    }

    // CivitAI Extension Data Properties
    public bool HasCivitaiData
    {
        get => _hasCivitaiData;
        set => SetField(ref _hasCivitaiData, value);
    }

    public string? CivitaiLoraRiforgiati
    {
        get => _civitaiLoraRiforgiati;
        set => SetField(ref _civitaiLoraRiforgiati, value);
    }

    public string? CivitaiLoraInForge
    {
        get => _civitaiLoraInForge;
        set => SetField(ref _civitaiLoraInForge, value);
    }

    public string? CivitaiReforgedTags
    {
        get => _civitaiReforgedTags;
        set => SetField(ref _civitaiReforgedTags, value);
    }

    public string? CivitaiLoraHashes
    {
        get => _civitaiLoraHashes;
        set => SetField(ref _civitaiLoraHashes, value);
    }

    public string? CivitaiTiHashes
    {
        get => _civitaiTiHashes;
        set => SetField(ref _civitaiTiHashes, value);
    }

    public string? CivitaiPicMetadata
    {
        get => _civitaiPicMetadata;
        set => SetField(ref _civitaiPicMetadata, value);
    }

    public string? CivitaiExif
    {
        get => _civitaiExif;
        set => SetField(ref _civitaiExif, value);
    }

    public bool IsLoraRiforgiatiEditMode
    {
        get => _isLoraRiforgiatiEditMode;
        set
        {
            if (SetField(ref _isLoraRiforgiatiEditMode, value))
            {
                OnPropertyChanged(nameof(IsLoraRiforgiatiReadOnly));
            }
        }
    }

    public bool IsLoraRiforgiatiReadOnly => !_isLoraRiforgiatiEditMode;

    public ICommand? ToggleLoraRiforgiatiEditCommand
    {
        get => _toggleLoraRiforgiatiEditCommand;
        set => SetField(ref _toggleLoraRiforgiatiEditCommand, value);
    }

    public ICommand? SaveLoraRiforgiatiCommand
    {
        get => _saveLoraRiforgiatiCommand;
        set => SetField(ref _saveLoraRiforgiatiCommand, value);
    }

    public string? CivitaiImageId
    {
        get => _civitaiImageId;
        set => SetField(ref _civitaiImageId, value);
    }

    public bool HasCivitaiImageId => !string.IsNullOrEmpty(_civitaiImageId);

    public ICommand? OpenCivitaiPageCommand
    {
        get => _openCivitaiPageCommand;
        set => SetField(ref _openCivitaiPageCommand, value);
    }
}


public class ImageTagView : BaseNotify
{
    public int Id { get; set; }
    public string Name { get; set; }

    public bool IsTicked
    {
        get; 
        set => SetField(ref field, value); 
    }
}

public class TagFilterView : BaseNotify
{
    public int Id { get; set; }
    public string Name { get; set; }
    public int TagCount { get; set; }

    public bool IsTicked
    {
        get;
        set => SetField(ref field, value);
    }
}