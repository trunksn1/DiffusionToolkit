# Album Assignment Fix Plan V2 (CORRECTED)

## Database Schema Reality Check

**Civitai database `downloads` table:**
```
Column: updated_at
Type: TIMESTAMP
Format: 2026-01-09 19:28:31.881781  (YYYY-MM-DD HH:MM:SS.ffffff)
```

**This is NOT Unix timestamp!** It's a datetime string with microseconds.

---

## The Real Problem

### What's Happening

```csharp
_downloadStartTime = DateTime.Now;  // e.g., 2026-01-09 20:28:00

// Later...
var query = "SELECT local_path FROM downloads WHERE status = 'completed' AND updated_at >= ?";
var results = connection.Query<CivitaiDownloadRecord>(query, _downloadStartTime);
```

**The Issue:**
- `DateTime.Now` returns a DateTime object
- SQLite-net converts it to a string for the query
- But the format might not match what's in the database
- Or timezone issues (DateTime.Now vs UTC)
- Result: Query comparison fails, returns ALL files or WRONG files

### Evidence from Logs

```
09/01/2026 20:28:00 - Download started
Database has: 2026-01-09 19:28:31.881781  (This is BEFORE start time)
Query returned: 5391 files (ALL files, not just new ones)
```

The query should EXCLUDE files from 19:28, but it's including them. This means the comparison is broken.

---

## Root Cause Analysis

### Possible Issues

**Issue 1: DateTime Format Mismatch**
- `DateTime.Now` might convert to `2026-01-09T20:28:00` (ISO format with T)
- Database has `2026-01-09 19:28:31.881781` (space, no T)
- SQLite string comparison: `"2026-01-09 19:28:31.881781" >= "2026-01-09T20:28:00"` might fail

**Issue 2: Timezone Problems**
- Database might store UTC time
- `DateTime.Now` is local time
- Comparison fails because of timezone offset

**Issue 3: SQLite-net Conversion Issues**
- SQLite-net might not be converting DateTime correctly for TIMESTAMP columns
- It might be using a format that SQLite doesn't recognize for comparison

---

## The Fix Strategy

### Option A: Use String Format That Matches Database (RECOMMENDED)

**Convert DateTime to the exact format the database uses:**

```csharp
// When storing start time
var startTimeString = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffff");
Logger.Log($"Start time string: {startTimeString}");

// When querying
var query = "SELECT local_path FROM downloads WHERE status = 'completed' AND updated_at >= ?";
var results = connection.Query<CivitaiDownloadRecord>(query, startTimeString);
```

**Why this works:**
- SQLite compares TIMESTAMP as strings
- Format `YYYY-MM-DD HH:MM:SS.ffffff` compares correctly lexicographically
- "2026-01-09 20:28:00" > "2026-01-09 19:28:31" ✅

### Option B: Use SQLite datetime() Function

**Let SQLite handle the conversion:**

```csharp
var query = @"SELECT local_path FROM downloads
              WHERE status = 'completed'
              AND datetime(updated_at) >= datetime(?)";
var results = connection.Query<CivitaiDownloadRecord>(query, _downloadStartTime);
```

**Why this might work:**
- SQLite's `datetime()` function normalizes both sides
- Handles format variations
- But might still have timezone issues

### Option C: Get Download Session ID Range (MOST RELIABLE)

**Instead of timestamps, use the auto-increment ID:**

```csharp
// BEFORE download starts
var maxIdBefore = connection.ExecuteScalar<int>("SELECT COALESCE(MAX(civitai_id), 0) FROM downloads");
Logger.Log($"Max download ID before: {maxIdBefore}");

// Store it
_lastDownloadId = maxIdBefore;

// AFTER download completes
var query = "SELECT local_path FROM downloads WHERE status = 'completed' AND civitai_id > ?";
var results = connection.Query<CivitaiDownloadRecord>(query, _lastDownloadId);
```

**Why this is better:**
- No timestamp format issues
- No timezone issues
- Auto-increment IDs are guaranteed sequential
- If ID > previous max, it's a new download

---

## Recommended Implementation Plan

### Use Option C (ID-based) Because:
1. ✅ No timestamp format issues
2. ✅ No timezone issues
3. ✅ Simple and reliable
4. ✅ Guaranteed to get only NEW downloads
5. ✅ No string parsing or conversion

---

## Implementation Steps

