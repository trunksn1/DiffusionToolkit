# Author Username Tagging System

## Overview

The system captures who posted each image on CivitAI and automatically tags those images in Diffusion Toolkit with `zz_AUTHOR_{username}` tags, so you can filter/browse by creator. The `zz_` prefix ensures author tags sort to the bottom of the alphabetical tag list.

---

## Part 1: Capturing Usernames (Python Scraper)

**When you download new images**, the flow is:

1. **API call** — `api_client.py` calls CivitAI's tRPC endpoint `image.getInfinite` to list images in a collection. Each image in the response includes a `user` object with a `username` field.

2. **ImageItem model** — `models.py` stores the username alongside the image ID, URL, collection info, etc.

3. **Database storage** — `database.py` saves the username into the `downloads` table (column `username`, added via auto-migration). The `COALESCE` in the upsert ensures that if we re-scrape and the API somehow returns no username, we keep any previously captured value.

### Key files

- `api_client.py:278` — extracts `img.get('user', {}).get('username')`
- `database.py:58-61` — `ALTER TABLE` migration adds column on first run
- `database.py:131-147` — `mark_pending_batch()` stores username with each record

---

## Part 2: Tagging New Downloads (C# — Automatic)

**After each scraper run**, `AssignDownloadedImagesToAlbum()` in `MainWindow.xaml.cs` runs:

1. **Queries CivitAI state DB** (line 1633) — `SELECT local_path, collection_name, username FROM downloads WHERE status = 'completed' AND updated_at > ?` — gets only images from this session.

2. **Builds two mappings:**
   - `pathToCollection` — file path → collection name (for `COLL_` tags)
   - `pathToUsername` — file path → author username (for `zz_AUTHOR_` tags)

3. **Resolves paths to image IDs** — calls `GetImageIdsByPaths()` to find matching images in the Diffusion Toolkit database.

4. **Groups by username** — builds `authorToImageIds` dictionary (e.g., `"phinjo" → [id1, id2, id3, ...]`).

5. **Creates and assigns tags** — for each author:
   - `GetOrCreateTag("zz_AUTHOR_phinjo")` — creates the tag if it doesn't exist, returns its ID
   - `AddImagesTag([id1, id2, ...], tagId)` — bulk-assigns the tag to all their images

This runs automatically every time the scraper finishes — new downloads get author tags with zero manual effort.

---

## Part 3: Backfilling Existing Images

Two tools handle images that were downloaded before this feature existed:

### `backfill_usernames.py` (Python)

Populates the `username` column for records that have `username IS NULL`:

1. Queries `downloads` table for all completed records missing a username
2. For each, calls `tRPC image.get` with the `civitai_id` — this returns the image's full metadata including the `user` object
3. Updates the `username` column in the database
4. Rate-limited (0.5s delay by default) to avoid CivitAI throttling

**Usage:**
```bash
cd "Diffusion.PyScripts/Civitai Collections Scraper"
.venv/Scripts/activate

# Preview
python backfill_usernames.py --db-path "C:/Users/trunk/AppData/Roaming/DiffusionToolkit/Civitai/civitai_state.db" --dry-run

# Run
python backfill_usernames.py --db-path "C:/Users/trunk/AppData/Roaming/DiffusionToolkit/Civitai/civitai_state.db"

# Faster (but risk rate limiting)
python backfill_usernames.py --db-path "..." --delay 0.3
```

**Important:** The REST API (`/v1/images?id=X`) does NOT work for this — it filters by model ID, not image ID. Only the tRPC endpoint `image.get` correctly resolves by image ID.

### BackfillAuthorTags Button (C# — UserPlus Icon in Toolbar)

Applies `zz_AUTHOR_` tags to ALL images that have a username, not just recent ones:

1. Queries `SELECT local_path, username FROM downloads WHERE status = 'completed' AND username IS NOT NULL` — no timestamp filter, gets everything
2. Resolves all paths to Diffusion Toolkit image IDs
3. Groups by username → image IDs
4. Creates/assigns `zz_AUTHOR_` tags in bulk
5. Shows a popup with the count of authors and images tagged

---

## Part 4: The Image2 Gap

