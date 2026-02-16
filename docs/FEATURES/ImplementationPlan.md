# Diffusion Toolkit - Feature Implementation Plan

## 1. Overview of Changes

This document outlines the implementation plan for adding new features to the Diffusion Toolkit application. The changes focus on improving the workflow for two external script-launching features: the Civitai Scraper and the Pipeline Script.

**Civitai Scraper Enhancements:**
-   **Album Assignment:** After downloading images, users will be prompted to assign them to a new or existing album.
-   **Configuration:** Users will be able to set a default album and control the prompting behavior through the application's settings.
-   **Auto-Refresh:** The image library will automatically refresh after the download script completes, making new images immediately visible.

**Pipeline Script Enhancements:**
-   **Administrator Privileges:** The pipeline script will be launched with administrator rights to ensure it has the necessary permissions to execute.

## 2. Detailed Implementation Steps

### Feature 1: Civitai Scraper Enhancements

#### 1.1. Modify the Scraper Launch Logic

The current implementation in `MainWindow.xaml.cs` launches the scraper script and doesn't wait for it to complete. This will be changed to an asynchronous operation that awaits the process exit.

**File:** `Diffusion.Toolkit\MainWindow.xaml.cs`
**Method:** `LaunchCivitaiScraper()`

1.  Modify the method signature to be `private async void LaunchCivitaiScraper()`.
2.  Wrap the `Process.Start(processInfo)` call in a `Task`.
3.  Use `process.WaitForExitAsync()` to wait for the script to complete.
4.  After the process exits, call a new method, `OnCivitaiScraperCompleted()`.

```csharp
// Example modification in MainWindow.xaml.cs
private async void LaunchCivitaiScraper()
{
    try
    {
        var scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "NSFW_CIVITAI_Collections_Scraper.bat");

        if (!File.Exists(scriptPath))
        {
            MessageBox.Show(this, $"Script not found at: {scriptPath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var processInfo = new ProcessStartInfo()
        {
            FileName = scriptPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)
        };

        var process = Process.Start(processInfo);

        if (process != null)
        {
            await process.WaitForExitAsync();
            await OnCivitaiScraperCompleted();
        }
    }
    catch (Exception ex)
    {
        MessageBox.Show(this, $"Error launching scraper: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
```

#### 1.2. Implement Album Selection Logic

A new method `OnCivitaiScraperCompleted()` will handle the post-download workflow.

**File:** `Diffusion.Toolkit\MainWindow.xaml.cs` (new method)

1.  **Check for Default Album:**
    -   If `_settings.CivitaiDefaultAlbum` is set and `_settings.CivitaiAlwaysPromptForAlbum` is `false`, directly call a new method `AssignImagesToAlbum(albumName)` and then proceed to step 1.3 (Auto-Refresh).
    -   If the default album is "None", skip album assignment and proceed to step 1.3.

2.  **Show Album Selection Dialog:**
    -   If no default is set or if prompting is forced, create and show a new custom dialog window: `CivitaiAlbumSelectionDialog`.
    -   This dialog will be inspired by `AlbumListWindow` but with added controls.
    -   **Dialog Features:**
        -   Dropdown list of existing albums (from `_model.Albums`).
        -   Option to create a new album (TextBox).
        -   A "Skip" button.
        -   A "Remember my choice" checkbox.
    -   The dialog will return a result object containing the user's choice (existing album, new album name, skip) and the state of the "Remember" checkbox.

3.  **Process Dialog Result:**
    -   If the user chose an album (new or existing):
        -   Call `AssignImagesToAlbum(albumName)`.
        -   If "Remember my choice" was checked, save the choice to `_settings.CivitaiDefaultAlbum`.
    -   If the user clicked "Skip":
        -   If "Remember my choice" was checked, save a special value (e.g., "None") to `_settings.CivitaiDefaultAlbum`.

#### 1.3. Auto-Refresh After Download

**File:** `Diffusion.Toolkit\MainWindow.xaml.cs` (in `OnCivitaiScraperCompleted`)

1.  After the album assignment logic is complete, trigger a full rescan of the library.
2.  The `RescanTask` in `MainWindow.xaml.Scanning.cs` shows that this is done via `ServiceLocator.ScanningService`.
3.  Call `ServiceLocator.ScanningService.Scan(false, false, CancellationToken.None)` wrapped in a progress task to provide UI feedback.

