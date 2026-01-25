# Civitai Download and Album Assignment - ACTUAL Code Flow

This document explains the ACTUAL code flow from button click to album assignment, based on the real implementation in the codebase.

---

## Overview

When the user clicks the "Launch NSFW Civitai Collections Scraper" button, the following happens:
1. Album selection dialog is shown (BEFORE download)
2. Last download ID is saved from Civitai database
3. Python script is launched to download images
4. Process waits for script to complete
5. Scan is triggered to add new images to Diffusion Toolkit database
6. New downloads are queried from Civitai database
7. Paths are matched to get Image IDs from Diffusion Toolkit database
8. Images are assigned to the selected album

---

## 1. Button Click Event

**File:** `Diffusion.Toolkit\MainWindow.xaml.cs`
**Line:** 119
**Command:** `_model.LaunchCivitaiScraperCommand = new RelayCommand<object>((o) => LaunchCivitaiScraper());`

**Method Called:** `LaunchCivitaiScraper()` (Line 1170)

### Initial Logging:
```
==========================================
LaunchCivitaiScraper: STARTING
==========================================
LaunchCivitaiScraper: Reset _selectedAlbumForDownload to null
```

### Initial Checks (Lines 1183-1188):
- Check if `_settings` is null
- If null, show error and return

**Potential Failure:** Settings not initialized

---

## 2. Album Selection Logic (Lines 1190-1253)

### Check if Prompt is Needed:
```csharp
bool needsPrompt = _settings.CivitaiAlwaysPromptForAlbum ||
                  string.IsNullOrEmpty(_settings.CivitaiDefaultAlbum) ||
                  _settings.CivitaiDefaultAlbum == "None";
```

### If Prompt is Needed:

**File:** `Diffusion.Toolkit\CivitaiAlbumSelectionWindow.xaml.cs`

1. Load albums if not already loaded (Line 1206-1210)
2. Create `CivitaiAlbumSelectionWindow` with albums collection (Line 1217)
3. Show dialog (Line 1221)
4. If user cancels, return without launching download
5. Store selected album in `_selectedAlbumForDownload` (Line 1230)
6. If "Remember" is checked, save as default album (Lines 1234-1238)

### Logging:
```
LaunchCivitaiScraper: Settings - AlwaysPrompt=True, DefaultAlbum=None
LaunchCivitaiScraper: needsPrompt = True
LaunchCivitaiScraper: Loading albums
LaunchCivitaiScraper: Albums count: 45
LaunchCivitaiScraper: Creating dialog with 45 albums
LaunchCivitaiScraper: Showing dialog
LaunchCivitaiScraper: Selected album: My Album Name
```

**Potential Failures:**
- User cancels dialog (normal exit, no download happens)
- Error showing dialog (caught and logged)

---

## 3. Configuration Checks (Lines 1256-1278)

### Check Repository Path:
- Verify `_settings.CivitaiScraperRepositoryPath` is not empty
- If empty, show error and return

### Check Python Executable:
```csharp
var pythonPath = Path.Combine(_settings.CivitaiScraperRepositoryPath, ".venv", "Scripts", "python.exe");
```
- Check if file exists
- If not found, show error and return

### Check main.py:
```csharp
var mainPyPath = Path.Combine(_settings.CivitaiScraperRepositoryPath, "main.py");
```
- Check if file exists
- If not found, show error and return

**Potential Failures:**
- Repository path not configured
- Python not found at expected location
- main.py not found

---

## 4. Get Last Download ID (Lines 1280-1304)

**THIS IS CRITICAL FOR THE ALBUM ASSIGNMENT ISSUE**

### SQL Query to Civitai Database:
```csharp
var civitaiDbPath = Path.Combine(_settings.CivitaiScraperRepositoryPath, "civitai_state.db");

using (var connection = new SQLite.SQLiteConnection(civitaiDbPath, SQLite.SQLiteOpenFlags.ReadOnly))
{
    _lastDownloadId = connection.ExecuteScalar<int>("SELECT COALESCE(MAX(civitai_id), 0) FROM downloads");
}
```

**Database:** `civitai_state.db`
**Table:** `downloads`
**Column:** `civitai_id` (INTEGER, auto-increment primary key)

**What this does:**
- Gets the highest `civitai_id` currently in the downloads table
- Stores it in `_lastDownloadId` field (defined at line 1168: `private int _lastDownloadId = 0;`)
- This baseline allows us to identify NEW downloads after the script completes

**Example:** If max civitai_id = 5391, then `_lastDownloadId = 5391`

