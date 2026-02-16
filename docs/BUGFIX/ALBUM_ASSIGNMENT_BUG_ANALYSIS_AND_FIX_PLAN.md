# Album Assignment Bug Analysis and Fix Plan

## Current Problem

The album assignment feature is broken. Here's what's happening:

### What the Log Shows

```
AssignDownloadedImagesToAlbum: Total downloaded files to process = 5391
AssignDownloadedImagesToAlbum: Found 5380 matching image IDs in database
AssignDownloadedImagesToAlbum: Matched image IDs: 0, 0, 0, 0, 0, 0, 0, 0, ...
AssignDownloadedImagesToAlbum: ERROR - FOREIGN KEY constraint failed
```

### What This Means

1. **Query returned ALL downloads (5391 files)** - Not just the 1 new file you downloaded
2. **Image IDs are all ZERO** - This is wrong, IDs should be positive integers
3. **Foreign key error** - Can't insert (AlbumId=174, ImageId=0) because Image with Id=0 doesn't exist

---

## Root Cause Analysis

### Problem 1: Timestamp Comparison Issue

**The Code:**
```csharp
_downloadStartTime = DateTime.Now;  // Stores as .NET DateTime
// Later...
var query = "SELECT local_path FROM downloads WHERE status = 'completed' AND updated_at >= ?";
var results = connection.Query<CivitaiDownloadRecord>(query, _downloadStartTime);
```

**The Issue:**
- Civitai database `updated_at` field is stored as **Unix timestamp** (integer seconds since 1970)
- We're passing a **DateTime object** to the query
- SQLite-net is converting it incorrectly, probably to a string or wrong format
- The comparison fails, so it returns ALL completed downloads instead of just new ones

**Example:**
- `_downloadStartTime` = `2026-01-09 20:28:00` (DateTime)
- `updated_at` in database = `1736454480` (Unix timestamp for same time)
- Query compares: `1736454480 >= "2026-01-09 20:28:00"` ❌ WRONG comparison type

### Problem 2: Image IDs Are Zero

**Why are the IDs zero?**

Two possibilities:

**Possibility A: GetImageIdsByPaths is broken**
```csharp
var imageIds = db.Query<int>("SELECT Id FROM Image WHERE Path IN (SELECT Path FROM TempPaths)");
```
- If this query returns 5380 rows but they're all 0, something is very wrong
- Image.Id is auto-increment PRIMARY KEY, should never be 0

**Possibility B: Path mismatch but method returns default values**
- If paths don't match, query returns 0 rows
- But log says "Found 5380 matching image IDs"
- So the query IS finding matches, but the IDs are wrong

**Most Likely:** The query is returning empty result sets that get converted to default int (0)

---

## Why This Happened

### Original Design Flaw

The original plan was:
1. Record download start time as `DateTime.Now`
2. Query Civitai DB for downloads after that time
3. Get paths of newly downloaded images
4. Look up those paths in Diffusion Toolkit DB to get Image IDs
5. Assign those Image IDs to the album

**The flaw:** Comparing DateTime with Unix timestamp doesn't work correctly.

### What Should Have Been Done

1. Record download start time as **Unix timestamp** (integer)
2. Query Civitai DB: `WHERE updated_at >= [unix_timestamp]`
3. This gives us ONLY the newly downloaded files
4. Rest of the flow is correct

---

## The Fix Plan

### Step 1: Fix Timestamp Storage and Comparison

**Change in MainWindow.xaml.cs - LaunchCivitaiScraper()**

**OLD CODE:**
```csharp
_downloadStartTime = DateTime.Now;
```

**NEW CODE:**
```csharp
private long _downloadStartTimeUnix;  // Change field type from DateTime to long

// In LaunchCivitaiScraper():
_downloadStartTimeUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
```

**Change in AssignDownloadedImagesToAlbum()**

**OLD CODE:**
```csharp
var query = "SELECT local_path FROM downloads WHERE status = 'completed' AND updated_at >= ?";
var results = connection.Query<CivitaiDownloadRecord>(query, _downloadStartTime);
```

**NEW CODE:**
```csharp
var query = "SELECT local_path FROM downloads WHERE status = 'completed' AND updated_at >= ?";
var results = connection.Query<CivitaiDownloadRecord>(query, _downloadStartTimeUnix);
```

