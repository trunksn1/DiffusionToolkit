# Civitai Script Integration Plan

## Overview

This document outlines the changes needed to integrate the Civitai Collections Scraper Python scripts into the Diffusion Toolkit repository, eliminating the need for users to configure an external script path.

---

## Current vs New Architecture

### Current (External Script):
- **Script Location:** User-configurable external path (e.g., `E:\Clouding\Dropbox\INFORMATICA\GitHub\Civitai Collections Scraper`)
- **Configuration:** `CivitaiScraperRepositoryPath` setting in Settings UI
- **User Action Required:** User must set the path before using the feature

### New (Integrated Script):
- **Script Location:** `{AppBaseDirectory}\Diffusion.PyScripts\Civitai Collections Scraper`
- **Configuration:** Hardcoded internal path, no user configuration needed
- **User Action Required:** None (scripts ship with application)

---

## Files Affected

### 1. **Settings.cs**
**File:** `Diffusion.Toolkit\Configuration\Settings.cs`
**Line:** 470-474

**Current Code:**
```csharp
public string? CivitaiScraperRepositoryPath
{
    get;
    set => UpdateValue(ref field, value);
}
```

**Action:** REMOVE this property entirely (unless we want to keep it as optional override - see Decision Point below)

---

### 2. **Settings.xaml**
**File:** `Diffusion.Toolkit\Pages\Settings.xaml`
**Lines:** 195-206

**Current Code:**
```xml
<StackPanel Orientation="Vertical" Margin="0,10,0,0">
    <Label Content="Civitai Collections Scraper Repository Path:"></Label>
    <Grid Margin="0,5,0,10">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="120"/>
        </Grid.ColumnDefinitions>
        <TextBox Height="26" VerticalContentAlignment="Center" Text="{Binding CivitaiScraperRepositoryPath, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,10,0"></TextBox>
        <Button Height="26" Grid.Column="1" Click="BrowseCivitaiScraperPath_OnClick" Content="Browse"></Button>
    </Grid>
    <TextBlock Foreground="{DynamicResource ForegroundBrush}" Margin="0,0,0,15" TextWrapping="Wrap"
               Text="Path to the Civitai Collections Scraper repository folder (e.g., E:\Clouding\Dropbox\INFORMATICA\GitHub\Civitai Collections Scraper)"></TextBlock>
</StackPanel>
```

**Action:** REMOVE this entire StackPanel section

---

### 3. **Settings.xaml.cs**
**File:** `Diffusion.Toolkit\Pages\Settings.xaml.cs`
**Lines:** 452-461

**Current Code:**
```csharp
private void BrowseCivitaiScraperPath_OnClick(object sender, RoutedEventArgs e)
{
    using var dialog = new CommonOpenFileDialog();
    dialog.IsFolderPicker = true;
    dialog.Title = "Select Civitai Collections Scraper Repository Folder";
    if (dialog.ShowDialog(this._window) == CommonFileDialogResult.Ok)
    {
        _settings.CivitaiScraperRepositoryPath = dialog.FileName;
    }
}
```

**Action:** REMOVE this method entirely

---

### 4. **MainWindow.xaml.cs** - Multiple Changes

**File:** `Diffusion.Toolkit\MainWindow.xaml.cs`

#### Change 1: Remove Path Check (Lines 1257-1261)

**Current Code:**
```csharp
if (string.IsNullOrEmpty(_settings.CivitaiScraperRepositoryPath))
{
    MessageBox.Show(this, "Civitai Collections Scraper repository path is not configured.\n\nPlease set it in Settings.", "Configuration Required", MessageBoxButton.OK, MessageBoxImage.Warning);
    return;
}
```

**New Code:**
```csharp
// Get the internal script path
var scriptsBasePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Diffusion.PyScripts", "Civitai Collections Scraper");

if (!Directory.Exists(scriptsBasePath))
{
    MessageBox.Show(this, $"Civitai Collections Scraper scripts not found at:\n{scriptsBasePath}\n\nPlease ensure the Diffusion.PyScripts folder is present in the application directory.", "Scripts Missing", MessageBoxButton.OK, MessageBoxImage.Error);
    return;
}
```

#### Change 2: Python Path (Line 1264)

**Current Code:**
```csharp
var pythonPath = Path.Combine(_settings.CivitaiScraperRepositoryPath, ".venv", "Scripts", "python.exe");
```

**New Code:**
```csharp
var pythonPath = Path.Combine(scriptsBasePath, ".venv", "Scripts", "python.exe");
```