### Logging:
```
LaunchCivitaiScraper: Getting last download ID before starting...
LaunchCivitaiScraper: Last download ID before starting = 5391
```

**Potential Failures:**
- Database file doesn't exist (fallback to 0)
- Query error (caught, fallback to 0)

---

## 5. Launch Python Process (Lines 1306-1338)

### Process Configuration:
```csharp
var processInfo = new ProcessStartInfo()
{
    FileName = pythonPath,
    Arguments = "main.py sync",
    WorkingDirectory = _settings.CivitaiScraperRepositoryPath,
    UseShellExecute = true,
    CreateNoWindow = false
};
```

**Command executed:** `{pythonPath} main.py sync`
**Example:** `E:\Civitai\...\.venv\Scripts\python.exe main.py sync`

### Logging:
```
LaunchCivitaiScraper: Python path: E:\...\python.exe
LaunchCivitaiScraper: main.py path: E:\...\main.py
LaunchCivitaiScraper: Working directory: E:\...
LaunchCivitaiScraper: Starting Python process...
LaunchCivitaiScraper: Process started with PID 12345
```

### Wait for Completion:
```csharp
await Task.Run(() =>
{
    Logger.Log("LaunchCivitaiScraper: Waiting for process to complete...");
    process.WaitForExit();
    Logger.Log($"LaunchCivitaiScraper: Process exited with code {process.ExitCode}");
});
```

**This is BLOCKING** - execution waits here until Python script finishes

**Potential Failures:**
- Process fails to start
- Python script errors out

---

## 6. Post-Completion Handler (Line 1342)

```csharp
await OnCivitaiScraperCompleted();
```

**Method:** `OnCivitaiScraperCompleted()` (Line 1354)

---

## 7. Scan for New Images (Lines 1365-1389)

### Logging:
```
======================================
OnCivitaiScraperCompleted: STARTING
OnCivitaiScraperCompleted: Selected album = 'My Album Name'
OnCivitaiScraperCompleted: Last download ID = 5391
======================================
OnCivitaiScraperCompleted: Starting scan for new images
```

### Scan Call:
```csharp
ServiceLocator.ProgressService.SetStatus("Scanning for new images...");
await ServiceLocator.ScanningService.ScanWatchedFolders(false, false, ServiceLocator.ProgressService.CancellationToken);
```

**What this does:**
- Scans all watched folders configured in Diffusion Toolkit
- Finds new image files
- Adds them to the `Image` table in Diffusion Toolkit database
- Auto-generates `Image.Id` values (auto-increment primary key)

**CRITICAL:** The downloaded images MUST be in a folder that Diffusion Toolkit is configured to watch, or they won't be found!

### Logging:
```
OnCivitaiScraperCompleted: Scan completed successfully
OnCivitaiScraperCompleted: Search refreshed
```

---

## 8. Check if Album Assignment is Needed (Lines 1392-1405)

```csharp
if (!string.IsNullOrEmpty(_selectedAlbumForDownload) && _selectedAlbumForDownload != "None")
{
    await AssignDownloadedImagesToAlbum(_selectedAlbumForDownload);
}
```

### Logging:
```
OnCivitaiScraperCompleted: Checking if should assign to album...
OnCivitaiScraperCompleted: _selectedAlbumForDownload IsNullOrEmpty = False
OnCivitaiScraperCompleted: _selectedAlbumForDownload == 'None' = False
OnCivitaiScraperCompleted: YES - Calling AssignDownloadedImagesToAlbum with album 'My Album Name'
```

If album is "None" or empty, assignment is skipped.

---

## 9. Album Assignment Method (Lines 1427-1564)

**Method:** `AssignDownloadedImagesToAlbum(string albumName)`

### Step 1: Get Album from Database (Lines 1434-1441)

```csharp
var album = ServiceLocator.DataStore.GetAlbumByName(albumName);
```

**File:** `Diffusion.Database\DataStore.Album.cs`
**Method:** `GetAlbumByName` (Lines 103-121)

**SQL Query:**
```sql
SELECT * FROM Album WHERE Name = ?
```

Returns the `Album` object with `Id` and `Name`.

### Logging:
```
AssignDownloadedImagesToAlbum: Starting for album 'My Album Name'
AssignDownloadedImagesToAlbum: Found album with ID 174
```

---

### Step 2: Query Civitai Database for New Downloads (Lines 1443-1490)

**THIS IS WHERE THE BUG WAS HAPPENING**