The `civitai-extension-trimmed.db` (at `C:\Users\trunk\AppData\Roaming\DiffusionToolkit\`) has ~19,000 CivitAI images, but only ~10,800 were originally in the `downloads` table. The remaining ~6,800 were downloaded through other means (e.g., the CivitAI browser extension).

These were recovered by:

1. Extracting the `civitai_id` from filenames using the `__CIV_ID__57179764` pattern
2. Inserting them into the `downloads` table as `completed` with `collection_id = 0` (marker for imported records)
3. Running `backfill_usernames.py` to fetch their usernames via the API

Now all ~17,600 CivitAI images are in one table, and the BackfillAuthorTags button tags them all.

---

## Data Flow Summary

```
CivitAI API
    |
    v (username in API response)
Python scraper --> downloads table (civitai_state.db)
    |                  |
    |                  | username column
    |                  v
    |            C# BackfillAuthorTags / post-download hook
    |                  |
    |                  v
    |            Tag table <-- "zz_AUTHOR_phinjo"
    |            ImageTag table <-- image 123 <-> tag 456
    |                  |
    |                  v
    +------------> Diffusion Toolkit UI (filter by tag)
```

---

## Where Everything Lives

| What | Where |
|------|-------|
| Username data | `civitai_state.db` → `downloads.username` |
| Author tags | Diffusion Toolkit DB → `Tag` table (name = `zz_AUTHOR_*`) |
| Tag assignments | Diffusion Toolkit DB → `ImageTag` junction table |
| Backfill script | `Diffusion.PyScripts/Civitai Collections Scraper/backfill_usernames.py` |
| Backfill button | Toolbar UserPlus icon → `BackfillAuthorTags()` in `MainWindow.xaml.cs` |
| Auto-tagging | `AssignDownloadedImagesToAlbum()` in `MainWindow.xaml.cs` (runs after every scraper session) |

### Database Locations

| Database | Path |
|----------|------|
| CivitAI state DB | `C:\Users\trunk\AppData\Roaming\DiffusionToolkit\Civitai\civitai_state.db` |
| CivitAI extension DB | `C:\Users\trunk\AppData\Roaming\DiffusionToolkit\civitai-extension-trimmed.db` |
| Diffusion Toolkit main DB | User-configurable (stores images, tags, albums) |

---

## Files Modified

| File | Changes |
|------|---------|
| `Diffusion.PyScripts/Civitai Collections Scraper/models.py` | Added `username` field to `ImageItem` and `DownloadRecord` |
| `Diffusion.PyScripts/Civitai Collections Scraper/api_client.py` | Extracts `username` from API response |
| `Diffusion.PyScripts/Civitai Collections Scraper/database.py` | Auto-migration adds `username` column; updated all SQL statements |
| `Diffusion.PyScripts/Civitai Collections Scraper/backfill_usernames.py` | One-time script to fetch usernames for existing records |
| `Diffusion.Toolkit/MainWindow.xaml` | Added BackfillAuthorTags toolbar button |
| `Diffusion.Toolkit/MainWindow.xaml.cs` | Added `BackfillAuthorTags()` method; updated `AssignDownloadedImagesToAlbum()` to also apply author tags |
| `Diffusion.Toolkit/Models/MainModel.cs` | Added `BackfillAuthorTagsCommand` property |

---

## Troubleshooting

### No author tags appearing after download

- Check that the Python scraper is capturing usernames: `SELECT civitai_id, username FROM downloads ORDER BY updated_at DESC LIMIT 10;`
- If `username` is NULL, the API might not have returned user info for those images (deleted users, private images)

### BackfillAuthorTags shows "0 images tagged"

- Make sure the download folder has been scanned by Diffusion Toolkit first
- The paths in `civitai_state.db` must match the paths in the Diffusion Toolkit Image table

### `backfill_usernames.py` returns "(not found)" for many images

- The image may have been deleted from CivitAI
- The user account may have been deleted
- This is expected for a small percentage of images (~3-5%)

### Verifying username data

```bash
sqlite3 "C:\Users\trunk\AppData\Roaming\DiffusionToolkit\Civitai\civitai_state.db"

-- Count by status
SELECT COUNT(*), CASE WHEN username IS NULL THEN 'no username' ELSE 'has username' END
FROM downloads WHERE status = 'completed' GROUP BY 2;

-- Top authors
SELECT username, COUNT(*) c FROM downloads
WHERE username IS NOT NULL GROUP BY username ORDER BY c DESC LIMIT 20;

-- Check imported records (from Image2)
SELECT COUNT(*) FROM downloads WHERE collection_id = 0;
```