#### Change 3: main.py Path (Line 1272)

**Current Code:**
```csharp
var mainPyPath = Path.Combine(_settings.CivitaiScraperRepositoryPath, "main.py");
```

**New Code:**
```csharp
var mainPyPath = Path.Combine(scriptsBasePath, "main.py");
```

#### Change 4: Database Path (Line 1285) - IMPORTANT DECISION NEEDED

**Current Code:**
```csharp
var civitaiDbPath = Path.Combine(_settings.CivitaiScraperRepositoryPath, "civitai_state.db");
```

**Option A - Keep DB in Script Folder (Simple):**
```csharp
var civitaiDbPath = Path.Combine(scriptsBasePath, "civitai_state.db");
```

**Option B - Move DB to AppData (Recommended for Data Persistence):**
```csharp
var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiffusionToolkit", "Civitai");
Directory.CreateDirectory(appDataPath); // Ensure directory exists
var civitaiDbPath = Path.Combine(appDataPath, "civitai_state.db");
```

**Recommendation:** Use Option B - This ensures the database survives application reinstalls/updates.

#### Change 5: Working Directory (Line 1296)

**Current Code:**
```csharp
WorkingDirectory = _settings.CivitaiScraperRepositoryPath,
```

**New Code:**
```csharp
WorkingDirectory = scriptsBasePath,
```

#### Change 6: Logging (Line 1289)

**Current Code:**
```csharp
Logger.Log($"LaunchCivitaiScraper: Working directory: {_settings.CivitaiScraperRepositoryPath}");
```

**New Code:**
```csharp
Logger.Log($"LaunchCivitaiScraper: Script base path: {scriptsBasePath}");
Logger.Log($"LaunchCivitaiScraper: Database path: {civitaiDbPath}");
Logger.Log($"LaunchCivitaiScraper: Working directory: {scriptsBasePath}");
```

#### Change 7: AssignDownloadedImagesToAlbum Method (Lines 1431-1439)

**Current Code:**
```csharp
Logger.Log($"AssignDownloadedImagesToAlbum: _settings?.CivitaiScraperRepositoryPath = '{_settings?.CivitaiScraperRepositoryPath}'");

if (string.IsNullOrEmpty(_settings?.CivitaiScraperRepositoryPath))
{
    Logger.Log("AssignDownloadedImagesToAlbum: ERROR - Civitai scraper repository path not configured");
    return;
}

var civitaiDbPath = Path.Combine(_settings.CivitaiScraperRepositoryPath, "civitai_state.db");
```

**New Code:**
```csharp
// Get database path (use same logic as in LaunchCivitaiScraper)
var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiffusionToolkit", "Civitai");
var civitaiDbPath = Path.Combine(appDataPath, "civitai_state.db");

Logger.Log($"AssignDownloadedImagesToAlbum: Database path = '{civitaiDbPath}'");
```

---

## Decision Points

### Decision 1: Keep Setting as Optional Override?

**Option A - Complete Removal:**
- Remove `CivitaiScraperRepositoryPath` setting entirely
- Always use internal path
- Simpler, cleaner

**Option B - Keep as Hidden Override:**
- Keep the setting but remove from UI
- Check setting first, fall back to internal path if empty
- Allows power users to override if needed

**Recommendation:** Option A (Complete Removal) - Keep it simple. The whole point is to integrate the scripts.

### Decision 2: Database Location

**Option A - Scripts Folder:**
```
E:\...\Diffusion.Toolkit\bin\Debug\...\Diffusion.PyScripts\Civitai Collections Scraper\civitai_state.db
```
- Pros: Everything in one place
- Cons: Lost on reinstall/update

**Option B - AppData:**
```
C:\Users\{User}\AppData\Roaming\DiffusionToolkit\Civitai\civitai_state.db
```
- Pros: Survives reinstalls, user-specific
- Cons: Slightly more complex

**Recommendation:** Option B (AppData) - Better for data persistence.

---

## Implementation Steps

### Step 1: Update MainWindow.xaml.cs

Add a helper method at the class level:

```csharp
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
```

### Step 2: Update LaunchCivitaiScraper Method

**Before (Lines 1257-1296):**
```csharp
if (string.IsNullOrEmpty(_settings.CivitaiScraperRepositoryPath))
{
    MessageBox.Show(...);
    return;
}

var pythonPath = Path.Combine(_settings.CivitaiScraperRepositoryPath, ".venv", "Scripts", "python.exe");
// ... etc
```