#### Database Path:
```csharp
var civitaiDbPath = Path.Combine(_settings.CivitaiScraperRepositoryPath, "civitai_state.db");
```

#### The Critical Query (Lines 1469-1476):
```csharp
using (var connection = new SQLite.SQLiteConnection(civitaiDbPath, SQLite.SQLiteOpenFlags.ReadOnly))
{
    var query = "SELECT local_path FROM downloads WHERE status = 'completed' AND civitai_id > ?";
    var results = connection.Query<CivitaiDownloadRecord>(query, _lastDownloadId);
}
```

**Database:** `civitai_state.db`
**Table:** `downloads`
**SQL Query:**
```sql
SELECT local_path FROM downloads WHERE status = 'completed' AND civitai_id > ?
```
**Parameter:** `_lastDownloadId` (the value we saved BEFORE download, e.g., 5391)

**What this returns:**
- All rows where `civitai_id > 5391`
- Only returns NEW downloads (those added during this download session)
- Each row contains `local_path` - the full file path to the downloaded image

**Example Results:**
```
civitai_id=5392, local_path=E:\Images\Civitai\image1.png
civitai_id=5393, local_path=E:\Images\Civitai\image2.png
```

**WHY THIS WORKS:**
- `civitai_id` is auto-increment, so new downloads get higher IDs
- Integer comparison (`5392 > 5391`) is simple and reliable
- No timestamp format issues
- No timezone issues

#### Collect Paths (Lines 1478-1489):
```csharp
foreach (var record in results)
{
    if (!string.IsNullOrEmpty(record.local_path))
    {
        downloadedPaths.Add(record.local_path);
    }
}
```

Builds a `List<string>` containing all the file paths.

### Logging:
```
AssignDownloadedImagesToAlbum: Opening Civitai database and querying downloads...
AssignDownloadedImagesToAlbum: Looking for downloads with civitai_id > 5391
AssignDownloadedImagesToAlbum: SQL Query = SELECT local_path FROM downloads WHERE status = 'completed' AND civitai_id > ?
AssignDownloadedImagesToAlbum: Query parameter (last download ID) = 5391
AssignDownloadedImagesToAlbum: Query returned 2 results
AssignDownloadedImagesToAlbum:   - Found path: E:\Images\Civitai\image1.png
AssignDownloadedImagesToAlbum:   - Found path: E:\Images\Civitai\image2.png
AssignDownloadedImagesToAlbum: Total downloaded files to process = 2
```

---

### Step 3: Get Image IDs from Diffusion Toolkit Database (Lines 1501-1526)

**THE CRITICAL PATH MATCHING STEP**

```csharp
var imageIds = ServiceLocator.DataStore.GetImageIdsByPaths(downloadedPaths).ToList();
```

**File:** `Diffusion.Database\DataStore.Image.cs`
**Method:** `GetImageIdsByPaths` (Lines 642-669)

#### How GetImageIdsByPaths Works:

1. Creates a temporary table:
```sql
CREATE TEMP TABLE TempPaths (Path TEXT)
```

2. Inserts all the paths:
```sql
INSERT INTO TempPaths (Path) VALUES (?)
```
For each path in `downloadedPaths` list.

3. Queries for matching Image IDs:
```sql
SELECT Id FROM Image WHERE Path IN (SELECT Path FROM TempPaths)
```

**What this does:**
- Matches `local_path` from Civitai database with `Path` from Diffusion Toolkit's Image table
- Returns the corresponding `Image.Id` values
- These IDs were auto-generated when the scan added the images

**Example:**
```
Input paths:
  E:\Images\Civitai\image1.png
  E:\Images\Civitai\image2.png

Database query finds:
  Id=123456, Path=E:\Images\Civitai\image1.png
  Id=123457, Path=E:\Images\Civitai\image2.png

Returns: [123456, 123457]
```

### Logging (Enhanced logging added recently):
```
AssignDownloadedImagesToAlbum: ========================================
AssignDownloadedImagesToAlbum: Querying Diffusion Toolkit database for image IDs...
AssignDownloadedImagesToAlbum: Looking for 2 paths in Image table
AssignDownloadedImagesToAlbum: Paths being searched:
AssignDownloadedImagesToAlbum:   [1] E:\Images\Civitai\image1.png
AssignDownloadedImagesToAlbum:   [2] E:\Images\Civitai\image2.png
AssignDownloadedImagesToAlbum: Query completed - Found 2 matching image IDs
AssignDownloadedImagesToAlbum: Image IDs returned from query:
AssignDownloadedImagesToAlbum:   [1] Image ID = 123456
AssignDownloadedImagesToAlbum:   [2] Image ID = 123457
AssignDownloadedImagesToAlbum: ========================================
```

