using Diffusion.Common;
using Diffusion.Database;
using Diffusion.Toolkit.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Path = System.IO.Path;
using Diffusion.Toolkit.Classes;
using Search = Diffusion.Toolkit.Pages.Search;
using Diffusion.Toolkit.Thumbnails;
using Diffusion.IO;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Toolkit.Themes;
using Microsoft.Win32;
using Diffusion.Toolkit.Pages;
using MessageBox = System.Windows.MessageBox;
using Model = Diffusion.Common.Model;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Diffusion.Civitai.Models;
using WPFLocalizeExtension.Engine;
using Diffusion.Toolkit.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Xml;
using Diffusion.Toolkit.Services;
using Diffusion.Toolkit.Common;
using Settings = Diffusion.Toolkit.Configuration.Settings;
using Diffusion.Database.Models;
using Image = System.Windows.Controls.Image;
using System.Windows.Media;
using SixLabors.ImageSharp;
using Size = System.Windows.Size;

namespace Diffusion.Toolkit
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : BorderlessWindow
    {
        private readonly MainModel _model;
        private NavigatorService _navigatorService;

        private DataStore _dataStore => ServiceLocator.DataStore;
        private Settings _settings;



        private Configuration<Settings> _configuration;
        //private Toolkit.Settings? _settings;


        private Search _search;
        private Pages.Settings _settingsPage;
        private Pages.Models _models;
        private bool _tipsOpen;
        private MessagePopupManager _messagePopupManager;

        public MainWindow()
        {

            try
            {
                Logger.Log("===========================================");
                Logger.Log($"Started Diffusion Toolkit {AppInfo.Version}");


                _configuration = new Configuration<Settings>(AppInfo.SettingsPath, AppInfo.IsPortable);


                //Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                //Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");

                InitializeComponent();

                AppDomain currentDomain = AppDomain.CurrentDomain;
                currentDomain.UnhandledException += new UnhandledExceptionEventHandler(GlobalExceptionHandler);

                QueryBuilder.Samplers = File.ReadAllLines("samplers.txt").ToList();

                Logger.Log($"Creating Thumbnail loader");



                _navigatorService = new NavigatorService(this);
                _navigatorService.OnNavigate += OnNavigate;

                ServiceLocator.SetNavigatorService(_navigatorService);

                SystemEvents.UserPreferenceChanged += SystemEventsOnUserPreferenceChanged;

                ServiceLocator.WindowService.SetWindow(this);

                _model = new MainModel();
                _model.CurrentProgress = 0;
                _model.TotalProgress = 100;

                ServiceLocator.MainModel = _model;
                ServiceLocator.Dispatcher = Dispatcher;

                _model.RenameFileCommand = new AsyncCommand<string>((o) => RenameImageEntry());
                _model.OpenWithCommand = new AsyncCommand<string>((o) => OpenWith(this, o));
                _model.Rescan = new AsyncCommand<object>(RescanTask);
                _model.Rebuild = new AsyncCommand<object>(RebuildTask);

                _model.LaunchCivitaiScraperCommand = new RelayCommand<object>((o) => LaunchCivitaiScraper());
                _model.LaunchCivitaiPipelineCommand = new AsyncCommand<object>(async (o) => await LaunchCivitaiPipeline());
                _model.BackfillAuthorTagsCommand = new AsyncCommand<object>(async (o) => await BackfillAuthorTags());
                _model.LaunchComfyUICommand = new RelayCommand<object>((o) => LaunchComfyUI());

                _model.ReloadHashes = new AsyncCommand<object>(async (o) =>
                {
                    LoadModels();
                    await _messagePopupManager.Show("Models have been reloaded", "Diffusion Toolkit", PopupButtons.OK);
                });

                _model.SettingsCommand = new RelayCommand<object>(ShowSettings);

                _model.CancelCommand = new AsyncCommand<object>((o) => CancelProgress());
                _model.AboutCommand = new RelayCommand<object>((o) => ShowAbout());

                InitEvents();
                InitTags();
                InitAlbums();
                InitQueries();

                _model.Refresh = new RelayCommand<object>((o) => Refresh());

                // TODO: Remove
                _model.QuickCopy = new RelayCommand<object>((o) =>
                {
                    var win = new QuickCopy();
                    win.Owner = this;
                    win.ShowDialog();
                });

                _model.Escape = new RelayCommand<object>((o) => Escape());

                _model.PropertyChanged += ModelOnPropertyChanged;



                this.Loaded += OnLoaded;
                this.Closing += OnClosing;
                _model.CloseCommand = new RelayCommand<object>(o =>
                {
                    this.Close();
                });

                DataContext = _model;


                _messagePopupManager = new MessagePopupManager(this, PopupHost, Frame, Dispatcher);

                ServiceLocator.MessageService = new MessageService(_messagePopupManager);
                ServiceLocator.ToastService = new ToastService(ToastPopup);

                PreviewMouseDown += (sender, args) =>
                {
                    if (args.Source == QueryInput) return;
                    QueryPopup.IsOpen = false;
                };

                Deactivated += (sender, args) =>
                {
                    //QueryPopup.IsOpen = false;
                    //ToastPopup.IsOpen = false;
                    //_messagePopupManager.Cancel();
                };


                StateChanged += (sender, args) =>
                {
                    if (WindowState == WindowState.Minimized)
                    {
                        QueryPopup.IsOpen = false;
                        ToastPopup.IsOpen = false;
                        _messagePopupManager.Cancel();
                    }
                };

                string menuLanguage= null;

                // TODO: Find a better eay that doesn't get called even when mouseover?
                // Maybe only when the language has changed?
                Menu.LayoutUpdated += (sender, args) =>
                {
                    if (Thread.CurrentThread.CurrentCulture.Name != menuLanguage)
                    {
                        Menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        MenuWidth = new GridLength(Menu.DesiredSize.Width + 10);
                        menuLanguage = Thread.CurrentThread.CurrentCulture.Name;
                    }
                };

                //Thread.CurrentThread.CurrentCulture = new CultureInfo("pt-PT");
                //Thread.CurrentThread.CurrentUICulture = new CultureInfo("pt-PT");
                //FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(
                //    XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

                //var control = Application.Current.FindResource(typeof(Slider));
                //using (XmlTextWriter writer = new XmlTextWriter(@"slider.xaml", System.Text.Encoding.UTF8))
                //{
                //    writer.Formatting = Formatting.Indented;
                //    XamlWriter.Save(control, writer);
                //}

            }
            catch (Exception ex)
            {
                Logger.Log(ex.Message);
            }

        }

        private void ShowInExplorer(FolderViewModel folder)
        {
            var processInfo = new ProcessStartInfo()
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder.Path}\"",
                UseShellExecute = true
            };

            Process.Start(processInfo);

            //Process.Start("explorer.exe", $"/select,\"{p}\"");
        }

        private async Task UnavailableFiles(object o)
        {
            var window = new UnavailableFilesWindow();
            window.Owner = this;
            window.ShowDialog();

            if (window.DialogResult is true)
            {
                if (window.Model.RemoveImmediately)
                {
                    var result = await ServiceLocator.MessageService.Show("Are you sure you want to remove all unavailable files?", "Scan for Unavailable Images", PopupButtons.YesNo);
                    if (result == PopupResult.No)
                    {
                        return;
                    }
                }

                Task.Run(async () =>
                {
                    if (await ServiceLocator.ProgressService.TryStartTask())
                    {
                        try
                        {
                            await ServiceLocator.ScanningService.ScanUnavailable(window.Model, ServiceLocator.ProgressService.CancellationToken);
                        }
                        finally
                        {
                            ServiceLocator.ProgressService.CompleteTask();
                            ServiceLocator.ProgressService.SetStatus(GetLocalizedText("Actions.Scanning.Completed"));
                        }
                    }
                });

            }


        }

        private void DuplicateFinder_OnClick(object sender, RoutedEventArgs e)
        {
            var window = new DuplicateFinderWindow();
            window.Owner = this;
            window.Show();
        }

        private async void BackfillHashes_OnClick(object sender, RoutedEventArgs e)
        {
            var totalMissing = ServiceLocator.DataStore.CountImagesWithoutPerceptualHash();

            if (totalMissing == 0)
            {
                MessageBox.Show("All images already have perceptual hashes computed.", "Backfill Perceptual Hashes",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                $"This will compute perceptual hashes for {totalMissing:N0} images that don't have one yet.\nThis may take a while for large libraries.\n\nContinue?",
                "Backfill Perceptual Hashes", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            if (!await ServiceLocator.ProgressService.TryStartTask())
                return;

            try
            {
                ServiceLocator.ProgressService.InitializeProgress(totalMissing);
                ServiceLocator.ProgressService.SetStatus($"Backfilling perceptual hashes: 0 of {totalMissing:N0}...");

                var processed = await Task.Run(async () =>
                {
                    return await ServiceLocator.PerceptualHashService.BackfillHashes(
                        (current, total) =>
                        {
                            ServiceLocator.ProgressService.SetProgress(current,
                                "Backfilling perceptual hashes: {current} of {total}...");
                        }, ServiceLocator.ProgressService.CancellationToken);
                });

                ServiceLocator.ProgressService.SetStatus($"Backfill complete: {processed:N0} hashes computed");
                ServiceLocator.ToastService.Toast($"Computed {processed:N0} perceptual hashes", "Backfill Complete");
            }
            catch (OperationCanceledException)
            {
                ServiceLocator.ProgressService.SetStatus("Backfill cancelled");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during backfill: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ServiceLocator.ProgressService.ClearProgress();
                ServiceLocator.ProgressService.CompleteTask();
            }
        }

        private void PromptLibrary_OnClick(object sender, RoutedEventArgs e)
        {
            var window = new PromptLibraryWindow();
            window.Owner = this;
            window.Show();
        }

        private void TagManager_OnClick(object sender, RoutedEventArgs e)
        {
            var window = new TagManagerWindow();
            window.Owner = this;
            window.Show();
        }

        private void SmartAlbums_OnClick(object sender, RoutedEventArgs e)
        {
            var window = new SmartAlbumEditorWindow();
            window.Owner = this;
            window.Show();
        }

        private void ToggleNavigationPane()
        {
            _model.Settings.NavigationSection.ToggleSection();
        }

        private void ResetLayout()
        {
            _search.ResetLayout();
        }

        private void ToggleVisibility(string s)
        {
            switch (s)
            {
                case "Navigation.Folders":
                    _model.Settings.NavigationSection.ShowFolders = !_model.Settings.NavigationSection.ShowFolders;
                    break;
                case "Navigation.Models":
                    _model.Settings.NavigationSection.ShowModels = !_model.Settings.NavigationSection.ShowModels;
                    break;
                case "Navigation.Albums":
                    _model.Settings.NavigationSection.ShowAlbums = !_model.Settings.NavigationSection.ShowAlbums;
                    break;
                case "Navigation.Queries":
                    _model.Settings.NavigationSection.ShowQueries = !_model.Settings.NavigationSection.ShowQueries;
                    break;
            }
        }

        private void ToggleAutoRefresh()
        {
            _settings.AutoRefresh = !_settings.AutoRefresh;
        }

        private void Escape()
        {
            _messagePopupManager.Cancel();
        }

        private void Refresh()
        {
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                _search.SearchImages(null);
            }
            else
            {
                _search.ReloadMatches(null);
            }

            _ = ServiceLocator.FolderService.LoadFolders();
        }

        private PreviewWindow? _previewWindow;

        private void PopoutPreview(bool hidePreview, bool maximized, bool fullscreen)
        {
            if (_previewWindow == null)
            {
                if (hidePreview)
                {
                    _model.IsPreviewVisible = false;
                    _search.SetPreviewVisible(_model.IsPreviewVisible);
                }

                _previewWindow = new PreviewWindow();

                _previewWindow.WindowState = maximized ? WindowState.Maximized : WindowState.Normal;

                _previewWindow.Owner = this;

                // TODO: better implementation for StartNavigation events
                _previewWindow.PreviewKeyUp += _search.ExtOnKeyUp;
                _previewWindow.PreviewKeyDown += _search.ExtOnKeyDown;

                _previewWindow.AdvanceSlideShow = _search.Advance;

                _previewWindow.OnDrop = (s) => _search.LoadPreviewImage(s);
                //_previewWindow.Changed = (id) => _search.Update(id);

                _previewWindow.Closed += (sender, args) =>
                {
                    _search.OnCurrentImageChange = null;
                    _search.ThumbnailListView.FocusCurrentItem();
                    _previewWindow = null;
                    _search.NavigationCompleted -= SearchOnNavigationCompleted;
                };
                _previewWindow.SetCurrentImage(_search.CurrentImage);

                _search.NavigationCompleted += SearchOnNavigationCompleted;

                _search.OnCurrentImageChange = (image) =>
                {
                    _previewWindow?.SetCurrentImage(image);
                };

                void UpdatePreviewWindowState()
                {
                    _settings.PreviewWindowState.Top = _previewWindow.Top;
                    _settings.PreviewWindowState.Left = _previewWindow.Left;
                    _settings.PreviewWindowState.Height = _previewWindow.Height;
                    _settings.PreviewWindowState.Width = _previewWindow.Width;
                    _settings.PreviewWindowState.State = _previewWindow.WindowState;
                    _settings.PreviewWindowState.IsFullScreen = _previewWindow.IsFullScreen;
                    _settings.PreviewWindowState.IsSet = true;
                    _settings.SetDirty();
                }

                _previewWindow.LocationChanged += (sender, args) =>
                {
                    UpdatePreviewWindowState();
                };

                _previewWindow.SizeChanged += (sender, args) =>
                {
                    UpdatePreviewWindowState();
                };

                _previewWindow.StateChanged += (sender, args) =>
                {
                    UpdatePreviewWindowState();
                };

                if (_settings.PreviewWindowState.IsSet)
                {
                    _previewWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                    _previewWindow.WindowState = _settings.PreviewWindowState.State;
                    _previewWindow.Width = _settings.PreviewWindowState.Width;
                    _previewWindow.Height = _settings.PreviewWindowState.Height;
                    _previewWindow.Top = _settings.PreviewWindowState.Top;
                    _previewWindow.Left = _settings.PreviewWindowState.Left;
                }

                if (fullscreen || _settings.PreviewWindowState.IsFullScreen)
                {
                    _previewWindow.ShowFullScreen();
                }
                else
                {
                    _previewWindow.Show();
                }

            }
            else
            {
                _previewWindow.SetCurrentImage(_search.CurrentImage);
            }
        }

        private void SearchOnNavigationCompleted(object? sender, EventArgs e)
        {
            _previewWindow?.SetFocus();
        }


        private void GlobalExceptionHandler(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = ((Exception)e.ExceptionObject);

            Logger.Log($"An unhandled exception occured: {exception.Message}\r\n\r\n{exception.StackTrace}");

            MessageBox.Show(this, exception.Message, "An unhandled exception occured", MessageBoxButton.OK, MessageBoxImage.Exclamation);

        }

        private void TogglePreview()
        {
            _model.IsPreviewVisible = !_model.IsPreviewVisible;
            _search.SetPreviewVisible(_model.IsPreviewVisible);
        }

        private void SetThumbnailSize(int size)
        {
            _settings.ThumbnailSize = size;
            ServiceLocator.ThumbnailService.Size = _settings.ThumbnailSize;
            _model.ThumbnailSize = _settings.ThumbnailSize;
            _search.SetThumbnailSize(_settings.ThumbnailSize);
            _prompts.SetThumbnailSize(_settings.ThumbnailSize);
        }

        private void SystemEventsOnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.Color)
            {
                UpdateTheme(_settings.Theme);
            }
        }

        private void ModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainModel.ShowIcons))
            {
                _search.SetOpacityView(_model.ShowIcons);
            }
            else if (e.PropertyName == nameof(MainModel.HideIcons))
            {
                _search.SetIconVisibility(_model.HideIcons);
            }
            else if (e.PropertyName == nameof(MainModel.SelectedImages))
            {
                _search.UpdateResults();
            }
            else if (e.PropertyName == nameof(MainModel.CurrentAlbum))
            {
                //Debug.WriteLine(_model.CurrentAlbum.Name);
                //_search.SetView("albums");
                //_search.SearchImages(null);
            }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            var dataStore = new DataStore(AppInfo.DatabasePath);
            var _showReleaseNotes = false;

            ServiceLocator.SetDataStore(dataStore);

            // Initialize CivitAI extension data store (external database)
            const string civitaiExtensionDbPath = @"C:\Users\trunk\AppData\Roaming\DiffusionToolkit\civitai-extension-trimmed.db";
            var civitAiExtensionDataStore = new CivitAiExtensionDataStore(civitaiExtensionDbPath);
            ServiceLocator.SetCivitAiExtensionDataStore(civitAiExtensionDataStore);

            // Share CivitAI extension data store with DataStore for filter queries
            dataStore.SetCivitAiExtensionDataStore(civitAiExtensionDataStore);

            var isFirstTime = false;
            IReadOnlyList<string> newFolders = null;

            if (!_configuration.Exists())
            {
                Logger.Log($"Opening Settings for first time");

                _settings = new Settings();

                UpdateTheme(_settings.Theme);

                var welcome = new WelcomeWindow(_settings);
                welcome.Owner = this;
                welcome.ShowDialog();

                newFolders = welcome.SelectedPaths;

                if (_settings.IsDirty())
                {
                    _configuration.Save(_settings);
                    _settings.SetPristine();
                }

                ThumbnailCache.CreateInstance(_settings.PageSize * 5, _settings.PageSize * 2);

                isFirstTime = true;
            }
            else
            {
                try
                {
                    _configuration.Load(out _settings);

                    _settings.MetadataSection.Attach(_settings);
                    _settings.NavigationSection.Attach(_settings);
                    _settings.UseBuiltInViewer ??= true;
                    _settings.SortAlbumsBy ??= "Name";
                    _settings.Theme ??= "System";

                    UpdateTheme(_settings.Theme);

                    _settings.PortableMode = _configuration.Portable;
                    _settings.SetPristine();

                    ThumbnailCache.CreateInstance(_settings.PageSize * 5, _settings.PageSize * 2);
                }
                catch (Exception exception)
                {
                    MessageBox.Show(this, "An error occured while loading configuration settings. The application will exit", "Startup failed!", MessageBoxButton.OK, MessageBoxImage.Error);
                    throw;
                }

            }

            if (!SemanticVersion.TryParse(_settings.Version, out var semVer))
            {
                semVer = SemanticVersion.Parse("v0.0.0");
            }


            // TODO: Find a better place to put this:
            // Set defaults for new version features



            if (semVer < SemanticVersion.Parse("v1.9.0"))
            {
                _settings.ShowTags = true;
                _settings.NavigationSection.ShowFolders = true;
                _settings.NavigationSection.ShowAlbums = true;
                _settings.NavigationSection.ShowQueries = true;
                _settings.ConfirmDeletion = true;
                _settings.PermanentlyDelete = false;
            }

            if (semVer < SemanticVersion.Parse("v1.10.0"))
            {
                _settings.FileExtensions = $"{_settings.FileExtensions}, .mp4";
                _settings.LoopVideo = true;
                _settings.RenderMode = RenderMode.Default;
            }

            if (semVer < AppInfo.Version)
            {
                _showReleaseNotes = true;
                _settings.Version = AppInfo.Version.ToString();
            }


            _settings.PropertyChanged += (s, args) =>
            {
                if (!_isClosing)
                {
                    OnSettingsChanged(args);
                }
            };

            if (_settings.WindowState.HasValue)
            {
                this.WindowState = _settings.WindowState.Value;
            }

            if (_settings.WindowSize.HasValue)
            {
                this.Width = _settings.WindowSize.Value.Width;
                this.Height = _settings.WindowSize.Value.Height;
            }

            if (_settings.Top.HasValue)
            {
                this.Top = _settings.Top.Value;
            }

            if (_settings.Left.HasValue)
            {
                this.Left = _settings.Left.Value;
            }

            _settings.Culture ??= "default";

            if (_settings.Culture == "default")
            {
                LocalizeDictionary.Instance.Culture = CultureInfo.CurrentCulture;
            }
            else
            {
                LocalizeDictionary.Instance.Culture = new CultureInfo(_settings.Culture);
            }

            _model.AutoRefresh = _settings.AutoRefresh;
            _model.HideNSFW = _settings.HideNSFW;
            _model.HideDeleted = _settings.HideDeleted;
            _model.HideUnavailable = _settings.HideUnavailable;
            _model.PermanentlyDelete = _settings.PermanentlyDelete;

            // TODO: Get rid of globals
            QueryBuilder.HideNSFW = _model.HideNSFW;
            QueryBuilder.HideDeleted = _model.HideDeleted;
            QueryBuilder.HideUnavailable = _model.HideUnavailable;

            _model.NSFWBlur = _settings.NSFWBlur;
            _model.FitToPreview = _settings.FitToPreview;
            _model.ActualSize = _settings.ActualSize;
            _model.AutoAdvance = _settings.AutoAdvance;
            _model.ShowTags = _settings.ShowTags;
            _model.ShowNotifications = _settings.ShowNotifications;
            _model.ShowFilenames = _settings.ShowFilenames;

            _model.Settings = _settings;

            //KeyDown += (o, args) =>
            //{
            //    IInputElement focusedControl = Keyboard.FocusedElement;
            //    IInputElement focusedControl2 = FocusManager.GetFocusedElement(this);
            //};

            Activated += OnActivated;
            StateChanged += OnStateChanged;
            SizeChanged += OnSizeChanged;
            LocationChanged += OnLocationChanged;

            ServiceLocator.SetSettings(_settings);

            Logger.Log($"Initializing pages");


            await dataStore.Create(
                () => Dispatcher.Invoke(() => _messagePopupManager.ShowMessage("Please wait while we update your database", "Updating Database")),
                (handle) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        ((MessagePopupHandle)handle).CloseAsync();
                        //ServiceLocator.MessageService.CloseHandle((MessagePopupHandle)handle);
                    });
                }
            );

            // Initialize tag icon cache (must be after dataStore.Create which runs migrations)
            TagIconCache.Initialize();
            TagIconCache.RefreshTagMappings();

            //var total = _dataStore.GetTotal();

            //var text = GetLocalizedText("Main.Status.ImagesInDatabase").Replace("{count}", $"{total:n0}");

            //_model.Status = text;
            //_model.TotalProgress = 100;

            _models = new Pages.Models();

            _models.OnModelUpdated = (model) =>
            {
                var updatedModel = _modelsCollection.FirstOrDefault(m => m.Path == model.Path);
                if (updatedModel != null)
                {
                    updatedModel.SHA256 = model.SHA256;
                    _search.SetModels(_modelsCollection);
                }
            };

            _search = new Search();

            _search.MoveFiles = (files) =>
            {
                using var dialog = new CommonOpenFileDialog();
                dialog.IsFolderPicker = true;

                if (dialog.ShowDialog(this) == CommonFileDialogResult.Ok)
                {
                    var path = dialog.FileName;

                    var isInPath = false;

                    // TODO: Fix
                    throw new NotImplementedException("Too Lazy to fix");

                    // Check if the target path is in the list of selected diffusion folders

                    //if (_settings.RecurseFolders.GetValueOrDefault(true))
                    //{
                    //    foreach (var folder in ServiceLocator.FolderService.RootFolders)
                    //    {
                    //        if (path.StartsWith(folder.Path, true, CultureInfo.InvariantCulture))
                    //        {
                    //            isInPath = true;
                    //            break;
                    //        }
                    //    }

                    //    // Now check if the path falls under one of the excluded paths.

                    //    foreach (var folder in ServiceLocator.FolderService.ExcludedFolders)
                    //    {
                    //        if (path.StartsWith(folder.Path, true, CultureInfo.InvariantCulture))
                    //        {
                    //            isInPath = false;
                    //            break;
                    //        }
                    //    }
                    //}
                    //else
                    //{
                    //    // If recursion is turned off, the path must specifically equal one of the diffusion folders

                    //    foreach (var imagePath in ServiceLocator.FolderService.RootFolders.Select(d => d.Path))
                    //    {
                    //        if (path.Equals(imagePath, StringComparison.InvariantCultureIgnoreCase))
                    //        {
                    //            isInPath = true;
                    //            break;
                    //        }
                    //    }
                    //}

                    Task.Run(async () =>
                    {
                        await ServiceLocator.FileService.MoveFiles(files.Select(d => new ImagePath() { Id = d.Id, Path = d.Path }).ToList(), path, !isInPath);

                        if (!isInPath)
                        {
                            _search.SearchImages();
                        }
                    });

                }

            };

            _prompts = new Prompts();
            _settingsPage = new Pages.Settings(this);


            _model.ThumbnailSize = _settings.ThumbnailSize;
            _model.ThumbnailViewMode = _settings.ThumbnailViewMode;

            _search.SetThumbnailSize(_settings.ThumbnailSize);
            _search.SetPageSize(_settings.PageSize);

            _prompts.SetThumbnailSize(_settings.ThumbnailSize);
            _prompts.SetPageSize(_settings.PageSize);

            _search.OnPopout = () => PopoutPreview(true, true, false);
            _search.OnCurrentImageOpen = OnCurrentImageOpen;

            _model.GotoUrl = new RelayCommand<string>((url) =>
            {
                _navigatorService.Goto(url);
            });

            _navigatorService.OnNavigate += (o, args) =>
            {
                _model.ActiveView = args.TargetUri.Path.ToLower() switch
                {
                    "search" when args.TargetUri.Fragment != null => args.TargetUri.Fragment.ToLower() switch
                    {
                        "favorites" => "Favorites",
                        "deleted" => "For Deletion",
                        "images" => "Diffusions",
                        "folders" => "Folders",
                        "albums" => "Albums",
                        _ => _model.ActiveView
                    },
                    "search" => "Diffusions",
                    "models" => "Models",
                    "prompts" => "Prompts",
                    "settings" => "Settings",
                    _ => _model.ActiveView
                };
            };


            ServiceLocator.FolderService.CreateWatchers();

            //_navigatorService.Goto("search");

            Logger.Log($"Loading models");

            LoadAlbums();
            LoadSmartAlbums();
            LoadQueries();
            LoadModels();
            LoadImageModels();
            LoadTags();
            await InitFolders();

            ServiceLocator.ContextMenuService.Go();

            Logger.Log($"{_modelsCollection.Count} models loaded");

            Logger.Log($"Starting Services...");

            ServiceLocator.ThumbnailService.Size = _settings.ThumbnailSize;

            _ = ServiceLocator.ThumbnailService.StartAsync();

            if (_settings.CheckForUpdatesOnStartup)
            {
                _ = Task.Run(async () =>
                {
                    var checker = new UpdateChecker();

                    Logger.Log($"Checking for latest version");
                    try
                    {
                        var hasUpdate = await checker.CheckForUpdate();

                        if (hasUpdate)
                        {
                            var result = await _messagePopupManager.Show(GetLocalizedText("Main.Update.UpdateAvailable"), "Diffusion Toolkit", PopupButtons.YesNo);
                            if (result == PopupResult.Yes)
                            {
                                CallUpdater();
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        await _messagePopupManager.Show(exception.Message, "Update error", PopupButtons.OK);
                    }
                });
            }


            if (isFirstTime)
            {
                // Automatically scans images...
                await ServiceLocator.FolderService.ApplyFolderChanges(newFolders.Select(d => new FolderChange()
                {
                    ChangeType = ChangeType.Add,
                    FolderType = FolderType.Root,
                    Watched = true,
                    Recursive = true,
                    Path = d,
                }), confirmScan: false).ContinueWith(d =>
                {
                    ServiceLocator.NavigatorService.Goto("search");

                    if (ServiceLocator.FolderService.HasRootFolders)
                    {
                        // Wait for a bit, then show thumbnails
                        _ = Task.Delay(5000).ContinueWith(t =>
                        {
                            ServiceLocator.SearchService.ExecuteSearch();
                            ServiceLocator.MessageService.ShowMedium(GetLocalizedText("FirstScan.Message"),
                                GetLocalizedText("FirstScan.Title"), PopupButtons.OK);
                        });
                    }

                });
            }
            else
            {
                ServiceLocator.NavigatorService.Goto("search");

                if (ServiceLocator.FolderService.HasRootFolders)
                {
                    if (_settings.ScanForNewImagesOnStartup)
                    {
                        Logger.Log($"Scanning for new images");

                        _ = Task.Run(async () =>
                        {
                            if (await ServiceLocator.ProgressService.TryStartTask())
                            {
                                await ServiceLocator.ScanningService.ScanWatchedFolders(false, false, ServiceLocator.ProgressService.CancellationToken);
                            }
                        });
                    }
                }
            }

            RenderOptions.ProcessRenderMode = _settings.RenderMode;

            Logger.Log($"Init completed");

            if (_showReleaseNotes)
            {
                ShowReleaseNotes();
            }
            // Cleanup();
        }

        private async Task Cleanup()
        {
            await CleanRemovedFoldersInternal();
        }

        //private void NavigatorServiceOnOnNavigate(object? sender, NavigateEventArgs e)
        //{
        //    if (e.CurrentUrl == "settings")
        //    {
        //        var changes = _settingsPage.ApplySettings();

        //        _ = ExecuteFolderChanges(changes);
        //    }
        //}


        private void OnCurrentImageOpen(ImageViewModel obj)
        {
            //if (obj == null) return;
            var p = obj.Path;

            ServiceLocator.FileService.UpdateViewed(obj.Id);

            if (_settings.UseBuiltInViewer.GetValueOrDefault(true))
            {
                PopoutPreview(false, true, _settings.OpenInFullScreen.GetValueOrDefault(true));
            }
            else if (_settings.UseSystemDefault.GetValueOrDefault(false))
            {
                var processInfo = new ProcessStartInfo()
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{p}\"",
                    UseShellExecute = true
                };

                try
                {
                    Process.Start(processInfo);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }

            }
            else if (_settings.UseCustomViewer.GetValueOrDefault(false))
            {
                var args = _settings.CustomCommandLineArgs;

                if (string.IsNullOrWhiteSpace(args))
                {
                    args = "%1";
                }

                if (string.IsNullOrEmpty(_settings.CustomCommandLine))
                {
                    MessageBox.Show(this, "No custom viewer set. Please check Settings", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }


                if (!File.Exists(_settings.CustomCommandLine))
                {
                    MessageBox.Show(this, "The specified application does not exist", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var processInfo = new ProcessStartInfo()
                {
                    FileName = _settings.CustomCommandLine,
                    Arguments = args.Replace("%1", $"\"{p}\""),
                    UseShellExecute = true
                };

                try
                {
                    Process.Start(processInfo);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message + "\r\n\r\nPlease check that the path to your custom viewer is valid and that the arguments are correct", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }

            }


            //PopoutPreview(false, true);

            //Process.Start("explorer.exe", $"/select,\"{p}\"");

        }

        private async void ShowSettings(object obj)
        {
            _navigatorService.Goto("settings");
        }


        private void UpdateTheme(string theme)
        {
            ThemeManager.ChangeTheme(theme);
        }


        private void OnNavigate(object sender, NavigateEventArgs args)
        {
            _model.Page = args.TargetPage;
        }


        private ICollection<Model> _modelsCollection;
        private Prompts _prompts;

        private void LoadSmartAlbums()
        {
            _search.LoadSmartAlbums();
        }

        private void LoadModels()
        {
            if (!string.IsNullOrEmpty(_settings.ModelRootPath) && Directory.Exists(_settings.ModelRootPath))
            {
                _modelsCollection = ModelScanner.Scan(_settings.ModelRootPath).ToList();
            }
            else
            {
                _modelsCollection = new List<Model>();
            }

            if (!string.IsNullOrEmpty(_settings.HashCache))
            {
                try
                {
                    string text = File.ReadAllText(_settings.HashCache);

                    // Fix unquoted "NaN" strings, which sometimes show up in SafeTensor metadata.
                    text = text.Replace("NaN", "null");

                    var hashes = JsonSerializer.Deserialize<Hashes>(text);
                    var modelLookup = _modelsCollection.ToDictionary(m => m.Path);

                    var index = "checkpoint/".Length;

                    foreach (var hash in hashes.hashes)
                    {
                        if (index < hash.Key.Length)
                        {
                            var path = hash.Key.Substring(index);

                            if (modelLookup.TryGetValue(path, out var model))
                            {
                                model.SHA256 = hash.Value.sha256;
                            }
                            else
                            {
                                _modelsCollection.Add(new Model()
                                {
                                    Filename = Path.GetFileNameWithoutExtension(path),
                                    Path = path,
                                    SHA256 = hash.Value.sha256,
                                    IsLocal = true
                                });

                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    MessageBox.Show($"Error loading JSON file '{_settings.HashCache}':\n{e.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                //foreach (var model in _modelsCollection.ToList())
                //{
                //    if (hashes.hashes.TryGetValue("checkpoint/" + model.Path, out var hash))
                //    {
                //        model.SHA256 = hash.sha256;
                //    }
                //}
            }

            var otherModels = new List<Model>();

            if (File.Exists("models.json"))
            {
                var json = File.ReadAllText(Path.Combine(AppDir, "models.json"));

                var options = new JsonSerializerOptions()
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Converters = { new JsonStringEnumConverter() }
                };

                try
                {
                    var civitAiModels = JsonSerializer.Deserialize<LiteModelCollection>(json, options);

                    foreach (var model in civitAiModels.Models)
                    {
                        foreach (var modelVersion in model.ModelVersions)
                        {
                            foreach (var versionFile in modelVersion.Files)
                            {
                                otherModels.Add(new Model()
                                {
                                    Filename = Path.GetFileNameWithoutExtension(versionFile.Name),
                                    Hash = versionFile.Hashes.AutoV1,
                                    SHA256 = versionFile.Hashes.SHA256,
                                });
                            }
                        }

                    }

                }
                catch (Exception ex)
                {
                    Logger.Log(ex.Message);
                }


            }

            _allModels = _modelsCollection.Concat(otherModels).ToList();



            _search.SetModels(_allModels);
            _models.SetModels(_modelsCollection);

            QueryBuilder.SetModels(_allModels);
        }

        private ICollection<Model> _allModels = new List<Model>();

        private async Task TryScanFolders()
        {
            if (ServiceLocator.FolderService.HasRootFolders)
            {
                if (await ServiceLocator.MessageService.Show("Do you want to scan your folders now?", "Setup", PopupButtons.YesNo) == PopupResult.Yes)
                {
                    await Scan();
                }
            }
            else
            {
                await ServiceLocator.MessageService.ShowMedium("You have not setup any image folders.\r\n\r\nAdd one or more folders first, then click the Scan Folders for new images icon in the toolbar.", "Setup", PopupButtons.OK);
            }
        }

        private string? _selectedAlbumForDownload = null;
        private string _downloadStartTime = "";

        private string GetCivitaiScriptsPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Diffusion.PyScripts", "Civitai Collections Scraper");
        }

        private string GetCivitaiDatabasePath()
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DiffusionToolkit",
                "Civitai"
            );
            Directory.CreateDirectory(appDataPath); // Ensure directory exists
            return Path.Combine(appDataPath, "civitai_state.db");
        }

        private async void LaunchCivitaiScraper()
        {
            try
            {
                Logger.Log("==========================================");
                Logger.Log("LaunchCivitaiScraper: STARTING");
                Logger.Log("==========================================");

                // BEFORE launching: Check if we need to prompt for album selection
                _selectedAlbumForDownload = null;
                Logger.Log("LaunchCivitaiScraper: Reset _selectedAlbumForDownload to null");

                // Check if settings are initialized
                if (_settings == null)
                {
                    Logger.Log("LaunchCivitaiScraper: ERROR - Settings is null!");
                    MessageBox.Show(this, "Settings not initialized", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                Logger.Log($"LaunchCivitaiScraper: Settings - AlwaysPrompt={_settings.CivitaiAlwaysPromptForAlbum}, DefaultAlbum={_settings.CivitaiDefaultAlbum}");

                bool needsPrompt = _settings.CivitaiAlwaysPromptForAlbum ||
                                  string.IsNullOrEmpty(_settings.CivitaiDefaultAlbum) ||
                                  _settings.CivitaiDefaultAlbum == "None";

                Logger.Log($"LaunchCivitaiScraper: needsPrompt = {needsPrompt}");

                if (needsPrompt)
                {
                    Logger.Log("LaunchCivitaiScraper: Need to show album selection dialog");

                    try
                    {
                        // Show album selection dialog BEFORE downloading
                        // Ensure albums are loaded
                        if (_model?.Albums == null || _model.Albums.Count == 0)
                        {
                            Logger.Log("LaunchCivitaiScraper: Loading albums");
                            LoadAlbums();
                        }

                        Logger.Log($"LaunchCivitaiScraper: Albums count: {_model?.Albums?.Count ?? 0}");

                        var albumsToPass = _model?.Albums ?? new ObservableCollection<AlbumModel>();
                        Logger.Log($"LaunchCivitaiScraper: Creating dialog with {albumsToPass.Count} albums");

                        var window = new CivitaiAlbumSelectionWindow(albumsToPass);
                        window.Owner = this;

                        Logger.Log("LaunchCivitaiScraper: Showing dialog");
                        var result = window.ShowDialog();

                        if (result != true)
                        {
                            Logger.Log("LaunchCivitaiScraper: User cancelled");
                            // User cancelled, don't launch script
                            return;
                        }

                        _selectedAlbumForDownload = window.SelectedAlbumName;
                        Logger.Log($"LaunchCivitaiScraper: Selected album: {_selectedAlbumForDownload}");

                        // Save as default if user checked "Remember"
                        if (window.RememberChoice)
                        {
                            _settings.CivitaiDefaultAlbum = _selectedAlbumForDownload ?? "None";
                            Logger.Log("LaunchCivitaiScraper: Saved as default");
                        }
                    }
                    catch (Exception dialogEx)
                    {
                        Logger.Log($"LaunchCivitaiScraper: ERROR showing dialog - {dialogEx.Message}");
                        Logger.Log($"LaunchCivitaiScraper: Stack trace: {dialogEx.StackTrace}");
                        MessageBox.Show(this, $"Error showing album selection dialog:\n\n{dialogEx.Message}\n\nSee log for details.", "Dialog Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }
                else
                {
                    // Use the default album setting
                    _selectedAlbumForDownload = _settings.CivitaiDefaultAlbum;
                    Logger.Log($"LaunchCivitaiScraper: Using default album: {_selectedAlbumForDownload}");
                }

                // Now launch Python script directly
                // Get internal script path
                var scriptsBasePath = GetCivitaiScriptsPath();
                Logger.Log($"LaunchCivitaiScraper: Scripts base path: {scriptsBasePath}");

                if (!Directory.Exists(scriptsBasePath))
                {
                    MessageBox.Show(this, $"Civitai Collections Scraper scripts not found at:\n{scriptsBasePath}\n\nPlease ensure the Diffusion.PyScripts folder is present in the application directory.", "Scripts Missing", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Find Python executable in virtual environment
                var pythonPath = Path.Combine(scriptsBasePath, ".venv", "Scripts", "python.exe");

                if (!File.Exists(pythonPath))
                {
                    MessageBox.Show(this, $"Python executable not found at: {pythonPath}\n\nPlease ensure the virtual environment is set up correctly.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var mainPyPath = Path.Combine(scriptsBasePath, "main.py");

                if (!File.Exists(mainPyPath))
                {
                    MessageBox.Show(this, $"main.py not found at: {mainPyPath}\n\nPlease verify the Civitai Collections Scraper installation.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Get database path and handle migration from old location
                var civitaiDbPath = GetCivitaiDatabasePath();

                // Migrate database from old location if it exists
                if (!string.IsNullOrEmpty(_settings?.CivitaiScraperRepositoryPath))
                {
                    var oldDbPath = Path.Combine(_settings.CivitaiScraperRepositoryPath, "civitai_state.db");
                    if (File.Exists(oldDbPath) && !File.Exists(civitaiDbPath))
                    {
                        Logger.Log($"LaunchCivitaiScraper: Migrating database from {oldDbPath} to {civitaiDbPath}");
                        try
                        {
                            File.Copy(oldDbPath, civitaiDbPath);
                            Logger.Log("LaunchCivitaiScraper: Database migration successful");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"LaunchCivitaiScraper: Database migration failed - {ex.Message}");
                        }
                    }
                }

                // Record the current time before starting download
                // Format: "YYYY-MM-DD HH:MM:SS" to match civitai_state.db created_at column
                _downloadStartTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                Logger.Log($"LaunchCivitaiScraper: Recording download start time = {_downloadStartTime}");

                Logger.Log($"LaunchCivitaiScraper: Python path: {pythonPath}");
                Logger.Log($"LaunchCivitaiScraper: main.py path: {mainPyPath}");
                Logger.Log($"LaunchCivitaiScraper: Database path: {civitaiDbPath}");
                Logger.Log($"LaunchCivitaiScraper: Working directory: {scriptsBasePath}");

                // Record database modification time before launching (for validation)
                DateTime dbModifiedBefore = File.Exists(civitaiDbPath) ? File.GetLastWriteTime(civitaiDbPath) : DateTime.MinValue;
                Logger.Log($"LaunchCivitaiScraper: Database last modified before: {dbModifiedBefore}");

                // Launch Python process directly with database path override
                var processInfo = new ProcessStartInfo()
                {
                    FileName = pythonPath,
                    Arguments = $"main.py --db-path \"{civitaiDbPath}\" --max-pages {_settings.CivitaiMaxPagesPerCollection} sync",
                    WorkingDirectory = scriptsBasePath,
                    UseShellExecute = true,  // Show console window
                    CreateNoWindow = false
                };

                Logger.Log("LaunchCivitaiScraper: Starting Python process...");
                var process = Process.Start(processInfo);

                if (process == null)
                {
                    Logger.Log("LaunchCivitaiScraper: ERROR - Failed to start Python process");
                    MessageBox.Show(this, "Failed to start Civitai scraper process.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                Logger.Log($"LaunchCivitaiScraper: Process started with PID {process.Id}");

                // Wait for process to complete in background
                await Task.Run(() =>
                {
                    Logger.Log("LaunchCivitaiScraper: Waiting for process to complete...");
                    process.WaitForExit();
                    Logger.Log($"LaunchCivitaiScraper: Process exited with code {process.ExitCode}");
                });

                // Check for suspected auth failure (exit code 2 = all collections returned 0 images from API)
                if (process.ExitCode == 2)
                {
                    Logger.Log("LaunchCivitaiScraper: WARNING - No images found in any collection (exit code 2) - cookies likely expired");
                    var cookiePath = System.IO.Path.Combine(scriptsBasePath, "civitai.com_cookies.txt");
                    MessageBox.Show(this,
                        "No images were found in any collection.\n\n" +
                        "This almost always means your CivitAI session cookies have expired.\n\n" +
                        "To fix this:\n" +
                        "1. Open Chrome and log into civitai.com\n" +
                        "2. Install the \"Get cookies.txt LOCALLY\" extension\n" +
                        "3. Export cookies for civitai.com in Netscape format\n" +
                        $"4. Save as:\n   {cookiePath}\n\n" +
                        "Then try again.",
                        "CivitAI: No Images Found (Cookies Expired?)",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // Validate database was updated by Python script
                Logger.Log("LaunchCivitaiScraper: Validating database was updated...");
                if (File.Exists(civitaiDbPath))
                {
                    DateTime dbModifiedAfter = File.GetLastWriteTime(civitaiDbPath);
                    Logger.Log($"LaunchCivitaiScraper: Database last modified after: {dbModifiedAfter}");

                    if (dbModifiedAfter <= dbModifiedBefore)
                    {
                        Logger.Log("LaunchCivitaiScraper: WARNING - Database was NOT updated by Python script!");
                        Logger.Log($"LaunchCivitaiScraper: Before: {dbModifiedBefore}, After: {dbModifiedAfter}");
                    }
                    else
                    {
                        Logger.Log($"LaunchCivitaiScraper: SUCCESS - Database was updated (modified {(dbModifiedAfter - dbModifiedBefore).TotalSeconds:F1} seconds ago)");
                    }
                }
                else
                {
                    Logger.Log($"LaunchCivitaiScraper: ERROR - Database does not exist at {civitaiDbPath}");
                }

                // Call post-completion handler
                Logger.Log("LaunchCivitaiScraper: Process completed, calling OnCivitaiScraperCompleted...");
                await OnCivitaiScraperCompleted();
            }
            catch (OperationCanceledException)
            {
                // User cancelled the operation, just return
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error launching scraper: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task OnCivitaiScraperCompleted()
        {
            try
            {
                Logger.Log("======================================");
                Logger.Log("OnCivitaiScraperCompleted: STARTING");
                Logger.Log($"OnCivitaiScraperCompleted: Selected album = '{_selectedAlbumForDownload}'");
                Logger.Log($"OnCivitaiScraperCompleted: Download start time = {_downloadStartTime}");
                Logger.Log("======================================");

                // Auto-refresh by scanning for new images
                Logger.Log("OnCivitaiScraperCompleted: Starting scan for new images");
                if (await ServiceLocator.ProgressService.TryStartTask())
                {
                    try
                    {
                        ServiceLocator.ProgressService.SetStatus("Scanning for new images...");
                        await ServiceLocator.ScanningService.ScanWatchedFolders(false, false, ServiceLocator.ProgressService.CancellationToken);
                        Logger.Log("OnCivitaiScraperCompleted: Scan completed successfully");

                        // Refresh the search to show new images
                        Dispatcher.Invoke(() =>
                        {
                            _search?.SearchImages(null);
                        });
                        Logger.Log("OnCivitaiScraperCompleted: Search refreshed");
                    }
                    finally
                    {
                        ServiceLocator.ProgressService.CompleteTask();
                    }
                }
                else
                {
                    Logger.Log("OnCivitaiScraperCompleted: Could not start progress task for scanning");
                }

                // Wait 10 seconds to give the database time to complete all writes
                Logger.Log("OnCivitaiScraperCompleted: Waiting 10 seconds for database to finalize writes...");
                await Task.Delay(10000);
                Logger.Log("OnCivitaiScraperCompleted: Wait completed, proceeding with album assignment");

                // Assign downloaded images to the selected album
                Logger.Log($"OnCivitaiScraperCompleted: Checking if should assign to album...");
                Logger.Log($"OnCivitaiScraperCompleted: _selectedAlbumForDownload IsNullOrEmpty = {string.IsNullOrEmpty(_selectedAlbumForDownload)}");
                Logger.Log($"OnCivitaiScraperCompleted: _selectedAlbumForDownload == 'None' = {_selectedAlbumForDownload == "None"}");

                if (!string.IsNullOrEmpty(_selectedAlbumForDownload) && _selectedAlbumForDownload != "None")
                {
                    Logger.Log($"OnCivitaiScraperCompleted: YES - Calling AssignDownloadedImagesToAlbum with album '{_selectedAlbumForDownload}'");
                    await AssignDownloadedImagesToAlbum(_selectedAlbumForDownload);
                    Logger.Log("OnCivitaiScraperCompleted: AssignDownloadedImagesToAlbum completed");
                }
                else
                {
                    Logger.Log("OnCivitaiScraperCompleted: NO - Skipping album assignment");
                }

                // Show notification that download is complete
                string message = "Download complete! New images have been scanned.";

                if (!string.IsNullOrEmpty(_selectedAlbumForDownload) && _selectedAlbumForDownload != "None")
                {
                    message = $"Download complete! New images have been scanned and assigned to album '{_selectedAlbumForDownload}'.";
                }

                Logger.Log($"OnCivitaiScraperCompleted: Showing toast: {message}");
                ServiceLocator.ToastService.Toast(message, "Civitai Scraper", 5);
                Logger.Log("OnCivitaiScraperCompleted: COMPLETED SUCCESSFULLY");
            }
            catch (Exception ex)
            {
                Logger.Log($"OnCivitaiScraperCompleted: ERROR - {ex.Message}");
                Logger.Log($"OnCivitaiScraperCompleted: Stack trace - {ex.StackTrace}");
                await ServiceLocator.MessageService.Show($"Error processing downloaded images: {ex.Message}", "Error", PopupButtons.OK);
            }
        }

        private async Task AssignDownloadedImagesToAlbum(string albumName)
        {
            try
            {
                Logger.Log($"AssignDownloadedImagesToAlbum: Starting for album '{albumName}'");

                // Get the album from the database
                var album = ServiceLocator.DataStore.GetAlbumByName(albumName);
                if (album == null)
                {
                    Logger.Log($"AssignDownloadedImagesToAlbum: Album '{albumName}' not found");
                    return;
                }

                Logger.Log($"AssignDownloadedImagesToAlbum: Found album with ID {album.Id}");

                // Query the Civitai scraper's database for recently downloaded files
                var civitaiDbPath = GetCivitaiDatabasePath();
                Logger.Log($"AssignDownloadedImagesToAlbum: Civitai database path = '{civitaiDbPath}'");
                Logger.Log($"AssignDownloadedImagesToAlbum: Database exists = {File.Exists(civitaiDbPath)}");

                if (!File.Exists(civitaiDbPath))
                {
                    Logger.Log($"AssignDownloadedImagesToAlbum: ERROR - Civitai database not found at {civitaiDbPath}");
                    return;
                }

                Logger.Log($"AssignDownloadedImagesToAlbum: Opening Civitai database and querying downloads...");
                Logger.Log($"AssignDownloadedImagesToAlbum: Looking for downloads with created_at >= '{_downloadStartTime}'");

                var downloadedPaths = new List<string>();
                var pathToCollection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var pathToUsername = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // Query for files created at or after the download start time
                using (var connection = new SQLite.SQLiteConnection(civitaiDbPath, SQLite.SQLiteOpenFlags.ReadOnly))
                {
                    var query = "SELECT local_path, collection_name, username FROM downloads WHERE status = 'completed' AND updated_at > ?";
                    Logger.Log($"AssignDownloadedImagesToAlbum: SQL Query = {query}");
                    Logger.Log($"AssignDownloadedImagesToAlbum: Query parameter (download start time) = '{_downloadStartTime}");

                    var results = connection.Query<CivitaiDownloadRecord>(query, _downloadStartTime);
                    Logger.Log($"AssignDownloadedImagesToAlbum: Query returned {results.Count} results");

                    foreach (var record in results)
                    {
                        if (!string.IsNullOrEmpty(record.local_path))
                        {
                            Logger.Log($"AssignDownloadedImagesToAlbum:   - Found path: {record.local_path} (collection: {record.collection_name})");
                            downloadedPaths.Add(record.local_path);
                            if (!string.IsNullOrEmpty(record.collection_name))
                                pathToCollection[record.local_path] = record.collection_name;
                            if (!string.IsNullOrEmpty(record.username))
                                pathToUsername[record.local_path] = record.username;
                        }
                        else
                        {
                            Logger.Log($"AssignDownloadedImagesToAlbum:   - Skipped record with empty path");
                        }
                    }
                }

                Logger.Log($"AssignDownloadedImagesToAlbum: Total downloaded files to process = {downloadedPaths.Count}");

                if (downloadedPaths.Count == 0)
                {
                    Logger.Log("AssignDownloadedImagesToAlbum: WARNING - No new downloads to assign (query returned 0 results)");
                    return;
                }

                // Get image IDs from the Diffusion Toolkit database
                Logger.Log($"AssignDownloadedImagesToAlbum: ========================================");
                Logger.Log($"AssignDownloadedImagesToAlbum: Querying Diffusion Toolkit database for image IDs...");
                Logger.Log($"AssignDownloadedImagesToAlbum: Looking for {downloadedPaths.Count} paths in Image table");
                Logger.Log($"AssignDownloadedImagesToAlbum: Paths being searched:");
                for (int i = 0; i < Math.Min(10, downloadedPaths.Count); i++)
                {
                    Logger.Log($"AssignDownloadedImagesToAlbum:   [{i + 1}] {downloadedPaths[i]}");
                }
                if (downloadedPaths.Count > 10)
                {
                    Logger.Log($"AssignDownloadedImagesToAlbum:   ... and {downloadedPaths.Count - 10} more paths");
                }

                var imageIds = ServiceLocator.DataStore.GetImageIdsByPaths(downloadedPaths).ToList();

                Logger.Log($"AssignDownloadedImagesToAlbum: Query completed - Found {imageIds.Count} matching image IDs");

                // Enhanced logging: Show full row data for each path
                Logger.Log($"AssignDownloadedImagesToAlbum: Detailed row information from Image table:");
                for (int i = 0; i < Math.Min(10, downloadedPaths.Count); i++)
                {
                    var path = downloadedPaths[i];
                    Logger.Log($"AssignDownloadedImagesToAlbum: --- Path [{i + 1}]: {path}");

                    // Query for the full row
                    try
                    {
                        var imageRow = ServiceLocator.DataStore.GetImageByPath(path);
                        if (imageRow != null)
                        {
                            Logger.Log($"AssignDownloadedImagesToAlbum:     ✓ FOUND - Id={imageRow.Id}, FileName={imageRow.FileName}, CreatedDate={imageRow.CreatedDate}");
                        }
                        else
                        {
                            Logger.Log($"AssignDownloadedImagesToAlbum:     ✗ NOT FOUND in Image table");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"AssignDownloadedImagesToAlbum:     ✗ ERROR querying for this path: {ex.Message}");
                    }
                }
                if (downloadedPaths.Count > 10)
                {
                    Logger.Log($"AssignDownloadedImagesToAlbum:   ... and {downloadedPaths.Count - 10} more paths (not shown in detail)");
                }

                Logger.Log($"AssignDownloadedImagesToAlbum: Image IDs returned from batch query:");
                for (int i = 0; i < Math.Min(10, imageIds.Count); i++)
                {
                    Logger.Log($"AssignDownloadedImagesToAlbum:   [{i + 1}] Image ID = {imageIds[i]}");
                }
                if (imageIds.Count > 10)
                {
                    Logger.Log($"AssignDownloadedImagesToAlbum:   ... and {imageIds.Count - 10} more IDs");
                }
                Logger.Log($"AssignDownloadedImagesToAlbum: ========================================");

                if (imageIds.Count == 0)
                {
                    Logger.Log("AssignDownloadedImagesToAlbum: WARNING - No matching images found in database");
                    Logger.Log("AssignDownloadedImagesToAlbum: This usually means:");
                    Logger.Log("AssignDownloadedImagesToAlbum:   1. The paths in Civitai database don't match paths in Diffusion Toolkit Image table");
                    Logger.Log("AssignDownloadedImagesToAlbum:   2. The scan didn't pick up the new images yet");
                    Logger.Log("AssignDownloadedImagesToAlbum:   3. The download folder is not in Diffusion Toolkit's scan paths");
                    Logger.Log($"AssignDownloadedImagesToAlbum: Sample paths from Civitai database:");
                    for (int i = 0; i < Math.Min(5, downloadedPaths.Count); i++)
                    {
                        Logger.Log($"AssignDownloadedImagesToAlbum:   {i + 1}. {downloadedPaths[i]}");
                    }
                    return;
                }

                // Log matched image IDs
                Logger.Log($"AssignDownloadedImagesToAlbum: Matched image IDs: {string.Join(", ", imageIds)}");

                // Add images to the album
                Logger.Log($"AssignDownloadedImagesToAlbum: Adding {imageIds.Count} images to album ID {album.Id}...");
                var success = ServiceLocator.DataStore.AddImagesToAlbum(album.Id, imageIds);

                if (success)
                {
                    Logger.Log($"AssignDownloadedImagesToAlbum: SUCCESS - Assigned {imageIds.Count} images to album '{albumName}'");
                }
                else
                {
                    Logger.Log($"AssignDownloadedImagesToAlbum: ERROR - Failed to assign images to album '{albumName}'");
                }

                // Tag each image with COLL_{collection_name}
                Logger.Log("AssignDownloadedImagesToAlbum: Applying collection tags...");

                // Build path→imageId map (imageIds list is parallel to downloadedPaths)
                var pathToImageId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < Math.Min(downloadedPaths.Count, imageIds.Count); i++)
                {
                    if (imageIds[i] > 0)
                        pathToImageId[downloadedPaths[i]] = imageIds[i];
                }

                // Group image IDs by collection name
                var collectionToImageIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in pathToCollection)
                {
                    if (pathToImageId.TryGetValue(kvp.Key, out var imgId))
                    {
                        if (!collectionToImageIds.ContainsKey(kvp.Value))
                            collectionToImageIds[kvp.Value] = new List<int>();
                        collectionToImageIds[kvp.Value].Add(imgId);
                    }
                }

                // Apply one tag per collection
                foreach (var kvp in collectionToImageIds)
                {
                    var tagName = $"COLL_{kvp.Key}";
                    var tagId = ServiceLocator.DataStore.GetOrCreateTag(tagName);
                    if (tagId > 0)
                    {
                        ServiceLocator.DataStore.AddImagesTag(kvp.Value, tagId);
                        Logger.Log($"AssignDownloadedImagesToAlbum: Tagged {kvp.Value.Count} images with '{tagName}' (tagId={tagId})");
                    }
                }

                // Tag each image with zz_AUTHOR_{username}
                Logger.Log("AssignDownloadedImagesToAlbum: Applying author tags...");

                var authorToImageIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in pathToUsername)
                {
                    if (pathToImageId.TryGetValue(kvp.Key, out var imgId))
                    {
                        if (!authorToImageIds.ContainsKey(kvp.Value))
                            authorToImageIds[kvp.Value] = new List<int>();
                        authorToImageIds[kvp.Value].Add(imgId);
                    }
                }

                foreach (var kvp in authorToImageIds)
                {
                    var tagName = $"zz_AUTHOR_{kvp.Key}";
                    var tagId = ServiceLocator.DataStore.GetOrCreateTag(tagName);
                    if (tagId > 0)
                    {
                        ServiceLocator.DataStore.AddImagesTag(kvp.Value, tagId);
                        Logger.Log($"AssignDownloadedImagesToAlbum: Tagged {kvp.Value.Count} images with '{tagName}' (tagId={tagId})");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"AssignDownloadedImagesToAlbum: ERROR - {ex.Message}");
                Logger.Log($"AssignDownloadedImagesToAlbum: Stack trace: {ex.StackTrace}");
            }
        }

        // Helper class for querying Civitai database
        private class CivitaiDownloadRecord
        {
            public string? local_path { get; set; }
            public string? collection_name { get; set; }
            public string? username { get; set; }
        }

        private async Task BackfillAuthorTags()
        {
            try
            {
                var civitaiDbPath = GetCivitaiDatabasePath();

                if (!File.Exists(civitaiDbPath))
                {
                    await ServiceLocator.MessageService.Show(
                        "CivitAI state database not found.\n\nRun the scraper at least once first.",
                        "Backfill Author Tags", PopupButtons.OK);
                    return;
                }

                Logger.Log("BackfillAuthorTags: Starting...");

                // Query ALL completed downloads with a username from the CivitAI state DB
                var pathToUsername = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                using (var connection = new SQLite.SQLiteConnection(civitaiDbPath, SQLite.SQLiteOpenFlags.ReadOnly))
                {
                    var query = "SELECT local_path, username FROM downloads WHERE status = 'completed' AND username IS NOT NULL AND username != ''";
                    var results = connection.Query<CivitaiDownloadRecord>(query);

                    foreach (var record in results)
                    {
                        if (!string.IsNullOrEmpty(record.local_path) && !string.IsNullOrEmpty(record.username))
                            pathToUsername[record.local_path] = record.username;
                    }
                }

                Logger.Log($"BackfillAuthorTags: Found {pathToUsername.Count} downloads with usernames");

                if (pathToUsername.Count == 0)
                {
                    await ServiceLocator.MessageService.Show(
                        "No downloads with author usernames found.\n\nRun the backfill_usernames.py script first to populate usernames.",
                        "Backfill Author Tags", PopupButtons.OK);
                    return;
                }

                // Resolve paths to image IDs in the Diffusion Toolkit database
                var allPaths = pathToUsername.Keys.ToList();
                var imageIds = ServiceLocator.DataStore.GetImageIdsByPaths(allPaths).ToList();

                // Build path→imageId map (parallel lists)
                var pathToImageId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < Math.Min(allPaths.Count, imageIds.Count); i++)
                {
                    if (imageIds[i] > 0)
                        pathToImageId[allPaths[i]] = imageIds[i];
                }

                Logger.Log($"BackfillAuthorTags: Matched {pathToImageId.Count} images in database");

                if (pathToImageId.Count == 0)
                {
                    await ServiceLocator.MessageService.Show(
                        "No matching images found in the Diffusion Toolkit database.\n\nMake sure the download folder has been scanned.",
                        "Backfill Author Tags", PopupButtons.OK);
                    return;
                }

                // Group image IDs by username
                var authorToImageIds = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in pathToUsername)
                {
                    if (pathToImageId.TryGetValue(kvp.Key, out var imgId))
                    {
                        if (!authorToImageIds.ContainsKey(kvp.Value))
                            authorToImageIds[kvp.Value] = new List<int>();
                        authorToImageIds[kvp.Value].Add(imgId);
                    }
                }

                Logger.Log($"BackfillAuthorTags: Found {authorToImageIds.Count} distinct authors");

                // Apply zz_AUTHOR_ tags
                int taggedCount = 0;
                foreach (var kvp in authorToImageIds)
                {
                    var tagName = $"zz_AUTHOR_{kvp.Key}";
                    var tagId = ServiceLocator.DataStore.GetOrCreateTag(tagName);
                    if (tagId > 0)
                    {
                        ServiceLocator.DataStore.AddImagesTag(kvp.Value, tagId);
                        taggedCount += kvp.Value.Count;
                        Logger.Log($"BackfillAuthorTags: Tagged {kvp.Value.Count} images with '{tagName}'");
                    }
                }

                Logger.Log($"BackfillAuthorTags: DONE - Tagged {taggedCount} images across {authorToImageIds.Count} authors");

                await ServiceLocator.MessageService.Show(
                    $"Author tags applied!\n\n" +
                    $"Authors: {authorToImageIds.Count}\n" +
                    $"Images tagged: {taggedCount}",
                    "Backfill Author Tags", PopupButtons.OK);
            }
            catch (Exception ex)
            {
                Logger.Log($"BackfillAuthorTags: ERROR - {ex.Message}");
                Logger.Log($"BackfillAuthorTags: Stack trace: {ex.StackTrace}");
                await ServiceLocator.MessageService.Show(
                    $"Error: {ex.Message}",
                    "Backfill Author Tags", PopupButtons.OK);
            }
        }

        private async Task LaunchCivitaiPipeline()
        {
            try
            {
                var result = await ServiceLocator.MessageService.Show(
                    "This will run the Civitai Collections Pipeline.\n\n" +
                    "Diffusion Toolkit will close immediately and the pipeline will run.\n" +
                    "The application will automatically reopen when the pipeline finishes.\n\n" +
                    "Do you want to continue?",
                    "Launch Civitai Pipeline",
                    PopupButtons.YesNo);

                if (result == PopupResult.Yes)
                {
                    // Check if pipeline repository path is configured
                    if (string.IsNullOrEmpty(_settings?.CivitaiPipelineRepositoryPath))
                    {
                        await ServiceLocator.MessageService.Show("Civitai Pipeline repository path is not configured.\n\nPlease set it in Settings.", "Configuration Required", PopupButtons.OK);
                        return;
                    }

                    // Find Python executable in virtual environment
                    var pythonPath = Path.Combine(_settings.CivitaiPipelineRepositoryPath, ".venv1", "Scripts", "python.exe");

                    if (!File.Exists(pythonPath))
                    {
                        await ServiceLocator.MessageService.Show($"Python executable not found at: {pythonPath}\n\nPlease ensure the virtual environment is set up correctly.", "Error", PopupButtons.OK);
                        return;
                    }

                    var pipelineScript = Path.Combine(_settings.CivitaiPipelineRepositoryPath, "run_pipeline.py");

                    if (!File.Exists(pipelineScript))
                    {
                        await ServiceLocator.MessageService.Show($"run_pipeline.py not found at: {pipelineScript}\n\nPlease verify the pipeline repository path in Settings.", "Error", PopupButtons.OK);
                        return;
                    }

                    Logger.Log("LaunchCivitaiPipeline: Starting pipeline...");
                    Logger.Log($"LaunchCivitaiPipeline: Python path: {pythonPath}");
                    Logger.Log($"LaunchCivitaiPipeline: Pipeline script: {pipelineScript}");

                    // Launch Python process with admin rights
                    var processInfo = new ProcessStartInfo()
                    {
                        FileName = pythonPath,
                        Arguments = "run_pipeline.py",
                        WorkingDirectory = _settings.CivitaiPipelineRepositoryPath,
                        UseShellExecute = true,
                        Verb = "runas" // Run as administrator
                    };

                    Process.Start(processInfo);
                    Logger.Log("LaunchCivitaiPipeline: Pipeline process started, closing application");

                    // Close the application immediately so pipeline can run
                    Application.Current.Shutdown();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"LaunchCivitaiPipeline: ERROR - {ex.Message}");
                await ServiceLocator.MessageService.Show($"Error launching pipeline: {ex.Message}", "Error", PopupButtons.OK);
            }
        }

        // ====================================================================
        // ComfyUI Integration
        // ====================================================================

        private async void LaunchComfyUI()
        {
            try
            {
                Logger.Log("==========================================");
                Logger.Log("LaunchComfyUI: STARTING");
                Logger.Log("==========================================");

                if (_model.CurrentImage == null)
                {
                    await ServiceLocator.MessageService.Show(
                        "No image is currently selected. Please select an image first.",
                        "No Image Selected",
                        PopupButtons.OK);
                    return;
                }

                var launcherPath = _settings.ComfyUILauncherPath;

                if (string.IsNullOrEmpty(launcherPath))
                {
                    await ServiceLocator.MessageService.Show(
                        "ComfyUI launcher path is not configured.\n\n" +
                        "Please set the path to your ComfyUI launcher in Settings.\n" +
                        "Examples:\n" +
                        "  • run_nvidia_gpu.bat\n" +
                        "  • python.exe (if you launch with: python main.py)\n" +
                        "  • ComfyUI.exe (portable version)",
                        "Configuration Required",
                        PopupButtons.OK);
                    Dispatcher.Invoke(() => ServiceLocator.NavigatorService.Goto("settings#comfyui"));
                    return;
                }

                if (!File.Exists(launcherPath))
                {
                    await ServiceLocator.MessageService.Show(
                        $"ComfyUI launcher not found:\n{launcherPath}\n\nPlease verify the path in Settings.",
                        "Invalid Path",
                        PopupButtons.OK);
                    return;
                }

                string imagePath = _model.CurrentImage.Path;
                Logger.Log($"LaunchComfyUI: Image Path: {imagePath}");

                // Extract workflow from image
                var workflow = await ExtractWorkflowFromImage(imagePath);

                if (workflow == null)
                {
                    Logger.Log("LaunchComfyUI: No workflow found in image");
                    await ServiceLocator.MessageService.Show(
                        "This image does not contain workflow metadata.\n\n" +
                        "ComfyUI will launch, but no workflow will be loaded.",
                        "No Workflow Found",
                        PopupButtons.OK);
                    // Continue anyway - user might want to use ComfyUI for other purposes
                }
                else if (!IsValidComfyUIWorkflow(workflow))
                {
                    Logger.Log("LaunchComfyUI: Workflow is not ComfyUI format");
                    await ServiceLocator.MessageService.Show(
                        "This image's workflow metadata is not in ComfyUI format.\n\n" +
                        "It might be from a different tool (Automatic1111, InvokeAI, etc.).\n\n" +
                        "ComfyUI will launch, but the workflow cannot be loaded.",
                        "Not a ComfyUI Workflow",
                        PopupButtons.OK);
                    // Set workflow to null so we don't try to load it
                    workflow = null;
                }

                // Prepare launch parameters
                var workingDir = Path.GetDirectoryName(launcherPath);
                string arguments = _settings.ComfyUILauncherArgs ?? "";

                // If launcher is python.exe, add main.py
                if (Path.GetFileName(launcherPath).Equals("python.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var mainPyPath = Path.Combine(workingDir, "main.py");
                    if (File.Exists(mainPyPath))
                    {
                        arguments = $"\"{mainPyPath}\" {arguments}";
                    }
                }

                Logger.Log($"LaunchComfyUI: Launcher: {launcherPath}");
                Logger.Log($"LaunchComfyUI: Arguments: {arguments}");
                Logger.Log($"LaunchComfyUI: Working Directory: {workingDir}");

                // Launch ComfyUI and wait for server
                var launched = await LaunchAndWaitForComfyUI(launcherPath, workingDir, arguments);

                if (!launched)
                {
                    await ServiceLocator.MessageService.Show(
                        "ComfyUI failed to start or took too long to respond.\n\n" +
                        "Please check that ComfyUI is properly installed and try launching it manually first.",
                        "Launch Failed",
                        PopupButtons.OK);
                    return;
                }

                // Load workflow if we extracted one
                if (workflow != null)
                {
                    var loaded = await LoadWorkflowInComfyUI(workflow);

                    if (loaded)
                    {
                        Logger.Log("LaunchComfyUI: SUCCESS - Workflow loaded!");
                        // Note: URL method opens browser automatically with workflow
                        // API method requires separate browser open
                        ServiceLocator.ToastService.Toast(
                            "Workflow loaded successfully! Check your browser.",
                            "ComfyUI",
                            5);
                    }
                    else
                    {
                        // Failed to load workflow, open ComfyUI anyway
                        Logger.Log("LaunchComfyUI: Failed to load workflow, opening ComfyUI without it");

                        Process.Start(new ProcessStartInfo
                        {
                            FileName = _settings.ComfyUIServerUrl ?? "http://localhost:8188",
                            UseShellExecute = true
                        });

                        await ServiceLocator.MessageService.Show(
                            "ComfyUI launched successfully, but failed to load the workflow.\n\n" +
                            "You can manually drag the image into ComfyUI to load it.",
                            "Workflow Load Failed",
                            PopupButtons.OK);
                    }
                }
                else
                {
                    // No workflow found, just open ComfyUI
                    Logger.Log("LaunchComfyUI: No workflow to load, opening ComfyUI");

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _settings.ComfyUIServerUrl ?? "http://localhost:8188",
                        UseShellExecute = true
                    });
                }

                Logger.Log("LaunchComfyUI: COMPLETED");
            }
            catch (Exception ex)
            {
                Logger.Log($"LaunchComfyUI: ERROR - {ex.Message}");
                await ServiceLocator.MessageService.Show(
                    $"Error launching ComfyUI:\n{ex.Message}\n\nPlease verify your ComfyUI launcher path in Settings.",
                    "Error",
                    PopupButtons.OK);
            }
        }

        private async Task<string?> ExtractWorkflowFromImage(string imagePath)
        {
            try
            {
                // Get workflow from the current image if it's stored in the database
                if (!string.IsNullOrEmpty(_model.CurrentImage?.Workflow))
                {
                    Logger.Log("LaunchComfyUI: Workflow retrieved from database");
                    return _model.CurrentImage.Workflow;
                }

                Logger.Log("LaunchComfyUI: No workflow found in database");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"ExtractWorkflowFromImage: Failed to extract workflow - {ex.Message}");
                return null;
            }
        }

        private bool IsValidComfyUIWorkflow(string workflowJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(workflowJson))
                {
                    Logger.Log("IsValidComfyUIWorkflow: Workflow is null or empty");
                    return false;
                }

                // Parse as JSON
                var workflow = JsonNode.Parse(workflowJson);

                // Check if it's a JSON object (not array or primitive)
                if (workflow is not JsonObject workflowObj)
                {
                    Logger.Log("IsValidComfyUIWorkflow: Workflow is not a JSON object");
                    return false;
                }

                // ComfyUI workflows are objects with numeric string keys
                // Each value should have "class_type" and "inputs"
                var hasValidNodes = false;
                var nodeCount = 0;

                foreach (var kvp in workflowObj)
                {
                    // Keys should be numeric strings ("1", "2", etc.)
                    if (!int.TryParse(kvp.Key, out _))
                    {
                        continue;
                    }

                    var node = kvp.Value as JsonObject;
                    if (node == null)
                    {
                        continue;
                    }

                    // Check for ComfyUI-specific fields
                    if (node.ContainsKey("class_type") && node.ContainsKey("inputs"))
                    {
                        hasValidNodes = true;
                        nodeCount++;
                    }
                }

                if (hasValidNodes)
                {
                    Logger.Log($"IsValidComfyUIWorkflow: Valid ComfyUI workflow detected ({nodeCount} nodes)");
                }
                else
                {
                    Logger.Log("IsValidComfyUIWorkflow: No valid ComfyUI nodes found");
                }

                return hasValidNodes;
            }
            catch (JsonException ex)
            {
                Logger.Log($"IsValidComfyUIWorkflow: Invalid JSON - {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"IsValidComfyUIWorkflow: Validation failed - {ex.Message}");
                return false;
            }
        }

        private async Task<bool> LaunchAndWaitForComfyUI(string launcherPath, string workingDir, string args)
        {
            try
            {
                // Check if already running
                if (await IsComfyUIRunning())
                {
                    Logger.Log("LaunchComfyUI: ComfyUI is already running");
                    // Add extra delay to ensure API is fully ready
                    Logger.Log("LaunchComfyUI: Waiting 2 seconds to ensure API is fully ready...");
                    await Task.Delay(2000);
                    return true;
                }

                Logger.Log("LaunchComfyUI: ComfyUI not detected, launching new instance...");

                // Launch ComfyUI
                var processInfo = new ProcessStartInfo()
                {
                    FileName = launcherPath,
                    Arguments = args,
                    WorkingDirectory = workingDir,
                    UseShellExecute = true,
                    CreateNoWindow = false
                };

                Process.Start(processInfo);
                Logger.Log("LaunchComfyUI: Process started, waiting for server...");

                // Wait for server to be ready
                // Use configured timeout, or default to 60 seconds (ComfyUI can be slow on first launch)
                int timeout = _settings.ComfyUIStartupTimeout;
                if (timeout < 30)
                {
                    timeout = 60; // Override if user set it too low
                    Logger.Log($"LaunchComfyUI: Timeout was {_settings.ComfyUIStartupTimeout}s, using {timeout}s for safety");
                }

                Logger.Log($"LaunchComfyUI: Will poll for up to {timeout} seconds...");

                for (int i = 0; i < timeout; i++)
                {
                    await Task.Delay(1000);

                    if (i % 5 == 0 && i > 0)
                    {
                        Logger.Log($"LaunchComfyUI: Still waiting... ({i}/{timeout} seconds)");
                    }

                    if (await IsComfyUIRunning())
                    {
                        Logger.Log($"LaunchComfyUI: Server responded after {i + 1} seconds");
                        // Add extra delay to ensure API is fully initialized
                        Logger.Log("LaunchComfyUI: Waiting 3 more seconds for API to fully initialize...");
                        await Task.Delay(3000);
                        Logger.Log("LaunchComfyUI: Server ready!");
                        return true;
                    }
                }

                Logger.Log($"LaunchComfyUI: Timeout after {timeout} seconds waiting for server");
                Logger.Log("LaunchComfyUI: ComfyUI might have started but is taking longer than expected");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"LaunchComfyUI: Failed to launch - {ex.Message}");
                Logger.Log($"LaunchComfyUI: Stack trace - {ex.StackTrace}");
                return false;
            }
        }

        private async Task<bool> IsComfyUIRunning()
        {
            try
            {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(2);
                var serverUrl = _settings.ComfyUIServerUrl ?? "http://localhost:8188";
                var response = await httpClient.GetAsync(serverUrl);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> LoadWorkflowInComfyUI(string workflowJson)
        {
            try
            {
                var serverUrl = _settings.ComfyUIServerUrl ?? "http://localhost:8188";

                // Minify workflow JSON to reduce URL length
                var minified = workflowJson
                    .Replace("\r\n", "")
                    .Replace("\n", "")
                    .Replace("  ", "")
                    .Replace("\t", "");

                Logger.Log($"LaunchComfyUI: Workflow size: {minified.Length} characters");

                // Try URL parameter method first (works for most workflows, no auto-execution)
                // URL length limit is typically 8KB for modern browsers, 2KB for older ones
                // Using 6KB as safe threshold
                if (minified.Length < 6000)
                {
                    Logger.Log("LaunchComfyUI: Using URL parameter method (workflow appears in UI, no auto-execution)");

                    var encoded = System.Uri.EscapeDataString(minified);
                    var urlWithWorkflow = $"{serverUrl}/?workflow={encoded}";

                    Logger.Log($"LaunchComfyUI: Final URL length: {urlWithWorkflow.Length} characters");

                    // Open browser with workflow URL
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = urlWithWorkflow,
                        UseShellExecute = true
                    });

                    Logger.Log("LaunchComfyUI: Workflow loaded via URL parameter");
                    return true;
                }
                else
                {
                    // Workflow too large for URL, fall back to API method
                    Logger.Log($"LaunchComfyUI: Workflow too large ({minified.Length} chars), using API method");
                    Logger.Log("LaunchComfyUI: WARNING - API method queues workflow for execution!");

                    // Wait a bit longer to ensure API is fully ready
                    Logger.Log("LaunchComfyUI: Waiting 3 seconds for API to be fully ready...");
                    await Task.Delay(3000);

                    return await LoadWorkflowViaAPI(workflowJson, serverUrl);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"LaunchComfyUI: Failed to load workflow - {ex.Message}");
                Logger.Log($"LaunchComfyUI: Stack trace - {ex.StackTrace}");
                return false;
            }
        }

        private async Task<bool> LoadWorkflowViaAPI(string workflowJson, string serverUrl)
        {
            try
            {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10);

                // ComfyUI API endpoint for queueing workflows
                // NOTE: This queues the workflow for EXECUTION, not just loading into UI
                var content = new StringContent(
                    $"{{\"prompt\": {workflowJson}}}",
                    Encoding.UTF8,
                    "application/json"
                );

                Logger.Log($"LoadWorkflowViaAPI: Posting to {serverUrl}/api/prompt");
                var response = await httpClient.PostAsync($"{serverUrl}/api/prompt", content);

                var responseText = await response.Content.ReadAsStringAsync();
                Logger.Log($"LoadWorkflowViaAPI: Response status: {response.StatusCode}");
                Logger.Log($"LoadWorkflowViaAPI: Response body: {responseText}");

                if (response.IsSuccessStatusCode)
                {
                    // Check if response contains error details
                    if (responseText.Contains("\"error\""))
                    {
                        Logger.Log("LoadWorkflowViaAPI: API returned error in response body");
                        return false;
                    }

                    Logger.Log("LoadWorkflowViaAPI: Workflow queued successfully (will auto-execute)");
                    return true;
                }
                else
                {
                    Logger.Log($"LoadWorkflowViaAPI: HTTP error - {response.StatusCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"LoadWorkflowViaAPI: Exception - {ex.Message}");
                Logger.Log($"LoadWorkflowViaAPI: Stack trace - {ex.StackTrace}");
                return false;
            }
        }



    }



    public class Hashes
    {
        public Dictionary<string, HashInfo> hashes { get; set; }
    }

    public class HashInfo
    {
        public double mtime { get; set; }
        public string sha256 { get; set; }
    }

    static class WindowExtensions
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);


        public static double ActualTop(this Window window)
        {
            switch (window.WindowState)
            {
                case WindowState.Normal:
                    return window.Top;
                case WindowState.Minimized:
                    return window.RestoreBounds.Top;
                case WindowState.Maximized:
                    {
                        RECT rect;
                        GetWindowRect((new WindowInteropHelper(window)).Handle, out rect);
                        return rect.Top;
                    }
            }
            return 0;
        }
        public static double ActualLeft(this Window window)
        {
            switch (window.WindowState)
            {
                case WindowState.Normal:
                    return window.Left;
                case WindowState.Minimized:
                    return window.RestoreBounds.Left;
                case WindowState.Maximized:
                    {
                        RECT rect;
                        GetWindowRect((new WindowInteropHelper(window)).Handle, out rect);
                        return rect.Left;
                    }
            }
            return 0;
        }
    }
}