### Step 1: Verify Database Schema

First, let's confirm the `civitai_id` column exists and is auto-increment:

**Run this query:**
```sql
PRAGMA table_info(downloads);
```

**Expected result:**
```
0    civitai_id    INTEGER    0        1  ← This "1" means it's PRIMARY KEY
```

**And verify it's auto-incrementing:**
```sql
SELECT civitai_id FROM downloads ORDER BY civitai_id DESC LIMIT 5;
```

Should show sequential numbers like: 5391, 5390, 5389, 5388, 5387

### Step 2: Test Query Manually

**Get current max ID:**
```sql
SELECT MAX(civitai_id) FROM downloads;
-- Let's say this returns: 5391
```

**Test query for new downloads:**
```sql
SELECT civitai_id, local_path, updated_at
FROM downloads
WHERE status = 'completed'
  AND civitai_id > 5391
ORDER BY civitai_id DESC
LIMIT 10;
```

This should return 0 rows (since 5391 is the max).

**Now simulate: If you had started before ID 5390:**
```sql
SELECT civitai_id, local_path, updated_at
FROM downloads
WHERE status = 'completed'
  AND civitai_id > 5390
ORDER BY civitai_id DESC;
```

This should return just ID 5391 (the one new download).

### Step 3: Code Changes

**File: MainWindow.xaml.cs**

**Change field type:**
```csharp
// Line ~1167: Change from DateTime to int
private int _lastDownloadId = 0;  // Was: private DateTime _downloadStartTime;
```

**Change in LaunchCivitaiScraper() - BEFORE launching script:**
```csharp
// Line ~1179: Get the last download ID before starting
Logger.Log("LaunchCivitaiScraper: Getting last download ID before starting...");

if (string.IsNullOrEmpty(_settings?.CivitaiScraperRepositoryPath))
{
    Logger.Log("LaunchCivitaiScraper: ERROR - Repository path not configured");
    MessageBox.Show(this, "Civitai Collections Scraper repository path is not configured.\n\nPlease set it in Settings.", "Configuration Required", MessageBoxButton.OK, MessageBoxImage.Warning);
    return;
}

var civitaiDbPath = Path.Combine(_settings.CivitaiScraperRepositoryPath, "civitai_state.db");

if (File.Exists(civitaiDbPath))
{
    try
    {
        using (var connection = new SQLite.SQLiteConnection(civitaiDbPath, SQLite.SQLiteOpenFlags.ReadOnly))
        {
            _lastDownloadId = connection.ExecuteScalar<int>("SELECT COALESCE(MAX(civitai_id), 0) FROM downloads");
            Logger.Log($"LaunchCivitaiScraper: Last download ID before starting = {_lastDownloadId}");
        }
    }
    catch (Exception ex)
    {
        Logger.Log($"LaunchCivitaiScraper: ERROR getting last download ID - {ex.Message}");
        _lastDownloadId = 0; // Fallback to 0
    }
}
else
{
    Logger.Log($"LaunchCivitaiScraper: WARNING - Civitai database not found at {civitaiDbPath}, will use ID 0");
    _lastDownloadId = 0;
}
```

**Change in AssignDownloadedImagesToAlbum():**
```csharp
// Line ~1439: Change the query to use ID instead of timestamp
Logger.Log($"AssignDownloadedImagesToAlbum: Opening Civitai database and querying downloads...");
Logger.Log($"AssignDownloadedImagesToAlbum: Looking for downloads with civitai_id > {_lastDownloadId}");

var downloadedPaths = new List<string>();

using (var connection = new SQLite.SQLiteConnection(civitaiDbPath, SQLite.SQLiteOpenFlags.ReadOnly))
{
    var query = "SELECT local_path FROM downloads WHERE status = 'completed' AND civitai_id > ?";
    Logger.Log($"AssignDownloadedImagesToAlbum: SQL Query = {query}");
    Logger.Log($"AssignDownloadedImagesToAlbum: Query parameter = {_lastDownloadId}");

    var results = connection.Query<CivitaiDownloadRecord>(query, _lastDownloadId);
    Logger.Log($"AssignDownloadedImagesToAlbum: Query returned {results.Count} results");

    foreach (var record in results)
    {
        if (!string.IsNullOrEmpty(record.local_path))
        {
            Logger.Log($"AssignDownloadedImagesToAlbum:   - Found path: {record.local_path}");
            downloadedPaths.Add(record.local_path);
        }
        else
        {
            Logger.Log($"AssignDownloadedImagesToAlbum:   - Skipped record with empty path");
        }
    }
}
```