```csharp
// Example call in OnCivitaiScraperCompleted()
if (await ServiceLocator.ProgressService.TryStartTask())
{
    await ServiceLocator.ScanningService.ScanWatchedFolders(false, false, ServiceLocator.ProgressService.CancellationToken);
    ServiceLocator.ProgressService.CompleteTask();
}
```

#### 1.4. Integrate with Settings

**File:** `Diffusion.Toolkit\Configuration\Settings.cs`

1.  Add two new properties to the `Settings` class:
    ```csharp
    public string CivitaiDefaultAlbum { get; set; }
    public bool CivitaiAlwaysPromptForAlbum { get; set; }
    ```

**File:** `Diffusion.Toolkit\Pages\Settings.xaml` and `Settings.xaml.cs`

1.  Add a new section to the settings page UI titled "Civitai Scraper".
2.  Add a `ComboBox` for "Default Album for Downloads".
    -   Bind its `ItemsSource` to the collection of albums.
    -   Add a "None" option.
    -   Bind the `SelectedItem` to `Settings.CivitaiDefaultAlbum`.
3.  Add a `CheckBox` for "Always prompt for album selection after download".
    -   Bind its `IsChecked` property to `Settings.CivitaiAlwaysPromptForAlbum`.
4.  Add a "Clear Default" `Button` that resets `Settings.CivitaiDefaultAlbum` to `null`.

### Feature 2: Run Pipeline Script as Administrator

This is a straightforward change to the existing launch logic.

**File:** `Diffusion.Toolkit\MainWindow.xaml.cs`
**Method:** `LaunchCivitaiPipeline()`

1.  In the `ProcessStartInfo` object, set the `Verb` property to `"runas"`. This will trigger the UAC prompt if necessary.
2.  Set `UseShellExecute` to `true` (it already is), as this is required for the `Verb` property to work.

```csharp
// Example modification in MainWindow.xaml.cs
var processInfo = new ProcessStartInfo()
{
    FileName = scriptPath,
    UseShellExecute = true,
    WorkingDirectory = Path.GetDirectoryName(scriptPath),
    Verb = "runas" // This is the required change
};
```

## 3. New Classes/Files Needed

1.  **`CivitaiAlbumSelectionDialog.xaml`**: A new `Window` for the album selection prompt.
2.  **`CivitaiAlbumSelectionDialog.xaml.cs`**: The code-behind for the dialog, containing its logic and properties to hold the result.
3.  **`CivitaiAlbumSelectionResult.cs`**: A simple class to pass the dialog's results back to the main window.

## 4. Changes to Existing Files

1.  **`Diffusion.Toolkit/MainWindow.xaml.cs`**:
    -   Modify `LaunchCivitaiScraper()` to be `async` and await process exit.
    -   Add new method `OnCivitaiScraperCompleted()` to handle the post-download workflow.
    -   Modify `LaunchCivitaiPipeline()` to add the `Verb = "runas"` property.
2.  **`Diffusion.Toolkit/Configuration/Settings.cs`**:
    -   Add `CivitaiDefaultAlbum` and `CivitaiAlwaysPromptForAlbum` properties.
3.  **`Diffusion.Toolkit/Pages/Settings.xaml`**:
    -   Add the new UI controls for the Civitai Scraper settings.
4.  **`Diffusion.Toolkit/Pages/Settings.xaml.cs`**:
    -   Handle binding and data loading for the new settings controls.

## 5. UI Considerations

-   The new `CivitaiAlbumSelectionDialog` should be styled consistently with existing dialogs like `AlbumListWindow`.
-   It should be a modal dialog that blocks the main window until the user makes a choice.
-   The new section in the Settings page should be clearly labeled and organized.

## 6. Database Changes

No database schema changes are required. The implementation will use the existing `Albums` and `ImageAlbums` tables via the `DataStore` service.

## 7. Potential Challenges and Solutions

-   **Challenge:** The downloaded images are not known to the application. The scraper downloads them to a folder, but the application doesn't know which images are new.
    -   **Solution:** The current plan is to trigger a full rescan (`ScanWatchedFolders`). This is the most straightforward approach and leverages existing functionality. While potentially inefficient if the library is huge, it guarantees all new images are found. A future optimization could be to have the scraper script output a list of downloaded files that the application could then process directly.
-   **Challenge:** Handling process exit and potential errors gracefully.
    -   **Solution:** The `try-catch` blocks around the process launch should be robust. The exit code of the script can also be checked to see if it completed successfully.

## 8. Testing Considerations