**CRITICAL:** Paths must match EXACTLY (character for character) between:
- Civitai database `downloads.local_path`
- Diffusion Toolkit database `Image.Path`

**Potential Issues:**
- If paths don't match, `imageIds` will be empty
- Common causes:
  - Different drive letters
  - Forward slash vs backslash
  - Different casing (though SQLite is case-insensitive on Windows)
  - Download folder not in Diffusion Toolkit's watched folders

---

### Step 4: Insert into AlbumImage Table (Lines 1546-1557)

```csharp
var success = ServiceLocator.DataStore.AddImagesToAlbum(album.Id, imageIds);
```

**File:** `Diffusion.Database\DataStore.Album.cs`
**Method:** `AddImagesToAlbum` (Lines 195-229)

#### How AddImagesToAlbum Works:

```sql
INSERT INTO AlbumImage (AlbumId, ImageId) VALUES (?, ?)
```

For each Image ID, inserts a row with the Album ID and Image ID.

**Example:**
```
Album ID: 174
Image IDs: [123456, 123457]

Inserts:
  (174, 123456)
  (174, 123457)
```

### Logging:
```
AssignDownloadedImagesToAlbum: Matched image IDs: 123456, 123457
AssignDownloadedImagesToAlbum: Adding 2 images to album ID 174...
AssignDownloadedImagesToAlbum: SUCCESS - Assigned 2 images to album 'My Album Name'
```

---

## 10. Final Notification (Lines 1407-1416)

```csharp
ServiceLocator.ToastService.Toast(message, "Civitai Scraper", 5);
```

Shows a toast notification:
```
"Download complete! New images have been scanned and assigned to album 'My Album Name'."
```

---

## Complete Logging Example (Successful Flow)

```
==========================================
LaunchCivitaiScraper: STARTING
==========================================
LaunchCivitaiScraper: Reset _selectedAlbumForDownload to null
LaunchCivitaiScraper: Settings - AlwaysPrompt=True, DefaultAlbum=None
LaunchCivitaiScraper: needsPrompt = True
LaunchCivitaiScraper: Loading albums
LaunchCivitaiScraper: Albums count: 45
LaunchCivitaiScraper: Creating dialog with 45 albums
LaunchCivitaiScraper: Showing dialog
LaunchCivitaiScraper: Selected album: My Album Name
LaunchCivitaiScraper: Getting last download ID before starting...
LaunchCivitaiScraper: Last download ID before starting = 5391
LaunchCivitaiScraper: Python path: E:\...\python.exe
LaunchCivitaiScraper: main.py path: E:\...\main.py
LaunchCivitaiScraper: Working directory: E:\...
LaunchCivitaiScraper: Starting Python process...
LaunchCivitaiScraper: Process started with PID 12345
LaunchCivitaiScraper: Waiting for process to complete...
LaunchCivitaiScraper: Process exited with code 0
LaunchCivitaiScraper: Process completed, calling OnCivitaiScraperCompleted...
======================================
OnCivitaiScraperCompleted: STARTING
OnCivitaiScraperCompleted: Selected album = 'My Album Name'
OnCivitaiScraperCompleted: Last download ID = 5391
======================================
OnCivitaiScraperCompleted: Starting scan for new images
OnCivitaiScraperCompleted: Scan completed successfully
OnCivitaiScraperCompleted: Search refreshed
OnCivitaiScraperCompleted: Checking if should assign to album...
OnCivitaiScraperCompleted: YES - Calling AssignDownloadedImagesToAlbum with album 'My Album Name'
AssignDownloadedImagesToAlbum: Starting for album 'My Album Name'
AssignDownloadedImagesToAlbum: Found album with ID 174
AssignDownloadedImagesToAlbum: Opening Civitai database and querying downloads...
AssignDownloadedImagesToAlbum: Looking for downloads with civitai_id > 5391
AssignDownloadedImagesToAlbum: SQL Query = SELECT local_path FROM downloads WHERE status = 'completed' AND civitai_id > ?
AssignDownloadedImagesToAlbum: Query parameter (last download ID) = 5391
AssignDownloadedImagesToAlbum: Query returned 2 results
AssignDownloadedImagesToAlbum:   - Found path: E:\Images\Civitai\image1.png
AssignDownloadedImagesToAlbum:   - Found path: E:\Images\Civitai\image2.png
AssignDownloadedImagesToAlbum: Total downloaded files to process = 2
AssignDownloadedImagesToAlbum: ========================================
AssignDownloadedImagesToAlbum: Querying Diffusion Toolkit database for image IDs...
AssignDownloadedImagesToAlbum: Looking for 2 paths in Image table
AssignDownloadedImagesToAlbum: Paths being searched:
AssignDownloadedImagesToAlbum:   [1] E:\Images\Civitai\image1.png
AssignDownloadedImagesToAlbum:   [2] E:\Images\Civitai\image2.png
AssignDownloadedImagesToAlbum: Query completed - Found 2 matching image IDs
AssignDownloadedImagesToAlbum: Image IDs returned from query:
AssignDownloadedImagesToAlbum:   [1] Image ID = 123456
AssignDownloadedImagesToAlbum:   [2] Image ID = 123457
AssignDownloadedImagesToAlbum: ========================================
AssignDownloadedImagesToAlbum: Matched image IDs: 123456, 123457
AssignDownloadedImagesToAlbum: Adding 2 images to album ID 174...
AssignDownloadedImagesToAlbum: SUCCESS - Assigned 2 images to album 'My Album Name'
OnCivitaiScraperCompleted: AssignDownloadedImagesToAlbum completed
OnCivitaiScraperCompleted: Showing toast: Download complete! New images have been scanned and assigned to album 'My Album Name'.
OnCivitaiScraperCompleted: COMPLETED SUCCESSFULLY
```