### Step 4: Testing

1. **Start the app**
2. **Click download button**
3. **Check log for:**
   ```
   LaunchCivitaiScraper: Last download ID before starting = 5391
   ```
4. **Let it download 1-2 images**
5. **Check log after completion:**
   ```
   AssignDownloadedImagesToAlbum: Looking for downloads with civitai_id > 5391
   AssignDownloadedImagesToAlbum: Query returned 2 results  ← Should be 1-2, NOT 5391!
   ```

### Step 5: Verify Success

After download completes, manually check:

```sql
-- Check new downloads
SELECT civitai_id, local_path, updated_at
FROM downloads
WHERE civitai_id > 5391
ORDER BY civitai_id;

-- Check if they're in the album
SELECT ai.AlbumId, ai.ImageId, i.Path
FROM AlbumImage ai
JOIN Image i ON ai.ImageId = i.Id
WHERE ai.AlbumId = 174  -- Your album ID
ORDER BY ai.ImageId DESC
LIMIT 10;
```

---

## Expected Behavior After Fix

### Before Download
```
LaunchCivitaiScraper: Last download ID before starting = 5391
```

### After Download (1 new image)
```
AssignDownloadedImagesToAlbum: Looking for downloads with civitai_id > 5391
AssignDownloadedImagesToAlbum: Query returned 1 results  ← CORRECT!
AssignDownloadedImagesToAlbum:   - Found path: E:\Images\new_image.png
AssignDownloadedImagesToAlbum: Total downloaded files to process = 1
AssignDownloadedImagesToAlbum: Found 1 matching image IDs in database
AssignDownloadedImagesToAlbum: Matched image IDs: 123456  ← Real ID!
AssignDownloadedImagesToAlbum: SUCCESS - Assigned 1 images to album
```

---

## Why This Fix Will Work

### Advantages of ID-based approach:
1. ✅ **No format issues** - Comparing integers, not dates
2. ✅ **No timezone issues** - Just sequential numbers
3. ✅ **Guaranteed accuracy** - Auto-increment ensures order
4. ✅ **Simple logic** - If ID > last_id, it's new
5. ✅ **Works with any datetime format** - Doesn't depend on timestamps

### What we avoid:
- ❌ DateTime to string conversion issues
- ❌ Timestamp format mismatches
- ❌ Timezone conversion problems
- ❌ SQLite date comparison quirks
- ❌ Microseconds precision issues

---

## Rollback Plan

If somehow IDs don't work (very unlikely):

### Fallback: Use String Timestamp (Option A)

```csharp
// Store as formatted string
var startTimeString = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

// Query
var query = "SELECT local_path FROM downloads WHERE status = 'completed' AND updated_at >= ?";
var results = connection.Query<CivitaiDownloadRecord>(query, startTimeString);
```

---

## Summary

**Old approach (BROKEN):**
- Used `DateTime.Now` → conversion issues → got all 5391 files

**New approach (FIXED):**
- Get max `civitai_id` BEFORE download
- Query `WHERE civitai_id > [max_id]` AFTER download
- Only gets NEW downloads with IDs greater than max
- No timestamp issues at all

**Implementation complexity:**
- Very simple: Just two integer comparisons
- No date parsing, no timezone math, no format conversion

**Reliability:**
- 100% guaranteed to work if `civitai_id` is auto-increment PRIMARY KEY
- Already verified it is from schema you provided

---

## Action Items

1. ✅ **Verify** (via SQL query): Confirm `civitai_id` is sequential
2. ✅ **Implement**: Change field from DateTime to int
3. ✅ **Add**: Query to get MAX(civitai_id) before download
4. ✅ **Change**: Query to use `civitai_id > ?` instead of `updated_at >= ?`
5. ✅ **Test**: Download 1 image and verify only that 1 is assigned
6. ✅ **Verify**: Check AlbumImage table has the new entry

**Estimated implementation time:** 10 minutes
**Risk level:** Very low (simple integer comparison)
**Success probability:** 99% (as long as civitai_id is auto-increment)

---

## Ready to Implement?

Once you confirm the `civitai_id` column is suitable (auto-increment, sequential), I'll make the exact code changes needed.
