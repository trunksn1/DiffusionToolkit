using Diffusion.Common;
using Diffusion.Toolkit.Classes;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Themes;
using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Diffusion.Database;
using Diffusion.Toolkit.Configuration;
using Diffusion.Toolkit.Localization;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit.Pages
{

    /// <summary>
    /// Interaction logic for Settings.xaml
    /// </summary>
    public partial class Settings : NavigationPage
    {
        private SettingsModel _model = new SettingsModel();
        public Configuration.Settings _settings => ServiceLocator.Settings;

        private List<FolderChange> _folderChanges = new List<FolderChange>();

        private DataStore _dataStore => ServiceLocator.DataStore;

        private string GetLocalizedText(string key)
        {
            return (string)JsonLocalizationProvider.Instance.GetLocalizedObject(key, null, CultureInfo.InvariantCulture);
        }

        public Settings(Window window) : base("settings")
        {
            _window = window;
            InitializeComponent();

            _model.PropertyChanged += ModelOnPropertyChanged;

            InitializeSettings();
            LoadCultures();

            _model.SetPristine();

            ServiceLocator.NavigatorService.OnNavigate += (sender, args) =>
            {
                InitializeSettings();
                if (args.TargetUri.Path.ToLower() == "settings" && args.TargetUri.Fragment != null && args.TargetUri.Fragment.ToLowerInvariant() == "externalapplications")
                {
                    ExternalApplicationsTab.IsSelected = true;
                }
                else if (args.TargetUri.Path.ToLower() == "settings" && args.TargetUri.Fragment != null && args.TargetUri.Fragment.ToLowerInvariant() == "comfyui")
                {
                    TabItem.IsSelected = true; // "General" tab where ComfyUI settings are
                }
                else if (args.TargetUri.Path.ToLower() == "settings" && args.TargetUri.Fragment != null && args.TargetUri.Fragment.ToLowerInvariant() == "civitai")
                {
                    CivitaiTab.IsSelected = true;
                }
            };

            DataContext = _model;
        }

        private void InitializeSettings()
        {
            _model.CheckForUpdatesOnStartup = _settings.CheckForUpdatesOnStartup;
            _model.ScanForNewImagesOnStartup = _settings.ScanForNewImagesOnStartup;

            _model.AutoRefresh = _settings.AutoRefresh;

            _model.ModelRootPath = _settings.ModelRootPath;
            _model.FileExtensions = _settings.FileExtensions;
            _model.SoftwareOnly = _settings.RenderMode == RenderMode.SoftwareOnly;

            _model.PageSize = _settings.PageSize;
            _model.UseBuiltInViewer = _settings.UseBuiltInViewer.GetValueOrDefault(true);
            _model.OpenInFullScreen = _settings.OpenInFullScreen.GetValueOrDefault(true);
            _model.UseSystemDefault = _settings.UseSystemDefault.GetValueOrDefault(false);
            _model.UseCustomViewer = _settings.UseCustomViewer.GetValueOrDefault(false);
            _model.CustomCommandLine = _settings.CustomCommandLine;
            _model.CustomCommandLineArgs = _settings.CustomCommandLineArgs;
            _model.SlideShowDelay = _settings.SlideShowDelay;
            _model.ScrollNavigation = _settings.ScrollNavigation;
            _model.AdvanceOnTag = _settings.AutoAdvance;
            _model.ShowFilenames = _settings.ShowFilenames;
            _model.PermanentlyDelete = _settings.PermanentlyDelete;
            _model.ConfirmDeletion = _settings.ConfirmDeletion;
            _model.LoopVideo = _settings.LoopVideo;

            _model.AutoTagNSFW = _settings.AutoTagNSFW;
            _model.NSFWTags = string.Join("\r\n", _settings.NSFWTags);

            _model.HashCache = _settings.HashCache;
            _model.PortableMode = _settings.PortableMode;

            // Civitai settings
            _model.CivitaiAlwaysPromptForAlbum = _settings.CivitaiAlwaysPromptForAlbum;
            _model.CivitaiMaxPagesPerCollection = _settings.CivitaiMaxPagesPerCollection;
            _model.CivitaiApiKey = _settings.GetCivitaiApiKey();
            CivitaiApiKeyBox.Password = _model.CivitaiApiKey ?? "";
            LoadCivitaiAlbumDropdown();
            LoadCivitaiCollections();

            // Token Analyzer settings
            _model.TokenAnalyzerPath = _settings.TokenAnalyzerPath;

            // ComfyUI settings
            _model.ComfyUILauncherPath = _settings.ComfyUILauncherPath;
            _model.ComfyUILauncherArgs = _settings.ComfyUILauncherArgs;
            _model.ComfyUIServerUrl = _settings.ComfyUIServerUrl;
            _model.ComfyUIStartupTimeout = _settings.ComfyUIStartupTimeout;

            _model.StoreMetadata = _settings.StoreMetadata;
            _model.StoreWorkflow = _settings.StoreWorkflow;
            _model.ScanUnavailable = _settings.ScanUnavailable;

            _model.ExternalApplications = new ObservableCollection<ExternalApplicationModel>(_settings.ExternalApplications.Select(d => new ExternalApplicationModel()
            {
                CommandLineArgs = d.CommandLineArgs,
                Name = d.Name,
                Path = d.Path
            }));

            _model.Theme = _settings.Theme;
            _model.Culture = _settings.Culture;
            _model.SetPristine();
        }

        private void LoadCultures()
        {
            var cultures = new List<Langauge>
            {
                new ("Default", "default"),
            };

            _model.ThemeOptions = new List<OptionValue>()
            {
                new (GetLocalizedText("Settings.Themes.Theme.System"), "System"),
                new (GetLocalizedText("Settings.Themes.Theme.Light"), "Light"),
                new (GetLocalizedText("Settings.Themes.Theme.Dark"), "Dark")
            };

            try
            {
                var configPath = Path.Combine(AppInfo.AppDir, "Localization", "languages.json");

                var langs = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(configPath));

                foreach (var (name, culture) in langs)
                {
                    cultures.Add(new Langauge(name, culture));
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error loading languages.json: {ex.Message}");
            }

            _model.Cultures = new ObservableCollection<Langauge>(cultures);

        }


        private void ModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(SettingsModel.Theme):
                    ThemeManager.ChangeTheme(_model.Theme);
                    break;
                case nameof(SettingsModel.Culture):
                    _settings.Culture = _model.Culture;
                    break;
                case nameof(SettingsModel.SlideShowDelay):
                    {
                        if (_model.SlideShowDelay < 1)
                        {
                            _model.SlideShowDelay = 1;
                        }
                        if (_model.SlideShowDelay > 100)
                        {
                            _model.SlideShowDelay = 100;
                        }

                        break;
                    }
            }
        }

        private Window _window;
        
        private void BrowseModelPath_OnClick(object sender, RoutedEventArgs e)
        {
            using var dialog = new CommonOpenFileDialog();
            dialog.IsFolderPicker = true;
            if (dialog.ShowDialog(this._window) == CommonFileDialogResult.Ok)
            {
                _model.ModelRootPath = dialog.FileName;
            }
        }

        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            Regex regex = new Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        private void Open_DB_Folder(object sender, RoutedEventArgs e)
        {
            Process.Start("explorer.exe", $"/select,\"{_dataStore.DatabasePath}\"");
        }

        private void Backup_DB(object sender, RoutedEventArgs e)
        {
            _dataStore.CreateBackup();

            var result = MessageBox.Show(this._window,
                "A database backup has been created.",
                "Backup Database", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void Restore_DB(object sender, RoutedEventArgs e)
        {
            using var dialog = new CommonOpenFileDialog();
            dialog.Filters.Add(new CommonFileDialogFilter("SQLite databases", ".db"));
            dialog.DefaultDirectory = Path.GetDirectoryName(_dataStore.DatabasePath);
            dialog.Filters.Add(new CommonFileDialogFilter("All files", ".*"));
            if (dialog.ShowDialog(this._window) == CommonFileDialogResult.Ok)
            {
                if (dialog.FileName == _dataStore.DatabasePath)
                {
                    MessageBox.Show(this._window,
                    "The selected file is the current database. Please try another file.",
                    "Restore Database", MessageBoxButton.OK,
                    MessageBoxImage.Exclamation);

                    return;
                }

                var result = MessageBox.Show(this._window,
                    $"Are you sure you want to restore the file {dialog.FileName}? Your current database will be overwritten!",
                "Restore Database", MessageBoxButton.YesNo,
                    MessageBoxImage.Exclamation, MessageBoxResult.No);

                if (result == MessageBoxResult.Yes)
                {
                    if (!_dataStore.TryRestoreBackup(dialog.FileName))
                    {
                        MessageBox.Show(this._window,
                            "The database backup is not a Diffusion Toolkit database.",
                            "Restore Database", MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return;
                    }
                }

                MessageBox.Show(this._window,
                    "The database backup has been restored.",
                    "Restore Database", MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private void BrowseHashCache_OnClick(object sender, RoutedEventArgs e)
        {
            using var dialog = new CommonOpenFileDialog();
            dialog.Filters.Add(new CommonFileDialogFilter("A1111 cache", "*.json"));
            if (dialog.ShowDialog(this._window) == CommonFileDialogResult.Ok)
            {
                _model.HashCache = dialog.FileName;
            }
        }

        private void BrowseCustomViewer_OnClick(object sender, RoutedEventArgs e)
        {
            using var dialog = new CommonOpenFileDialog();
            dialog.DefaultFileName = _model.CustomCommandLine;
            dialog.Filters.Add(new CommonFileDialogFilter("Executable files", "*.exe;*.bat;*.cmd"));
            if (dialog.ShowDialog(this._window) == CommonFileDialogResult.Ok)
            {
                _model.CustomCommandLine = dialog.FileName;
            }
        }


        private void BrowseExternalApplicationPath_OnClick(object sender, RoutedEventArgs e)
        {
            //var button = (Button)sender;
            //var dc = (ExternalApplication)button.DataContext;
            var app = _model.SelectedApplication;

            using var dialog = new CommonOpenFileDialog();
            dialog.DefaultFileName = app.Path;
            dialog.Filters.Add(new CommonFileDialogFilter("Executable files", "*.exe;*.bat;*.cmd"));
            if (dialog.ShowDialog(this._window) == CommonFileDialogResult.Ok)
            {
                app.Path = dialog.FileName;
            }
        }

        private void RemoveExternalApplication_OnClick(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedApplication != null)
            {
                var result = MessageBox.Show(this._window,
                $"Are you sure you want to remove \"{_model.SelectedApplication.Name}\"?",
                "Remove Application", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No);

                if (result == MessageBoxResult.Yes)
                {
                    _model.ExternalApplications.Remove(_model.SelectedApplication);
                }
            }
        }

        private void AddExternalApplication_OnClick(object sender, RoutedEventArgs e)
        {
            var newApplication = new ExternalApplicationModel()
            {
                Name = "Application",
                Path = ""
            };
            _model.ExternalApplications.Add(newApplication);
            _model.SelectedApplication = newApplication;
        }

        private void MoveExternalApplicationUp_OnClick(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedApplication != null)
            {
                var index = _model.ExternalApplications.IndexOf(_model.SelectedApplication);

                if (index > 0)
                {
                    _model.ExternalApplications.Move(index, index - 1);
                }
            }
        }

        private void MoveExternalApplicationDown_OnClick(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedApplication != null)
            {
                var index = _model.ExternalApplications.IndexOf(_model.SelectedApplication);

                if (index < _model.ExternalApplications.Count)
                {
                    _model.ExternalApplications.Move(index, index + 1);
                }
            }
        }

        private void ApplyChanges_OnClick(object sender, RoutedEventArgs e)
        {
            ApplySettings();
            
            _model.SetPristine();
        }

        private void RevertChanges_OnClick(object sender, RoutedEventArgs e)
        {
            InitializeSettings();
        }


        public void ApplySettings()
        {
            if (_model.IsDirty)
            {
                _settings.SetPristine();

                _settings.ModelRootPath = _model.ModelRootPath;
                _settings.FileExtensions = _model.FileExtensions;
                _settings.Theme = _model.Theme;
                _settings.PageSize = _model.PageSize;
                _settings.AutoRefresh = _model.AutoRefresh;

                _settings.CheckForUpdatesOnStartup = _model.CheckForUpdatesOnStartup;
                _settings.ScanForNewImagesOnStartup = _model.ScanForNewImagesOnStartup;
                _settings.AutoTagNSFW = _model.AutoTagNSFW;
                _settings.NSFWTags = _model.NSFWTags.Split("\r\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                _settings.HashCache = _model.HashCache;
                _settings.PortableMode = _model.PortableMode;
                _settings.RenderMode = _model.SoftwareOnly ? RenderMode.SoftwareOnly : RenderMode.Default;
                
                _settings.UseBuiltInViewer = _model.UseBuiltInViewer;
                _settings.OpenInFullScreen = _model.OpenInFullScreen;
                _settings.UseSystemDefault = _model.UseSystemDefault;
                _settings.UseCustomViewer = _model.UseCustomViewer;
                _settings.CustomCommandLine = _model.CustomCommandLine;
                _settings.CustomCommandLineArgs = _model.CustomCommandLineArgs;
                _settings.SlideShowDelay = _model.SlideShowDelay;
                _settings.ScrollNavigation = _model.ScrollNavigation;
                _settings.AutoAdvance = _model.AdvanceOnTag;
                _settings.ShowFilenames = _model.ShowFilenames;
                _settings.PermanentlyDelete = _model.PermanentlyDelete;
                _settings.ConfirmDeletion = _model.ConfirmDeletion;
                _settings.LoopVideo = _model.LoopVideo;

                _settings.StoreMetadata = _model.StoreMetadata;
                _settings.StoreWorkflow = _model.StoreWorkflow;
                _settings.ScanUnavailable = _model.ScanUnavailable;
                _settings.ExternalApplications = _model.ExternalApplications.Select(d => new ExternalApplication()
                {
                    CommandLineArgs = d.CommandLineArgs,
                    Name = d.Name,
                    Path = d.Path
                }).ToList();

                _settings.Culture = _model.Culture;

                // Civitai settings
                _settings.CivitaiAlwaysPromptForAlbum = _model.CivitaiAlwaysPromptForAlbum;
                _settings.CivitaiMaxPagesPerCollection = _model.CivitaiMaxPagesPerCollection;
                _settings.SetCivitaiApiKey(_model.CivitaiApiKey);
                // CivitaiDefaultAlbum is saved when the user selects from dropdown or clicks Clear

                // Save CivitAI collections
                _settings.CivitaiCollections = _model.CivitaiCollections.Select(c => new CivitaiCollectionConfig
                {
                    Id = c.Id,
                    Name = c.Name,
                    Enabled = c.Enabled,
                    ImageCount = c.ImageCount,
                    FolderName = c.FolderName
                }).ToList();

                // Token Analyzer settings
                _settings.TokenAnalyzerPath = _model.TokenAnalyzerPath;

                // ComfyUI settings
                _settings.ComfyUILauncherPath = _model.ComfyUILauncherPath;
                _settings.ComfyUILauncherArgs = _model.ComfyUILauncherArgs;
                _settings.ComfyUIServerUrl = _model.ComfyUIServerUrl;
                _settings.ComfyUIStartupTimeout = _model.ComfyUIStartupTimeout;
            }
        }

        private void LoadCivitaiAlbumDropdown()
        {
            var albums = _dataStore.GetAlbumsByName();

            CivitaiDefaultAlbumComboBox.Items.Clear();
            CivitaiDefaultAlbumComboBox.Items.Add("None");

            foreach (var album in albums)
            {
                CivitaiDefaultAlbumComboBox.Items.Add(album.Name);
            }

            // Select the current default
            if (string.IsNullOrEmpty(_settings.CivitaiDefaultAlbum))
            {
                CivitaiDefaultAlbumComboBox.SelectedIndex = 0; // None
            }
            else
            {
                var index = CivitaiDefaultAlbumComboBox.Items.IndexOf(_settings.CivitaiDefaultAlbum);
                CivitaiDefaultAlbumComboBox.SelectedIndex = index >= 0 ? index : 0;
            }
        }

        private void CivitaiDefaultAlbumComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CivitaiDefaultAlbumComboBox.SelectedItem != null)
            {
                _settings.CivitaiDefaultAlbum = CivitaiDefaultAlbumComboBox.SelectedItem.ToString();
            }
        }

        private void ClearCivitaiDefaultAlbum_Click(object sender, RoutedEventArgs e)
        {
            _settings.CivitaiDefaultAlbum = null;
            CivitaiDefaultAlbumComboBox.SelectedIndex = 0; // Select "None"
        }

        private void CivitaiApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            // PasswordBox has no binding support; sync to the model manually.
            _model.CivitaiApiKey = CivitaiApiKeyBox.Password;
        }

        private void BrowseCivitaiPipelinePath_OnClick(object sender, RoutedEventArgs e)
        {
            using var dialog = new CommonOpenFileDialog();
            dialog.IsFolderPicker = true;
            dialog.Title = "Select Civitai Pipeline Repository Folder";
            if (dialog.ShowDialog(this._window) == CommonFileDialogResult.Ok)
            {
                _settings.CivitaiPipelineRepositoryPath = dialog.FileName;
            }
        }

        private void BrowseTokenAnalyzerPath_OnClick(object sender, RoutedEventArgs e)
        {
            using var dialog = new CommonOpenFileDialog();
            dialog.Title = "Select Token Analyzer Launcher";
            dialog.Filters.Add(new CommonFileDialogFilter("Batch files", "*.bat;*.cmd"));
            dialog.Filters.Add(new CommonFileDialogFilter("Python scripts", "*.py"));
            dialog.Filters.Add(new CommonFileDialogFilter("Executable files", "*.exe"));
            dialog.Filters.Add(new CommonFileDialogFilter("All files", "*.*"));
            if (dialog.ShowDialog(this._window) == CommonFileDialogResult.Ok)
            {
                _model.TokenAnalyzerPath = dialog.FileName;
            }
        }

        private void BrowseComfyUILauncher_OnClick(object sender, RoutedEventArgs e)
        {
            using var dialog = new CommonOpenFileDialog();
            dialog.Title = "Select ComfyUI Launcher";
            dialog.Filters.Add(new CommonFileDialogFilter("Batch files", "*.bat;*.cmd"));
            dialog.Filters.Add(new CommonFileDialogFilter("Python executable", "python.exe"));
            dialog.Filters.Add(new CommonFileDialogFilter("Executable files", "*.exe"));
            dialog.Filters.Add(new CommonFileDialogFilter("All files", "*.*"));
            if (dialog.ShowDialog(this._window) == CommonFileDialogResult.Ok)
            {
                _model.ComfyUILauncherPath = dialog.FileName;
            }
        }

        private void LoadCivitaiCollections()
        {
            _model.CivitaiCollections = new ObservableCollection<CivitaiCollectionModel>(
                (_settings.CivitaiCollections ?? new List<CivitaiCollectionConfig>()).Select(c => new CivitaiCollectionModel
                {
                    Id = c.Id,
                    Name = c.Name,
                    Enabled = c.Enabled,
                    ImageCount = c.ImageCount,
                    FolderName = c.FolderName
                })
            );
        }

        private async void FetchCivitaiCollections_Click(object sender, RoutedEventArgs e)
        {
            var scriptsBasePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Diffusion.PyScripts", "Civitai Collections Scraper");
            var pythonPath = Path.Combine(scriptsBasePath, ".venv", "Scripts", "python.exe");
            var mainPyPath = Path.Combine(scriptsBasePath, "main.py");

            if (!File.Exists(pythonPath))
            {
                MessageBox.Show(_window,
                    $"Python executable not found at:\n{pythonPath}\n\nPlease ensure the virtual environment is set up.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!File.Exists(mainPyPath))
            {
                MessageBox.Show(_window,
                    $"main.py not found at:\n{mainPyPath}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            FetchCollectionsButton.IsEnabled = false;
            FetchingText.Visibility = Visibility.Visible;

            try
            {
                var output = await Task.Run(() =>
                {
                    var processInfo = new ProcessStartInfo
                    {
                        FileName = pythonPath,
                        Arguments = $"main.py list-collections",
                        WorkingDirectory = scriptsBasePath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8
                    };

                    // API key via environment only - never on the command line (logged).
                    var civitaiApiKey = _settings.GetCivitaiApiKey();
                    if (!string.IsNullOrWhiteSpace(civitaiApiKey))
                    {
                        processInfo.EnvironmentVariables["CIVITAI_API_KEY"] = civitaiApiKey;
                    }

                    using var process = Process.Start(processInfo);
                    if (process == null) return null;

                    // Always read stdout - Python outputs JSON even on error (exit code 2)
                    var stdout = process.StandardOutput.ReadToEnd();
                    var stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    Logger.Log($"FetchCivitaiCollections: Python exited with code {process.ExitCode}");
                    if (!string.IsNullOrEmpty(stderr))
                        Logger.Log($"FetchCivitaiCollections: stderr (last 500 chars): {stderr[Math.Max(0, stderr.Length - 500)..]}");

                    return stdout;
                });

                if (string.IsNullOrEmpty(output))
                {
                    MessageBox.Show(_window,
                        "Failed to launch the CivitAI script.\n\nCheck that Python and the virtual environment are set up correctly.",
                        "Fetch Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var jsonStr = output.Trim();

                // Check if the response is an error object
                if (jsonStr.StartsWith("{"))
                {
                    var errorObj = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonStr);
                    var errorType = errorObj?.GetValueOrDefault("error", "");
                    var errorMsg = errorObj?.GetValueOrDefault("message", "Unknown error");

                    if (errorType == "auth")
                    {
                        MessageBox.Show(_window,
                            "CivitAI authentication failed.\n\n" +
                            "Preferred fix: enter a CivitAI API key in the field above\n" +
                            "(generate one at civitai.com > Account Settings > API Keys).\n\n" +
                            "Alternatively, refresh your cookies:\n" +
                            "1. Open Chrome and log into civitai.com\n" +
                            "2. Export cookies using 'Get cookies.txt LOCALLY' extension\n" +
                            "3. Save the cookies file in the Civitai Collections Scraper folder\n" +
                            "4. Try again.",
                            "Authentication Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        MessageBox.Show(_window,
                            $"Error fetching collections:\n{errorMsg}",
                            "Fetch Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    return;
                }

                var fetchedCollections = JsonSerializer.Deserialize<List<FetchedCollection>>(jsonStr, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (fetchedCollections == null || fetchedCollections.Count == 0)
                {
                    MessageBox.Show(_window,
                        "No collections found on your CivitAI account.",
                        "No Collections", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Merge with existing selections (preserve enabled state and folder names)
                var existingById = _model.CivitaiCollections.ToDictionary(c => c.Id);

                // Try to detect existing collection folders on disk for auto-mapping
                var existingFolders = new List<string>();
                try
                {
                    var scriptsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Diffusion.PyScripts", "Civitai Collections Scraper");
                    var configPath = Path.Combine(scriptsPath, "config.yaml");
                    if (File.Exists(configPath))
                    {
                        // Read downloads path from config.yaml
                        var configText = File.ReadAllText(configPath);
                        var match = Regex.Match(configText, @"downloads:\s*""?([^""\r\n]+)""?");
                        if (match.Success)
                        {
                            var downloadsPath = match.Groups[1].Value.Trim().Replace("/", "\\");
                            if (Directory.Exists(downloadsPath))
                            {
                                existingFolders = Directory.GetDirectories(downloadsPath)
                                    .Select(d => Path.GetFileName(d))
                                    .ToList();
                                Logger.Log($"FetchCivitaiCollections: Found {existingFolders.Count} existing folders in {downloadsPath}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"FetchCivitaiCollections: Could not scan existing folders: {ex.Message}");
                }

                var merged = new ObservableCollection<CivitaiCollectionModel>();
                foreach (var fc in fetchedCollections)
                {
                    string? folderName = null;

                    if (existingById.ContainsKey(fc.Id))
                    {
                        // Preserve existing folder name
                        folderName = existingById[fc.Id].FolderName;
                    }

                    // If no folder name set, try to find a matching existing folder
                    if (string.IsNullOrEmpty(folderName) && existingFolders.Count > 0)
                    {
                        // Check if collection name (sanitized) differs from an existing folder
                        var sanitized = Regex.Replace(fc.Name, @"[\\/:*?""<>|]", "_");
                        var exactMatch = existingFolders.FirstOrDefault(f => f == fc.Name);
                        var sanitizedMatch = existingFolders.FirstOrDefault(f => f == sanitized);

                        if (exactMatch != null)
                        {
                            // Name matches a folder exactly, no override needed
                        }
                        else if (sanitizedMatch != null)
                        {
                            // Sanitized name matches, no override needed
                        }
                        else
                        {
                            // Try fuzzy match: collection name contains chars that get sanitized
                            // Look for folders that could be an alternate sanitization
                            // e.g., "Concepts/Styles" -> existing "Concepts--Styles"
                            var nameLetters = Regex.Replace(fc.Name.ToLowerInvariant(), @"[^a-z0-9]", "");
                            var bestMatch = existingFolders
                                .Where(f => Regex.Replace(f.ToLowerInvariant(), @"[^a-z0-9]", "") == nameLetters)
                                .FirstOrDefault();
                            if (bestMatch != null)
                            {
                                folderName = bestMatch;
                                Logger.Log($"FetchCivitaiCollections: Auto-mapped '{fc.Name}' -> folder '{bestMatch}'");
                            }
                        }
                    }

                    var model = new CivitaiCollectionModel
                    {
                        Id = fc.Id,
                        Name = fc.Name,
                        ImageCount = fc.ImageCount,
                        FolderName = folderName,
                        Enabled = existingById.ContainsKey(fc.Id)
                            ? existingById[fc.Id].Enabled
                            : true
                    };
                    merged.Add(model);
                }

                _model.CivitaiCollections = merged;

                MessageBox.Show(_window,
                    $"Found {fetchedCollections.Count} collections.\nCheck the 'Folder' column for any collections with existing folders, then click Apply.",
                    "Collections Loaded", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Log($"FetchCivitaiCollections: Error - {ex.Message}");
                MessageBox.Show(_window,
                    $"Error fetching collections:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                FetchCollectionsButton.IsEnabled = true;
                FetchingText.Visibility = Visibility.Collapsed;
            }
        }

        private void CivitaiSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var c in _model.CivitaiCollections)
                c.Enabled = true;
        }

        private void CivitaiDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var c in _model.CivitaiCollections)
                c.Enabled = false;
        }

        private void CivitaiAddCollection_Click(object sender, RoutedEventArgs e)
        {
            var idText = CivitaiNewCollectionId.Text.Trim();
            var name = CivitaiNewCollectionName.Text.Trim();
            var folder = CivitaiNewCollectionFolder.Text.Trim();

            if (!int.TryParse(idText, out var id) || id <= 0)
            {
                MessageBox.Show(_window, "Please enter a valid collection ID (a positive number).", "Invalid ID", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(_window, "Please enter a collection name.", "Missing Name", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Check for duplicate
            if (_model.CivitaiCollections.Any(c => c.Id == id))
            {
                MessageBox.Show(_window, $"Collection with ID {id} already exists.", "Duplicate", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _model.CivitaiCollections.Add(new CivitaiCollectionModel
            {
                Id = id,
                Name = name,
                FolderName = string.IsNullOrWhiteSpace(folder) ? null : folder,
                Enabled = true
            });

            CivitaiNewCollectionId.Text = "";
            CivitaiNewCollectionName.Text = "";
            CivitaiNewCollectionFolder.Text = "";
        }

        private void CivitaiRemoveCollection_Click(object sender, RoutedEventArgs e)
        {
            if (CivitaiCollectionsList.SelectedItem is CivitaiCollectionModel selected)
            {
                _model.CivitaiCollections.Remove(selected);
            }
        }

        // Simple DTO for deserializing Python's JSON output
        private class FetchedCollection
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public int? ImageCount { get; set; }
        }

        // DTO for error responses
        private class FetchError
        {
            public string Error { get; set; } = "";
            public string Message { get; set; } = "";
        }
    }
}
