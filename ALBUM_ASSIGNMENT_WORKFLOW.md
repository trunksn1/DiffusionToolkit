# Album Assignment Workflow - Technical Documentation

## Overview
This document explains step-by-step how the Civitai album assignment feature works, including database queries, file paths, and the complete workflow.

---

## Complete Workflow

### Step 1: User Clicks "Launch NSFW Civitai Collections Scraper" Button
**Location:** `MainWindow.xaml.cs:1158-1278` (`LaunchCivitaiScraper()` method)

**What happens:**
1. User clicks the button in the left panel
2. The application immediately shows the album selection dialog (BEFORE download starts)

---

### Step 2: Album Selection Dialog
**Location:** `CivitaiAlbumSelectionWindow.xaml.cs`

**User sees:**
- List of existing albums
- Option to create a new album
- Option to skip album assignment
- "Remember my choice" checkbox

**What happens when user clicks OK:**
1. If user selected "Create new album":
   - Validates the album name (line 44-57)
   - Creates new album in database using `ServiceLocator.DataStore.CreateAlbum(newAlbum)` (line 68)
   - Sets `SelectedAlbumName` to the new album name (line 69)

2. If user selected an existing album:
   - Finds the checked RadioButton (line 80-84)
   - Gets the album name from the RadioButton's Tag property (line 83)
   - Sets `SelectedAlbumName` to the selected album name

3. If user selected "Skip":
   - Sets `SelectedAlbumName = null` (line 89)

4. Sets `RememberChoice` based on checkbox state (line 92)
5. Returns `DialogResult = true` (line 93)

---

### Step 3: Store Selected Album and Current Time
**Location:** `MainWindow.xaml.cs:1208-1225`

**What happens:**
```csharp
// Store the selected album name for later use
_selectedAlbumForDownload = albumSelectionWindow.SelectedAlbumName;

// Store the current time to query only new downloads
_downloadStartTime = DateTime.Now;
```

**Important variables:**
- `_selectedAlbumForDownload` (string) - Stores the album name the user selected
- `_downloadStartTime` (DateTime) - Records when download started, used later to query only NEW downloads

---

### Step 4: Launch the Batch Script
**Location:** `MainWindow.xaml.cs:1228-1245`

**Script path:**
```
E:\Clouding\Dropbox\INFORMATICA\GitHub\Diffusion-Toolkit\Diffusion.Toolkit\Scripts\NSFW_CIVITAI_Collections_Scraper.bat
```

**What the script does:**
1. Runs the Civitai Collections Scraper (Python script)
2. Downloads images to configured location
3. Updates the Civitai database (`civitai_state.db`) with download records
4. **CRITICAL:** Creates a `.download_complete` marker file when finished

**Marker file location:**
```
E:\Clouding\Dropbox\INFORMATICA\GitHub\Diffusion-Toolkit\Diffusion.Toolkit\bin\Debug\net10.0-windows\.download_complete
```

---

### Step 5: Wait for Download Completion
**Location:** `MainWindow.xaml.cs:1247-1272`

**What happens:**
```csharp
// Start polling for completion marker file
while (!File.Exists(completionMarkerPath))
{
    await Task.Delay(2000); // Check every 2 seconds
}

// Delete the marker file
File.Delete(completionMarkerPath);
```

The application polls every 2 seconds checking if `.download_complete` exists.

---

### Step 6: Trigger Scan for New Images
**Location:** `MainWindow.xaml.cs:1274-1277`

**What happens:**
```csharp
// Scan for new images
await Task.Run(() => Model?.Scan());
```

This scans the configured paths in Diffusion Toolkit and adds any new images to the **Image** table in the Diffusion Toolkit database.

**Database:** `Diffusion.Toolkit.db` (location depends on user settings)

**What gets inserted into Image table:**
- `Id` (auto-increment primary key)
- `Path` (full file path - THIS IS KEY for matching later)
- `FileName`
- `Hash`
- `CreatedDate`
- Other metadata...

---

### Step 7: Call Album Assignment
**Location:** `MainWindow.xaml.cs:1334-1342`