1.  **Civitai Scraper Workflow:**
    -   Test the "first run" experience where no default album is set.
    -   Test creating a new album from the dialog.
    -   Test selecting an existing album.
    -   Test the "Skip" option.
    -   For each of the above, test with the "Remember my choice" checkbox both checked and unchecked.
    -   Verify that the chosen setting is correctly saved in `settings.json`.
    -   Verify that if a default is set, the dialog is skipped on subsequent runs.
    -   Verify that the "Always prompt" setting forces the dialog to appear even when a default is set.
    -   Verify the UI auto-refreshes and the new images (if any) are visible after the process completes.
2.  **Pipeline Script:**
    -   Click the button and verify that the Windows UAC prompt appears.
    -   Test both accepting and denying the UAC prompt to ensure the application handles both cases without crashing.
3.  **Settings Page:**
    -   Verify the "Default Album" dropdown is populated correctly.
    -   Verify that changing the selection and saving settings works.
    -   Verify the "Clear Default" button works as expected.
    -   Verify the "Always prompt" checkbox saves its state correctly.

---

## 9. Technical Review & Analysis

### 9.1 Critical Issues Identified

#### Issue 1: Process.WaitForExitAsync() with UseShellExecute = true ⚠️ CRITICAL

**Problem:** When `UseShellExecute = true` is used with batch files, `Process.Start()` returns a Process object that represents the `cmd.exe` shell, not the actual script. The shell may exit immediately while the script continues running, or the Process object may not have access to track the actual process at all.

**Impact:** The `WaitForExitAsync()` call may return immediately, causing the album selection dialog to appear before the download completes.

**Solution Options:**
1. **File-based signaling (RECOMMENDED):**
   - Have the batch script create a "completion marker" file (e.g., `.download_complete`) as its last action
   - In the C# code, poll for this file's existence after launching the process
   - Delete the marker file once detected
   - Example:
     ```csharp
     var markerFile = Path.Combine(downloadPath, ".download_complete");
     if (File.Exists(markerFile)) File.Delete(markerFile); // Clean up old marker

     // Launch process...

     // Poll for completion
     while (!File.Exists(markerFile) && !cancellationToken.IsCancellationRequested)
     {
         await Task.Delay(1000, cancellationToken); // Check every second
     }
     ```

2. **Process monitoring:**
   - Monitor for the absence of the script's process by name
   - More complex and less reliable

3. **Hybrid approach:**
   - Try to wait for process with a timeout
   - Fall back to file-based signaling if the process handle is unreliable

#### Issue 2: Album Assignment Logic Order 🔄

**Problem:** The current plan suggests assigning images to an album before we know which images were downloaded. The workflow should be:
1. Script completes download
2. **Scan for new images** (discovers what was downloaded)
3. **Identify the new images** (compare before/after)
4. Show album selection dialog
5. Assign the new images to the chosen album

**Current Plan Order (Incorrect):**
```
Script completes → Show dialog → Assign images → Scan
```

**Corrected Order:**
```
Script completes → Scan → Identify new images → Show dialog → Assign new images
```

**Solution:** We need to track which images are new:
```csharp
private async Task OnCivitaiScraperCompleted()
{
    // Get current image IDs before scan
    var imageIdsBeforeScan = await _dataStore.GetAllImageIds();

    // Perform scan
    await ScanForNewImages();

    // Get image IDs after scan
    var imageIdsAfterScan = await _dataStore.GetAllImageIds();

    // Identify newly added images
    var newImageIds = imageIdsAfterScan.Except(imageIdsBeforeScan).ToList();

    if (newImageIds.Count == 0)
    {
        await ShowMessage("No new images were downloaded.");
        return;
    }

    // Now show album selection dialog
    var albumChoice = await ShowAlbumSelectionDialog(newImageIds.Count);

    // Assign new images to album
    if (!string.IsNullOrEmpty(albumChoice))
    {
        await AssignImagesToAlbum(newImageIds, albumChoice);
    }
}
```

#### Issue 3: Missing Database Query Method

**Problem:** The plan assumes we can call methods like `AssignImagesToAlbum()` but doesn't specify how to:
1. Query for image IDs before/after scan
2. Bulk assign images to an album

**Solution:** Need to add methods to `DataStore` or use existing ones:
- Check if `DataStore.GetAllImageIds()` exists or create it
- Check if bulk album assignment method exists or create it
- These might already exist in the codebase - need to verify

### 9.2 Existing Functionality Impact Assessment