### Step 2: Add Debugging for Image ID Issue

Add logging to see what's actually being returned:

**In GetImageIdsByPaths (DataStore.Image.cs:642-669):**

Add logging before and after the query:
```csharp
// Log sample paths being searched
Logger.Log($"GetImageIdsByPaths: Searching for {pathsList.Count} paths");
Logger.Log($"GetImageIdsByPaths: Sample path 1: {pathsList[0]}");

// Query
var imageIds = db.Query<int>("SELECT Id FROM Image WHERE Path IN (SELECT Path FROM TempPaths)");

// Log results
Logger.Log($"GetImageIdsByPaths: Query returned {imageIds.Count} IDs");
if (imageIds.Count > 0)
{
    Logger.Log($"GetImageIdsByPaths: First 5 IDs: {string.Join(", ", imageIds.Take(5))}");
}
```

### Step 3: Verify Civitai Database Schema

Before implementing, we should verify the actual column name and type in the Civitai database:

**Query to run manually:**
```sql
-- Check the downloads table schema
PRAGMA table_info(downloads);

-- Check sample data
SELECT id, local_path, status, updated_at, datetime(updated_at, 'unixepoch') as readable_time
FROM downloads
ORDER BY updated_at DESC
LIMIT 5;
```

This will confirm:
- Column name is actually `updated_at`
- It's stored as Unix timestamp
- What the actual values look like

### Step 4: Test Query Manually

Before changing code, test the query manually:

```sql
-- Current Unix timestamp (use this as test value)
SELECT strftime('%s', 'now');  -- Returns current time as Unix timestamp

-- Test query with Unix timestamp
SELECT local_path, updated_at, datetime(updated_at, 'unixepoch') as readable_time
FROM downloads
WHERE status = 'completed'
  AND updated_at >= 1736454480  -- Replace with actual start time
LIMIT 10;
```

This should return only files downloaded after your start time.

---

## Implementation Steps (In Order)

### Phase 1: Investigation (Do this FIRST)
1. ✅ Open Civitai database: `E:\Clouding\Dropbox\INFORMATICA\GitHub\Civitai Collections Scraper\civitai_state.db`
2. ✅ Run: `PRAGMA table_info(downloads);` to see schema
3. ✅ Run: `SELECT * FROM downloads ORDER BY updated_at DESC LIMIT 5;` to see data format
4. ✅ Verify `updated_at` is indeed Unix timestamp
5. ✅ Get the actual Unix timestamp from when you started the download
6. ✅ Test the query manually with correct Unix timestamp

### Phase 2: Code Changes (Only after Phase 1 confirms the issue)

**File 1: MainWindow.xaml.cs**

Change the field type:
```csharp
// Line ~1167: Change from DateTime to long
private long _downloadStartTimeUnix;  // Was: private DateTime _downloadStartTime;
```

Change where we set it:
```csharp
// Line ~1179: Record as Unix timestamp
_downloadStartTimeUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
Logger.Log($"LaunchCivitaiScraper: Recorded download start time (Unix) = {_downloadStartTimeUnix}");
```

Change the query:
```csharp
// Line ~1443: Use Unix timestamp in query
var results = connection.Query<CivitaiDownloadRecord>(query, _downloadStartTimeUnix);
```

**File 2: DataStore.Image.cs** (Optional - for debugging)

Add logging to GetImageIdsByPaths method around line 642.

### Phase 3: Testing