**What happens:**
```csharp
if (!string.IsNullOrEmpty(_selectedAlbumForDownload))
{
    await AssignDownloadedImagesToAlbum(_selectedAlbumForDownload);
}
```

If user selected an album (not "Skip"), call the assignment method with the album name.

---

### Step 8: Album Assignment Logic (DETAILED)
**Location:** `MainWindow.xaml.cs:1361-1444` (`AssignDownloadedImagesToAlbum()` method)

This is the **core** of the feature. Let's break it down:

#### 8.1 Get Album from Database
**Code:** `MainWindow.xaml.cs:1368-1375`
```csharp
var album = ServiceLocator.DataStore.GetAlbumByName(albumName);
if (album == null)
{
    Logger.Log($"AssignDownloadedImagesToAlbum: Album '{albumName}' not found");
    return;
}
```

**Database method:** `DataStore.Album.cs:103-121`
```csharp
public Album? GetAlbumByName(string name)
{
    using var db = OpenConnection();
    var query = $"SELECT * FROM Album WHERE Name = @Name LIMIT 1";
    var command = db.CreateCommand(query);
    command.Bind("@Name", name);
    var album = command.ExecuteQuery<Album>();

    if (album.Count < 1)
        return null;

    return album[0];
}
```

**What we get:**
- `album.Id` - The Album's primary key (used later to insert into AlbumImage table)
- `album.Name` - The album name

**Database:** `Diffusion.Toolkit.db`
**Table:** `Album`
**Columns:** `Id`, `Name`, `Order`, `LastUpdated`

---

#### 8.2 Query Civitai Database for Downloaded Files
**Code:** `MainWindow.xaml.cs:1377-1406`

**Civitai Database Path:**
```
E:\Clouding\Dropbox\INFORMATICA\GitHub\Civitai Collections Scraper\civitai_state.db
```

**IMPORTANT:** This path is HARDCODED. If your Civitai scraper database is in a different location, this will fail!

**Query:**
```sql
SELECT local_path
FROM downloads
WHERE status = 'completed'
  AND updated_at >= ?
```

**Parameters:**
- `?` = `_downloadStartTime` (the DateTime we stored in Step 3)

**What this returns:**
A list of file paths that were downloaded since the download button was clicked.

**Example results:**
```
E:\AI_Images\Civitai\image1.png
E:\AI_Images\Civitai\image2.png
E:\AI_Images\Civitai\image3.jpg
```

These paths are stored in `downloadedPaths` list.

**Civitai Database Schema (downloads table):**
- `id` - Primary key
- `local_path` - Full path where the image was saved
- `status` - Download status ('completed', 'pending', 'failed', etc.)
- `updated_at` - Timestamp when record was last updated
- Other columns...

---

#### 8.3 Get Image IDs from Diffusion Toolkit Database
**Code:** `MainWindow.xaml.cs:1417`
```csharp
var imageIds = ServiceLocator.DataStore.GetImageIdsByPaths(downloadedPaths).ToList();
```

**Database method:** `DataStore.Image.cs:642-669`
```csharp
public IEnumerable<int> GetImageIdsByPaths(IEnumerable<string> paths)
{
    var pathsList = paths.ToList();
    if (!pathsList.Any())
        return Enumerable.Empty<int>();

    using var db = OpenConnection();

    lock (_lock)
    {
        // Create a temp table with the paths
        db.Execute("DROP TABLE IF EXISTS TempPaths");
        db.Execute("CREATE TEMP TABLE TempPaths (Path TEXT)");

        // Insert paths into temp table
        foreach (var path in pathsList)
        {
            db.Execute("INSERT INTO TempPaths (Path) VALUES (?)", path);
        }

        // Query for matching image IDs
        var imageIds = db.Query<int>(
            "SELECT Id FROM Image WHERE Path IN (SELECT Path FROM TempPaths)"
        );

        return imageIds;
    }
}
```

**What this does:**
1. Creates a temporary table `TempPaths`
2. Inserts all downloaded file paths into the temp table
3. Queries the `Image` table to find IDs where `Path` matches any path in `TempPaths`
4. Returns a list of Image IDs