---

## Common Failure Scenarios

### Failure 1: Query Returns ALL Downloads (5391 files)

**Symptom:**
```
AssignDownloadedImagesToAlbum: Query returned 5391 results
```

**Cause:** The `_lastDownloadId` was 0 or the query is broken

**Fix:** Use integer ID comparison (already implemented)

---

### Failure 2: Image IDs Are All Zeros

**Symptom:**
```
AssignDownloadedImagesToAlbum: Matched image IDs: 0, 0, 0, ...
```

**Cause:** `GetImageIdsByPaths` is returning default int values

**Reason:** Paths don't match between databases

**Debug:**
- Check the paths logged in "Paths being searched"
- Manually query Diffusion Toolkit database:
```sql
SELECT Id, Path FROM Image WHERE Path LIKE '%civitai%'
```
- Compare with paths from Civitai database

---

### Failure 3: Foreign Key Constraint Error

**Symptom:**
```
AssignDownloadedImagesToAlbum: ERROR - FOREIGN KEY constraint failed
```

**Cause:** Trying to insert `ImageId=0` which doesn't exist

**Reason:** Same as Failure 2 - paths don't match

---

### Failure 4: No Downloads Found

**Symptom:**
```
AssignDownloadedImagesToAlbum: Query returned 0 results
```

**Causes:**
1. No new downloads (script didn't download anything)
2. `_lastDownloadId` is wrong (captured incorrectly)
3. Downloads are in database but with different status

**Debug:**
- Manually query Civitai database:
```sql
SELECT civitai_id, local_path, status FROM downloads ORDER BY civitai_id DESC LIMIT 10;
```
- Check if there are downloads with `civitai_id > last_id`

---

### Failure 5: No Matching Images

**Symptom:**
```
AssignDownloadedImagesToAlbum: WARNING - No matching images found in database
```

**Causes:**
1. Paths don't match exactly
2. Download folder is not in Diffusion Toolkit's watched folders
3. Scan didn't pick up the images

**Debug Steps:**
1. Check paths from Civitai database (shown in log)
2. Query Diffusion Toolkit database for those specific paths:
```sql
SELECT Id, Path FROM Image WHERE Path = 'E:\Images\Civitai\image1.png'
```
3. If not found, check watched folders in Diffusion Toolkit settings
4. Manually trigger a scan if needed

---

## Key Takeaways

1. **civitai_id is ONLY for filtering** - It's used to identify NEW downloads, not for matching with Image.Id

2. **Path is the ONLY matching key** - `downloads.local_path` must match `Image.Path` exactly

3. **Two separate ID systems:**
   - `civitai_id` in Civitai database (auto-increment, tracks downloads)
   - `Image.Id` in Diffusion Toolkit database (auto-increment, tracks images)
   - These are COMPLETELY INDEPENDENT

4. **The workflow is sequential:**
   - Save last ID → Download → Scan → Query new downloads → Match paths → Assign to album

5. **The scan is critical** - Without the scan, new images won't be in the Image table and path matching will fail

6. **Logging is extensive** - Every step is logged to help debug issues