1. Set the Civitai Scraper repository path in Settings
2. Click the download button
3. Let it download 1-2 images (don't let it download 5000!)
4. Check the log for:
   - Unix timestamp when download started
   - How many files the query returned (should be 1-2, not 5391)
   - The actual paths found
   - The image IDs (should NOT be zeros)
   - Success message

### Phase 4: Verify in Database

After the download completes, verify:
```sql
-- Check AlbumImage table
SELECT COUNT(*) FROM AlbumImage WHERE AlbumId = 174;  -- Should have your new images

-- Check the actual entries
SELECT ai.*, i.Path
FROM AlbumImage ai
JOIN Image i ON ai.ImageId = i.Id
WHERE ai.AlbumId = 174
ORDER BY i.Id DESC
LIMIT 10;
```

---

## Expected Behavior After Fix

### Before Download
```
LaunchCivitaiScraper: Recorded download start time (Unix) = 1736454480
```

### After Download
```
AssignDownloadedImagesToAlbum: Query returned 1 results  ← Should be 1, not 5391!
AssignDownloadedImagesToAlbum:   - Found path: E:\Images\new_image.png
AssignDownloadedImagesToAlbum: Total downloaded files to process = 1  ← Correct!
AssignDownloadedImagesToAlbum: Found 1 matching image IDs in database
AssignDownloadedImagesToAlbum: Matched image IDs: 123456  ← Real ID, not zero!
AssignDownloadedImagesToAlbum: SUCCESS - Assigned 1 images to album 'My Album'
```

---

## Why We Can't Just "Wing It"

### Previous Attempts Failed Because:
1. Didn't verify database format first
2. Made assumptions about timestamp storage
3. Didn't test queries manually before implementing
4. Removed batch file mechanism without understanding the full flow

### This Time We Need To:
1. ✅ **Investigate first** - Check actual database schema
2. ✅ **Test manually** - Verify queries work before coding
3. ✅ **Small changes** - Fix ONE thing at a time
4. ✅ **Verify each step** - Confirm each fix works before moving on

---

## Questions to Answer Before Implementing

1. **What is the exact column name in the downloads table?**
   - Run: `PRAGMA table_info(downloads);`
   - Confirm it's `updated_at` not `updated_date` or `timestamp`

2. **What format is updated_at stored in?**
   - Run: `SELECT updated_at FROM downloads LIMIT 1;`
   - Should be like: `1736454480` (integer)

3. **What is the current Unix timestamp?**
   - Run: `SELECT strftime('%s', 'now');`
   - Use this to test queries

4. **Does the manual query work?**
   - Test with known Unix timestamp
   - Should return only recent downloads

---

## Rollback Plan (If This Doesn't Work)

If after implementing the Unix timestamp fix, it still doesn't work:

### Alternative Approach: Don't Use Timestamps At All

Instead of querying by time, query by download session:

1. Before download: Get current max download ID: `SELECT MAX(id) FROM downloads`
2. After download: Query for downloads with ID > max: `SELECT * FROM downloads WHERE id > [max_id]`
3. This guarantees we only get NEW downloads

This is more reliable because:
- No timestamp conversion issues
- No timezone issues
- Sequential IDs are guaranteed to be in order

---

## Current State

❌ **Broken** - Timestamp comparison fails, returns all 5391 downloads
❌ **Broken** - Image IDs are all zeros
❌ **Broken** - Foreign key error when trying to insert

## Next Steps

**DO NOT IMPLEMENT CODE CHANGES YET!**

**First, investigate:**
1. Open the Civitai database
2. Check the schema
3. Look at sample data
4. Test queries manually
5. Report findings

**Then, I'll write the exact code changes needed based on what you find.**

---

## Contact Points for Issues

If things go wrong during implementation:

1. **Foreign key constraint error** = Image IDs are invalid (zeros or non-existent)
   - Check: Are paths matching correctly?
   - Check: Are Image IDs actually in the Image table?

2. **Still getting 5391 files** = Timestamp query still broken
   - Check: Is updated_at actually Unix timestamp?
   - Check: Are we using long instead of DateTime?

3. **Getting 0 results** = Query too restrictive
   - Check: Is Unix timestamp calculated correctly?
   - Check: Are there actually new downloads in the database?

4. **Other errors** = Stop and report exact error message

---

## TL;DR

**The Bug:**
- Comparing DateTime with Unix timestamp = broken query
- Gets ALL downloads (5391) instead of just new ones (1)
- Image IDs come back as zeros
- Can't insert into database because ID 0 doesn't exist

**The Fix:**
- Store timestamp as Unix long integer, not DateTime
- Query with matching Unix timestamp
- Should get only new downloads
- Should get real Image IDs
- Should successfully insert into AlbumImage table

**The Process:**
1. ✅ Investigate database format FIRST
2. ✅ Test queries manually
3. ✅ Make targeted code changes
4. ✅ Test with 1-2 downloads (not 5000!)
5. ✅ Verify in database

**DO NOT proceed with code changes until Phase 1 investigation is complete!**