**Example:**
- Input: `["E:\AI_Images\Civitai\image1.png", "E:\AI_Images\Civitai\image2.png"]`
- Output: `[5001, 5002]` (the Image IDs from the Image table)

**THIS IS THE CRITICAL MATCHING STEP:**
- The paths in the Civitai database must EXACTLY match the paths in the Diffusion Toolkit Image table
- If paths don't match (different drives, different folders, case sensitivity), no matches will be found

---

#### 8.4 Add Images to Album
**Code:** `MainWindow.xaml.cs:1428`
```csharp
var success = ServiceLocator.DataStore.AddImagesToAlbum(album.Id, imageIds);
```

**Database method:** `DataStore.Album.cs:195-229`
```csharp
public bool AddImagesToAlbum(int albumId, IEnumerable<int> imageIds)
{
    // Check album exists
    if (GetAlbum(albumId) == null)
        return false;

    using var db = OpenConnection();

    lock (_lock)
    {
        db.BeginTransaction();

        // Create temp table with image IDs
        var selectedIds = InsertIds(db, "SelectedIds", imageIds);

        // Insert into AlbumImage table
        var query = $"INSERT OR IGNORE INTO AlbumImage (AlbumId, ImageId) " +
                    $"SELECT @AlbumId, Id FROM {selectedIds}";

        var command = db.CreateCommand(query);
        command.Bind("@AlbumId", albumId);
        command.ExecuteNonQuery();

        // Update album's LastUpdated timestamp
        query = $"UPDATE Album SET LastUpdated = @LastUpdated WHERE Id = @Id";
        command = db.CreateCommand(query);
        command.Bind("@LastUpdated", DateTime.Now);
        command.Bind("@Id", albumId);
        command.ExecuteNonQuery();

        db.Commit();
    }

    return true;
}
```

**What this does:**
1. Verifies the album exists
2. Creates a temp table with the image IDs
3. **Inserts into AlbumImage table:** Creates one row for each (AlbumId, ImageId) pair
4. Updates the album's LastUpdated timestamp
5. Commits the transaction

**AlbumImage table schema:**
- `AlbumId` (int) - Foreign key to Album.Id
- `ImageId` (int) - Foreign key to Image.Id

**Example insert:**
If `album.Id = 3` and `imageIds = [5001, 5002, 5003]`, this creates:
```
AlbumId | ImageId
--------|--------
3       | 5001
3       | 5002
3       | 5003
```

---

## Summary of Database Tables and Relationships

### Diffusion Toolkit Database (`Diffusion.Toolkit.db`)

**Album Table:**
```
Id (PK) | Name          | Order | LastUpdated
--------|---------------|-------|------------------
1       | My Favorites  | 0     | 2025-01-09 10:00
2       | Landscapes    | 1     | 2025-01-09 11:00
3       | Civitai Pics  | 2     | 2025-01-09 12:00
```

**Image Table:**
```
Id (PK) | Path                           | FileName    | Hash      | CreatedDate
--------|--------------------------------|-------------|-----------|------------------
5001    | E:\AI_Images\Civitai\img1.png | img1.png    | abc123... | 2025-01-09 12:00
5002    | E:\AI_Images\Civitai\img2.png | img2.png    | def456... | 2025-01-09 12:01
5003    | E:\AI_Images\Civitai\img3.jpg | img3.jpg    | ghi789... | 2025-01-09 12:02
```

**AlbumImage Table (Junction Table):**
```
AlbumId (FK) | ImageId (FK)
-------------|-------------
3            | 5001
3            | 5002
3            | 5003
```

This creates the many-to-many relationship: Album "Civitai Pics" contains Images 5001, 5002, 5003.

---

### Civitai Database (`civitai_state.db`)

**downloads Table:**
```
id | local_path                     | status    | updated_at
---|--------------------------------|-----------|------------------
1  | E:\AI_Images\Civitai\img1.png | completed | 2025-01-09 12:00
2  | E:\AI_Images\Civitai\img2.png | completed | 2025-01-09 12:01
3  | E:\AI_Images\Civitai\img3.jpg | completed | 2025-01-09 12:02
```