**After:**
```csharp
// Get internal script path
var scriptsBasePath = GetCivitaiScriptsPath();

if (!Directory.Exists(scriptsBasePath))
{
    MessageBox.Show(this, $"Civitai Collections Scraper scripts not found at:\n{scriptsBasePath}\n\nPlease ensure the Diffusion.PyScripts folder is present.", "Scripts Missing", MessageBoxButton.OK, MessageBoxImage.Error);
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

// Record the current time before starting download
_downloadStartTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
Logger.Log($"LaunchCivitaiScraper: Recording download start time = {_downloadStartTime}");

var civitaiDbPath = GetCivitaiDatabasePath();

Logger.Log($"LaunchCivitaiScraper: Scripts base path: {scriptsBasePath}");
Logger.Log($"LaunchCivitaiScraper: Python path: {pythonPath}");
Logger.Log($"LaunchCivitaiScraper: main.py path: {mainPyPath}");
Logger.Log($"LaunchCivitaiScraper: Database path: {civitaiDbPath}");
Logger.Log($"LaunchCivitaiScraper: Working directory: {scriptsBasePath}");

// Launch Python process directly
var processInfo = new ProcessStartInfo()
{
    FileName = pythonPath,
    Arguments = "main.py sync",
    WorkingDirectory = scriptsBasePath,
    UseShellExecute = true,
    CreateNoWindow = false
};
```

### Step 3: Update AssignDownloadedImagesToAlbum Method

**Before (Lines 1431-1439):**
```csharp
Logger.Log($"AssignDownloadedImagesToAlbum: _settings?.CivitaiScraperRepositoryPath = '{_settings?.CivitaiScraperRepositoryPath}'");

if (string.IsNullOrEmpty(_settings?.CivitaiScraperRepositoryPath))
{
    Logger.Log("AssignDownloadedImagesToAlbum: ERROR - Civitai scraper repository path not configured");
    return;
}

var civitaiDbPath = Path.Combine(_settings.CivitaiScraperRepositoryPath, "civitai_state.db");
```

**After:**
```csharp
var civitaiDbPath = GetCivitaiDatabasePath();
Logger.Log($"AssignDownloadedImagesToAlbum: Database path = '{civitaiDbPath}'");
```

### Step 4: Remove Settings UI

In **Settings.xaml**, remove lines 195-206 (the entire Civitai Scraper Repository Path section)

### Step 5: Remove Browse Handler

In **Settings.xaml.cs**, remove lines 452-461 (the `BrowseCivitaiScraperPath_OnClick` method)

### Step 6: Remove or Deprecate Setting Property

In **Settings.cs**, either:
- **Option A:** Remove lines 470-474 entirely
- **Option B:** Mark as obsolete and add comment:
```csharp
[Obsolete("Script path is now internal - this setting is no longer used")]
public string? CivitaiScraperRepositoryPath
{
    get;
    set => UpdateValue(ref field, value);
}
```

**Recommendation:** Option A (Remove entirely)

---

## Build Configuration

### Ensure Scripts Are Copied to Output

The `Diffusion.PyScripts` folder must be copied to the build output directory.

Check the `.csproj` file and ensure there's an ItemGroup like:

```xml
<ItemGroup>
    <None Update="Diffusion.PyScripts\**\*">
        <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
</ItemGroup>
```

This ensures all files in `Diffusion.PyScripts` and subdirectories are copied during build.

---

## Migration for Existing Users

### Issue: Existing Database

Users who already have `civitai_state.db` in their old external path will lose it with this change.

### Solution Options:

**Option 1 - Manual Migration (Simple):**
- Document in release notes that users should copy their `civitai_state.db` to the new AppData location
- Provide the path in a message box on first run

**Option 2 - Automatic Migration (Better UX):**

Add migration logic in `LaunchCivitaiScraper`:

```csharp
// Check if old database exists in settings and migrate it
if (!string.IsNullOrEmpty(_settings?.CivitaiScraperRepositoryPath))
{
    var oldDbPath = Path.Combine(_settings.CivitaiScraperRepositoryPath, "civitai_state.db");
    var newDbPath = GetCivitaiDatabasePath();

    if (File.Exists(oldDbPath) && !File.Exists(newDbPath))
    {
        Logger.Log($"LaunchCivitaiScraper: Migrating database from {oldDbPath} to {newDbPath}");
        try
        {
            File.Copy(oldDbPath, newDbPath);
            Logger.Log("LaunchCivitaiScraper: Database migration successful");
        }
        catch (Exception ex)
        {
            Logger.Log($"LaunchCivitaiScraper: Database migration failed - {ex.Message}");
        }
    }
}
```