#### Will This Break Existing Features? ✅ UNLIKELY

**Civitai Scraper Button:**
- Currently launches script and returns immediately
- Changing to `async void` is safe for event handlers
- Adding `await` won't break the button - it just makes it wait
- **Verdict:** No breakage expected

**Pipeline Button:**
- Adding `Verb = "runas"` is additive only
- If UAC is denied, the process simply won't start - same as now but with UAC prompt
- **Verdict:** No breakage expected, improves reliability

**Settings:**
- Adding new properties won't break existing settings
- Need to ensure nullable types to handle missing values: `string?` and `bool?`
- **Verdict:** No breakage if nullable types are used

### 9.3 Efficiency & Simplification Opportunities

#### Opportunity 1: Use Existing Infrastructure
**Current Plan:** Creates new `CivitaiAlbumSelectionDialog.xaml` window
**Better Approach:** Use the existing `MessagePopupManager` system (already used throughout the app)
- The app uses `ServiceLocator.MessageService.Show()` for dialogs
- Could create a custom popup using the existing popup infrastructure
- More consistent with the rest of the application
- Example: `ManageAlbumWindow` already exists and handles album creation

**Recommendation:**
- Reuse or extend `ManageAlbumWindow` instead of creating a completely new dialog
- Or use the MessagePopup system with custom content

#### Opportunity 2: Simplified Settings Binding
**Current Plan:** Manual binding in code-behind
**Better Approach:** Use direct XAML binding to Settings

Settings.xaml already binds to a Settings object. We can leverage this:
```xaml
<ComboBox ItemsSource="{Binding Albums}"
          SelectedValue="{Binding CivitaiDefaultAlbum}"
          DisplayMemberPath="Name"
          SelectedValuePath="Name"/>
```

#### Opportunity 3: Debounce Multiple Rapid Clicks
**Consideration:** User might rapidly click the scraper button multiple times
**Solution:** Disable the button while script is running:
```csharp
_model.LaunchCivitaiScraperCommand.CanExecute = false; // Disable
// ... run script ...
_model.LaunchCivitaiScraperCommand.CanExecute = true; // Re-enable
```

### 9.4 Recommended Implementation Sequence

1. **Phase 1: Administrator Privileges (Simplest)**
   - Add `Verb = "runas"` to pipeline script
   - Test thoroughly
   - **Estimated effort:** 5 minutes + testing

2. **Phase 2: Process Completion Detection**
   - Modify batch script to create completion marker file
   - Implement file-based polling in C#
   - Test thoroughly
   - **Estimated effort:** 30 minutes + testing

3. **Phase 3: Auto-Refresh After Download**
   - Implement scan triggering after script completion
   - Test thoroughly
   - **Estimated effort:** 15 minutes + testing

4. **Phase 4: Album Assignment**
   - Add Settings properties (with nullable types)
   - Implement image ID tracking (before/after scan)
   - Create album selection dialog (or reuse existing)
   - Implement assignment logic
   - **Estimated effort:** 2-3 hours + testing

5. **Phase 5: Settings UI**
   - Add UI controls to Settings page
   - Wire up bindings
   - Test thoroughly
   - **Estimated effort:** 1 hour + testing

### 9.5 Additional Recommendations

1. **Error Handling:**
   - Check script exit code to detect failures
   - Show user-friendly error messages if download fails
   - Don't show album dialog if no images were downloaded

2. **User Feedback:**
   - Show progress indicator while waiting for script completion
   - Show toast notification when new images are found
   - Display count of new images in the album selection dialog

3. **Settings Persistence:**
   - Ensure settings are saved immediately when changed
   - Use `_settings.SetDirty()` after modifications

4. **Cancellation Support:**
   - Allow user to cancel the wait operation
   - Add a "Cancel" button or use existing cancellation infrastructure

### 9.6 Final Verdict

**Will it work?**
- ✅ Yes, with the critical fix for process waiting (file-based signaling)
- ⚠️ Needs order correction for album assignment

**Will it break existing functionality?**
- ✅ No, changes are additive and isolated

**Can it be done more efficiently?**
- ✅ Yes, several opportunities for simplification identified above
- Use existing infrastructure (MessageService, ManageAlbumWindow)
- Implement in phases to reduce complexity

**Overall Assessment:**
The plan is solid with excellent structure, but requires critical fixes for process completion detection and album assignment order. The suggested improvements will make implementation cleaner and more maintainable.