---

## Potential Failure Points and Debugging

### Issue 1: Civitai Database Not Found
**Symptom:** Log shows "Civitai database not found at..."

**Cause:** Hardcoded path doesn't match actual location

**Check:** `MainWindow.xaml.cs:1378-1387`
```csharp
var civitaiDbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "Dropbox", "INFORMATICA", "GitHub", "Civitai Collections Scraper", "civitai_state.db"
);
```

**Solution:** Update the hardcoded path to match your actual Civitai database location.

---

### Issue 2: No Downloads Found in Civitai Database
**Symptom:** Log shows "Found 0 downloaded files"

**Possible causes:**
1. `_downloadStartTime` is incorrect (set too late)
2. Download script didn't update `updated_at` timestamp
3. Download `status` is not 'completed'
4. No records in `downloads` table

**Debug:**
Query the Civitai database manually:
```sql
SELECT local_path, status, updated_at
FROM downloads
WHERE status = 'completed'
ORDER BY updated_at DESC
LIMIT 10;
```

Check if `updated_at` values are recent and match expected download time.

---

### Issue 3: Path Mismatch - No Images Found in Diffusion Toolkit Database
**Symptom:** Log shows "Found X downloaded files" but "Found 0 matching images in database"

**Cause:** Paths in Civitai database don't match paths in Image table

**Common reasons:**
1. **Case sensitivity:** `E:\AI_Images` vs `e:\ai_images`
2. **Path separators:** `E:\AI_Images\file.png` vs `E:/AI_Images/file.png`
3. **Different base directories:** Civitai saves to `D:\Downloads` but Diffusion scans `E:\AI_Images`
4. **Scan hasn't run yet:** Images downloaded but not yet added to Image table

**Debug:**
1. Check Civitai database paths:
```sql
SELECT local_path FROM downloads WHERE status = 'completed' LIMIT 5;
```

2. Check Diffusion Toolkit Image table paths:
```sql
SELECT Path FROM Image ORDER BY CreatedDate DESC LIMIT 5;
```

3. Compare paths - they must match EXACTLY (character by character)

**Solution:**
Ensure Civitai scraper saves images to a folder that Diffusion Toolkit scans.

---

### Issue 4: Scan Didn't Add New Images
**Symptom:** Images were downloaded but not in Image table

**Cause:** Diffusion Toolkit scan doesn't include the download folder

**Debug:**
Check Diffusion Toolkit settings for scanned paths.

**Solution:**
Add the Civitai download folder to Diffusion Toolkit's scan paths.

---

### Issue 5: AlbumImage Entries Not Created
**Symptom:** Images exist, IDs found, but no entries in AlbumImage table