**Recommendation:** Implement Option 2 (Automatic Migration) for better user experience.

---

## Testing Checklist

After implementing changes:

- [ ] Build succeeds without errors
- [ ] `Diffusion.PyScripts` folder is copied to output directory
- [ ] Civitai scraper button launches script correctly
- [ ] Python process starts from internal path
- [ ] Database is created in AppData location
- [ ] Download and album assignment work correctly
- [ ] Settings page no longer shows the repository path field
- [ ] No references to `CivitaiScraperRepositoryPath` remain (except migration code if implemented)
- [ ] Logs show correct paths being used

---

## Summary of Changes

| Component | Current Behavior | New Behavior |
|-----------|-----------------|--------------|
| **Script Location** | User-configurable external path | Internal: `{AppBase}\Diffusion.PyScripts\Civitai Collections Scraper` |
| **Database Location** | Same as script folder | AppData: `{AppData}\DiffusionToolkit\Civitai\civitai_state.db` |
| **Settings UI** | TextBox + Browse button | Removed entirely |
| **User Configuration** | Required before use | Not needed (works out of box) |
| **Path Resolution** | `_settings.CivitaiScraperRepositoryPath` | `GetCivitaiScriptsPath()` helper method |

---

## Backward Compatibility Notes

- **Settings File:** Old settings with `CivitaiScraperRepositoryPath` will be ignored (or trigger migration if implemented)
- **Database Migration:** Automatic migration recommended for smooth transition
- **UI Changes:** Removal of settings UI is a breaking change but improves UX

---

## File Structure After Integration

```
Diffusion.Toolkit/
├── bin/
│   └── Debug/
│       └── net10.0-windows/
│           ├── Diffusion.Toolkit.exe
│           └── Diffusion.PyScripts/
│               └── Civitai Collections Scraper/
│                   ├── .venv/
│                   │   └── Scripts/
│                   │       └── python.exe
│                   ├── main.py
│                   └── (other Python files)
│
└── (User's AppData)/
    └── DiffusionToolkit/
        └── Civitai/
            └── civitai_state.db
```

---

## Next Steps

1. **Implement helper methods** (`GetCivitaiScriptsPath`, `GetCivitaiDatabasePath`)
2. **Update LaunchCivitaiScraper** method with new path logic
3. **Update AssignDownloadedImagesToAlbum** method with new database path
4. **Remove Settings UI** elements
5. **Remove Settings property** (or mark obsolete)
6. **Add migration logic** for existing users (optional but recommended)
7. **Update .csproj** to ensure scripts are copied
8. **Test thoroughly** with checklist above
9. **Update documentation** (if any exists)
10. **Create release notes** documenting the change

---

## Risks and Mitigation

| Risk | Impact | Mitigation |
|------|--------|-----------|
| Users lose database on upgrade | High | Implement automatic migration |
| Scripts not copied during build | High | Verify .csproj ItemGroup configuration |
| Path separator issues (\ vs /) | Medium | Use `Path.Combine` consistently |
| Missing .venv on fresh install | High | Document Python setup requirements |
| AppData folder permissions | Low | Handle directory creation gracefully |

---

## Benefits of This Change

1. **Improved User Experience:** No configuration needed, works out of the box
2. **Cleaner Settings UI:** One less thing users need to configure
3. **Better Distribution:** Scripts ship with application
4. **Version Control:** Scripts are versioned with the application
5. **Easier Updates:** Script updates come with app updates
6. **Data Persistence:** Database in AppData survives reinstalls

---

## Alternative: Hybrid Approach

If you want to keep some flexibility:

1. Use internal path as **default**
2. Keep setting but **hide from UI** (or make it "Advanced")
3. Check setting first, fall back to internal if empty:

```csharp
private string GetCivitaiScriptsPath()
{
    // Check if setting exists and directory is valid
    if (!string.IsNullOrEmpty(_settings?.CivitaiScraperRepositoryPath) &&
        Directory.Exists(_settings.CivitaiScraperRepositoryPath))
    {
        Logger.Log("Using custom Civitai scraper path from settings");
        return _settings.CivitaiScraperRepositoryPath;
    }

    // Fall back to internal path
    return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Diffusion.PyScripts", "Civitai Collections Scraper");
}
```

This allows power users to override while keeping it simple for normal users.

**Recommendation:** Start with full integration (no override), add hybrid approach later only if users request it.