**Possible causes:**
1. `AddImagesToAlbum` returned false (album doesn't exist)
2. Database transaction failed
3. Database locked

**Debug:**
Check logs for success/failure message:
```
"Successfully assigned X images to album 'Y'"
```

Query AlbumImage table:
```sql
SELECT * FROM AlbumImage WHERE AlbumId = ?;
```

---

## How to Verify Everything Works

### Step 1: Check Logs
Logs are written throughout the process. Check for these messages:

```
AssignDownloadedImagesToAlbum: Starting for album 'X'
AssignDownloadedImagesToAlbum: Found album with ID Y
AssignDownloadedImagesToAlbum: Reading from Civitai database
AssignDownloadedImagesToAlbum: Found N downloaded files
AssignDownloadedImagesToAlbum: Found M matching images in database
AssignDownloadedImagesToAlbum: Successfully assigned M images to album 'X'
```

### Step 2: Query Civitai Database
```sql
-- Check recent downloads
SELECT id, local_path, status, datetime(updated_at, 'unixepoch') as download_time
FROM downloads
WHERE status = 'completed'
ORDER BY updated_at DESC
LIMIT 10;
```

### Step 3: Query Diffusion Toolkit Image Table
```sql
-- Check if downloaded images exist in Image table
SELECT Id, Path, FileName, CreatedDate
FROM Image
WHERE Path LIKE '%civitai%'  -- Adjust based on your paths
ORDER BY CreatedDate DESC
LIMIT 10;
```

### Step 4: Query AlbumImage Table
```sql
-- Check if images were assigned to album
SELECT ai.AlbumId, ai.ImageId, a.Name as AlbumName, i.Path as ImagePath
FROM AlbumImage ai
JOIN Album a ON ai.AlbumId = a.Id
JOIN Image i ON ai.ImageId = i.Id
WHERE a.Name = 'YourAlbumName'  -- Replace with your album name
ORDER BY ai.ImageId DESC
LIMIT 10;
```

---

## Key Code Locations Reference

| Functionality | File | Lines |
|--------------|------|-------|
| Launch Civitai Scraper button handler | `MainWindow.xaml.cs` | 1158-1278 |
| Album selection dialog | `CivitaiAlbumSelectionWindow.xaml.cs` | 39-95 |
| Store album name and start time | `MainWindow.xaml.cs` | 1208-1225 |
| Wait for completion | `MainWindow.xaml.cs` | 1247-1272 |
| Trigger scan | `MainWindow.xaml.cs` | 1274-1277 |
| Album assignment orchestration | `MainWindow.xaml.cs` | 1334-1358 |
| Album assignment logic | `MainWindow.xaml.cs` | 1361-1444 |
| GetAlbumByName | `DataStore.Album.cs` | 103-121 |
| GetImageIdsByPaths | `DataStore.Image.cs` | 642-669 |
| AddImagesToAlbum | `DataStore.Album.cs` | 195-229 |

---

## Recommendations

1. **Add configurable path for Civitai database** - Don't hardcode the path
2. **Add path normalization** - Convert all paths to same format before comparing
3. **Add detailed error messages** - Show user why assignment failed
4. **Add retry logic** - Sometimes scan needs time to index files
5. **Add manual assignment option** - Let users manually trigger assignment
6. **Add progress feedback** - Show user how many images were assigned

---

## Example End-to-End Scenario

**Given:**
- User has album "My Civitai Collection" (Id = 5) in Diffusion Toolkit
- Civitai scraper saves to `E:\AI_Images\Civitai\`
- Diffusion Toolkit scans `E:\AI_Images\`

**When:**
1. User clicks "Launch NSFW Civitai Collections Scraper"
2. Dialog appears, user selects "My Civitai Collection"
3. System stores: `_selectedAlbumForDownload = "My Civitai Collection"`, `_downloadStartTime = 2025-01-09 12:00:00`
4. Batch script runs, downloads 3 images:
   - `E:\AI_Images\Civitai\artwork_001.png`
   - `E:\AI_Images\Civitai\artwork_002.png`
   - `E:\AI_Images\Civitai\artwork_003.jpg`
5. Script updates `civitai_state.db` downloads table with these paths and `status = 'completed'`, `updated_at = 12:05:00`
6. Script creates `.download_complete` marker
7. Diffusion Toolkit detects marker, triggers scan
8. Scan finds 3 new images, inserts into Image table:
   - Id=1001, Path=`E:\AI_Images\Civitai\artwork_001.png`
   - Id=1002, Path=`E:\AI_Images\Civitai\artwork_002.png`
   - Id=1003, Path=`E:\AI_Images\Civitai\artwork_003.jpg`
9. `AssignDownloadedImagesToAlbum("My Civitai Collection")` runs:
   - Gets Album: `{Id=5, Name="My Civitai Collection"}`
   - Queries Civitai DB: Gets 3 paths
   - Queries Image table: Gets `[1001, 1002, 1003]`
   - Inserts into AlbumImage:
     - `(5, 1001)`
     - `(5, 1002)`
     - `(5, 1003)`
10. User sees "Download complete! New images have been scanned and assigned to album 'My Civitai Collection'."
11. User opens "My Civitai Collection" album and sees 3 new images

**Result:** Success!
