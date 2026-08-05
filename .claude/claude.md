# Diffusion Toolkit Project Context

## Project Overview

Diffusion Toolkit is a WPF application (.NET) for managing and organizing AI-generated images from Stable Diffusion and similar tools. It includes database management, metadata extraction, album organization, and integration with CivitAI for downloading images from collections.

**Primary Language:** C# (WPF, .NET)
**Database:** SQLite
**Additional Components:** Python scripts for CivitAI integration

---

## Project Structure

### Key Directories

- `Diffusion.Toolkit/` - Main WPF application
  - `MainWindow.xaml.cs` - Main application window and orchestration logic
  - `Pages/Settings.xaml(.cs)` - Settings UI
  - `Configuration/Settings.cs` - Application settings model

- `Diffusion.Database/` - SQLite database layer
  - `DataStore.Image.cs` - Image-related database operations
  - `DataStore.Album.cs` - Album-related database operations

- `Diffusion.PyScripts/` - Python integration scripts
  - `Civitai Collections Scraper/` - Python scripts for downloading from CivitAI
    - `main.py` - Entry point with argparse CLI
    - `api_client.py` - CivitAI API interaction
    - `downloader.py` - Download orchestration
    - `storage.py` - Deduplication and file management
    - `database.py` - SQLite state tracking
    - `config.yaml` - User configuration

### Important Files

**C# Code:**
- `MainWindow.xaml.cs` - Contains CivitAI integration workflow:
  - `LaunchCivitaiScraper()` (line ~1280) - Launches Python script
  - `AssignDownloadedImagesToAlbum()` (line ~1390) - Album assignment logic
  - `GetCivitaiScriptsPath()` (line ~1170) - Resolves script location
  - `GetCivitaiDatabasePath()` (line ~1176) - Resolves AppData database path

**Python Code:**
- `main.py` - CLI with subcommands (sync, retry, stats, etc.)
  - **CRITICAL:** Global arguments (--db-path) must come BEFORE subcommands
  - Example: `python main.py --db-path "..." sync` ✓
  - Wrong: `python main.py sync --db-path "..."` ✗

**Configuration:**
- `config.yaml` - Python script configuration (collections, paths, etc.)
- User settings stored in: `C:\Users\{user}\AppData\Roaming\DiffusionToolkit\`

---

## Key Architectural Decisions

### CivitAI Integration Workflow

**Database Locations:**
- **CivitAI state database:** `C:\Users\{user}\AppData\Roaming\DiffusionToolkit\Civitai\civitai_state.db`
  - Tracks download state, prevents re-downloads
  - Shared between C# and Python

- **DiffusionToolkit main database:** User-configurable location
  - Stores image metadata, albums, paths
  - Queried to prevent duplicate downloads

**Download Flow:**
1. User clicks "Launch NSFW Civitai Collections Scraper" button
2. C# launches Python script: `python main.py --db-path "{AppDataPath}" sync`
3. Python fetches collection metadata from CivitAI API
4. Python checks if images already exist (deduplication):
   - Checks CivitAI state database (by civitai_id)
   - Checks filesystem in configured paths
   - Checks DiffusionToolkit database (by path)
5. Python downloads new images to configured folder
6. Python updates CivitAI state database
7. C# waits for Python to complete (monitors process)
8. C# validates database was updated (timestamp check)
9. C# queries for newly downloaded images (created_at > last_sync_time)
10. C# assigns images to selected album

**Album Assignment Logic:**
- Uses timestamp-based filtering: `WHERE created_at > {lastSyncTime}`
- Converts Python paths to C# paths (case-insensitive, normalized)
- Queries DiffusionToolkit database to get image IDs by paths
- Inserts album assignments in batch

---

## Common Development Tasks

### Building the Project

```bash
# Clean build
dotnet clean
dotnet build

# Rebuild (recommended after changes)
dotnet build --no-incremental
```

**Note:** `.csproj` includes configuration to copy `Diffusion.PyScripts` to output directory.

### Running Python Scripts Manually

```bash
cd "Diffusion.PyScripts/Civitai Collections Scraper"

# Activate virtual environment
.venv/Scripts/activate

# Run with database override (same as C# does)
python main.py --db-path "C:\Users\{user}\AppData\Roaming\DiffusionToolkit\Civitai\civitai_state.db" sync

# Dry run to see what would be downloaded
python main.py --db-path "{path}" sync --dry-run

# Test authentication
python main.py test-auth

# View stats
python main.py --db-path "{path}" stats
```

### Debugging CivitAI Integration

**Check C# Logs:**
```
DiffusionToolkit.log
```

Look for:
- `LaunchCivitaiScraper: Python path: ...`
- `LaunchCivitaiScraper: Database path: ...`
- `LaunchCivitaiScraper: Process exited with code X`
- `AssignDownloadedImagesToAlbum: Query returned N results`

**Check Python Console:**
- Python console window shows real-time progress
- Look for "Using database path override" message
- Check for authentication errors (403 Forbidden)
- Verify download counts match expectations

**Check Database State:**
```bash
sqlite3 "C:\Users\{user}\AppData\Roaming\DiffusionToolkit\Civitai\civitai_state.db"

# View schema
.schema

# Count downloads by status
SELECT status, COUNT(*) FROM download_state GROUP BY status;

# View recent downloads
SELECT civitai_id, status, collection_name, downloaded_at
FROM download_state
ORDER BY downloaded_at DESC
LIMIT 10;
```

---

## Known Issues and Gotchas

### 1. Python Argument Order (CRITICAL)

**Problem:** Python's argparse requires global arguments before subcommands.

**Correct:**
```csharp
Arguments = $"main.py --db-path \"{civitaiDbPath}\" sync"
```

**Wrong:**
```csharp
Arguments = $"main.py sync --db-path \"{civitaiDbPath}\""  // ✗ Python rejects this
```

**Impact:** Python exits with error, database not updated, album assignment fails.

### 2. Database Path Synchronization

**Problem:** C# and Python must use the SAME database file.

**Solution:**
- C# passes `--db-path` to Python via command-line argument
- Python overrides `config['paths']['state_db']` when argument provided
- Both use: `C:\Users\{user}\AppData\Roaming\DiffusionToolkit\Civitai\civitai_state.db`

**Validation:**
- C# records database timestamp before launch
- C# checks timestamp after Python exits
- If unchanged → either no new images OR error occurred (check exit code)

### 3. Temp Table Query Issue

**Problem:** SQLite temp table IN clause returned 0 for valid image IDs.

**Location:** `DataStore.Image.cs` - `GetImageIdsByPaths()` method (line ~642)

**Solution:** Changed from temp table to simple loop:
```csharp
// OLD (broken): Used temp table with IN clause
// NEW (working): Loop through paths and query individually
foreach (var path in paths)
{
    var id = GetImageIdByPath(path);
    if (id > 0) imageIds.Add(id);
}
```

**Why it failed:** Unknown SQLite quirk with temp tables and IN clauses.

### 4. Unicode Encoding in Python Console

**Problem:** Windows console (cp1252) can't display certain Unicode characters.

**Symptom:**
```
UnicodeEncodeError: 'charmap' codec can't encode characters in position 31-32
```

**Impact:** Script crashes when printing image names with special characters.

**Workaround:** Handled in main.py with try-except blocks around print statements.

### 5. Virtual Environment Shipping

**Current State:** `.venv` folder is shipped with repository (included in build output).

**Issues:**
- Large folder size (~100MB)
- Platform-specific binaries
- Not ideal for distribution

**Future Improvement:** Consider using embedded Python for production releases.

### 6. WPF Local Values Beat Style Triggers

**Problem:** A DataTrigger in a `Style` silently does nothing when the same
property is also set as an attribute on the element. Local values sit above
style triggers in WPF's dependency-property precedence.

**Where it bit us:** the calendar's day cell (`Pages/CivitaiCalendar.xaml`) set
`BorderBrush` / `Background` / `BorderThickness` on the `Border` *and* in its
`IsToday` / `IsSelected` triggers. Today and the selected day looked identical
to every other cell — while `Opacity`, which had no local value, dimmed
out-of-month cells correctly. That split is the tell.

**Rule:** if a cell's trigger appears to run but change nothing, look for a
local value on the element before doubting the binding. Defaults belong in
`Style` setters.

### 7. CivitAI Sometimes Stores No Filename

**Problem:** posts submitted through a **challenge page** come back with
`name: null` on every image, plus an empty `metadata` block — verified 2026-08-05
across `post.getInfinite` and `image.getInfinite`, authenticated and anonymous.
An ordinary post from the same account keeps both.

**Impact:** the calendar matches library files by filename, so those images can
never be matched to the file that was uploaded.

**Solution:** one deterministic stem, `CivitaiPostsService.NamelessStem(id)` =
`civitai-{id}`, used by *both* `DownloadMissingAsync` (when writing the file)
and `ResolveMatches`/`MatchNameFor` (when looking it up). Download → rescan →
matched. Changing one side without the other silently breaks the loop.

### 8. Scheduling Limits Come From CivitAI's Source

`SchedulePostModal.tsx` in `civitai/civitai`: max is `dayjs().add(3, 'month')` —
3 **calendar** months, not 90 days — and the floor is
`POST_MINIMUM_SCHEDULE_MINUTES = 60`, so "in the future" is not enough
validation. Both are mirrored in `CivitaiPostsService`
(`MaxScheduleMonthsAhead` / `MinScheduleMinutesAhead` and the derived dates and
messages) and must be read from there, never re-hardcoded at a call site.

---

## Development Guidelines

### Code Style

**C# Conventions:**
- Use PascalCase for public members
- Use camelCase for private fields
- Prefix private fields with underscore: `_settings`
- Use nullable reference types: `string?`
- Add XML comments for public APIs

**Python Conventions:**
- Follow PEP 8
- Use snake_case for functions/variables
- Use type hints where appropriate
- Add docstrings for public functions

### Git Workflow

**Branch Structure:**
- `master` - Main branch (upstream: RupertAvery/DiffusionToolkit)
- `feature/civitai-enhancements` - Your working branch
- Fork: `trunksn1/DiffusionToolkit`

**Commit Messages:**
Follow existing style:
```
Fix Civitai album assignment using timestamp and direct path queries

- Changed from civitai_id to created_at timestamp comparison
- Fixed GetImageIdsByPaths to use direct queries instead of temp table
- Added logging for better debugging

Co-Authored-By: Claude Sonnet 4.5 <noreply@anthropic.com>
```

**IMPORTANT:** When adding Python scripts folder:
1. Remove any `.git` folders from `Diffusion.PyScripts/`
2. Use `git rm --cached -f Diffusion.PyScripts` if git treats it as submodule
3. Re-add with `git add "Diffusion.PyScripts/"` to add as regular files

### Testing Workflow

**After Making Changes:**

1. **Build:**
   ```bash
   dotnet build
   ```

2. **Test CivitAI Download:**
   - Open application
   - Click "Launch NSFW Civitai Collections Scraper"
   - Verify Python console appears
   - Check for "Using database path override" message
   - Verify downloads complete (or "Nothing new to download")

3. **Test Album Assignment:**
   - Select an album in UI
   - Click download button (should happen automatically after script)
   - Wait 10 seconds (configured delay)
   - Check logs for "AssignDownloadedImagesToAlbum: SUCCESS"
   - Open album and verify images were added

4. **Check Logs:**
   - Review `DiffusionToolkit.log` for errors
   - Look for process exit code (should be 0)
   - Verify database timestamp changed

---

## Settings and Configuration

### C# Settings (Settings.cs)

**Key Properties:**
- `CivitaiScraperRepositoryPath` - DEPRECATED, kept for migration only
  - Old users may have this in config
  - Used once to migrate database to AppData
  - Not shown in UI

**Settings Location:**
```
C:\Users\{user}\AppData\Roaming\DiffusionToolkit\settings.json
```

### Python Configuration (config.yaml)

**Important Sections:**

```yaml
paths:
  downloads: "E:/path/to/downloads"  # Where images are saved
  check_paths:  # Paths to check for existing files
    - "E:/path/to/downloads"
    - "E:/other/path"
  state_db: "./civitai_state.db"  # ← OVERRIDDEN by --db-path from C#

collections:
  - name: "Collection Name"
    id: 1234567  # CivitAI collection ID

browser:
  profile_path: "./Profilo Pezzotto"  # Chrome profile for cookies

limits:
  max_pages_per_collection: 1  # Pages to fetch per collection
  concurrent_downloads: 3      # Parallel downloads
```

---

## Troubleshooting Common Issues

### "Database was NOT updated" Warning

**Possible Causes:**

1. **No new images to download** (most common)
   - Collections already synced
   - Everything already downloaded
   - Solution: This is normal, change warning to info message

2. **Argument order wrong**
   - Python rejects `--db-path` argument
   - Solution: Ensure `--db-path` comes BEFORE `sync`

3. **Python error**
   - Check exit code in logs
   - Look at Python console output
   - Solution: Fix Python errors (auth, config, etc.)

### "Album assignment found 0 images"

**Possible Causes:**

1. **Database not shared**
   - C# and Python using different databases
   - Solution: Verify both use AppData location

2. **Timestamp sync issue**
   - `lastSyncTime` too recent
   - Solution: Check `lastSyncTime` value in logs

3. **Path mismatch**
   - Python saves to different location than C# expects
   - Solution: Verify paths match in config.yaml and C# settings

### "Python executable not found"

**Cause:** Python not in PATH or wrong Python version.

**Solution:**
1. Install Python 3.11+
2. Ensure Python is in PATH
3. Or update code to use `.venv/Scripts/python.exe` directly

---

## API and External Dependencies

### CivitAI API

**Endpoints Used:**
- Collection metadata: `https://civitai.com/api/v1/collections/{id}`
- Image downloads: Direct URLs from API response

**Authentication:**
- Preferred: official API key (Bearer token), stored DPAPI-encrypted in C# settings
  (`Settings.CivitaiApiKeyProtected`, accessed via `GetCivitaiApiKey()`/`SetCivitaiApiKey()`)
  and passed to Python via the `CIVITAI_API_KEY` environment variable — NEVER on the
  command line (Arguments are logged to DiffusionToolkit.log)
- Also supported: OAuth 2.0 + PKCE sign-in (`Services/CivitaiOAuthService.cs`), passed
  to Python as `CIVITAI_ACCESS_TOKEN`. It is a SECOND credential, not a replacement:
  tRPC accepts OAuth tokens, REST `/api/v1/*` rejects them (401) and still needs the key
- Python tries Bearer first per endpoint family and falls back to cookies on 401/403,
  memoizing the rejection (`CivitAIClient._auth_request`, with `_auth_get`/`_auth_post`
  wrappers in api_client.py); with no credentials, behavior is cookie-only as before.
  Families: 'trpc' (OAuth→key→cookies, reads AND mutations), 'rest' (key→cookies),
  'session' and 'upload' (OAuth→key→cookies)
- Cookie fallback: `civitai*_cookies.txt` files, then Chrome browser cookies
- CDN image downloads never send the Bearer token (cookies only)
- Username resolution can NOT use OAuth (`/v1/users/me` is REST), so
  `CivitaiPostsService` passes `--username` from the OAuth session — without it an
  OAuth-only user with no cookies fails the whole posts fetch
- No Delete scope is ever requested, so `post.delete` 403s: a failed upload reports the
  orphaned draft's URL instead of silently leaving one behind
- `probe_auth.py` / `probe_oauth.py` / `probe_stats.py` (standalone) empirically test
  which endpoints accept which credential, and what stats fields come back
- NOTE: `LaunchCivitaiScraper` uses `UseShellExecute = false` — required to pass
  environment variables; the Python console window still appears because
  `CreateNoWindow = false` and python.exe is a console app

**Rate Limiting:**
- API has rate limits (exact limits unknown)
- Script includes delays between requests
- Be respectful of API usage

### SQLite Databases

**CivitAI State Database Schema:**
```sql
CREATE TABLE download_state (
    civitai_id INTEGER PRIMARY KEY,
    status TEXT,  -- 'completed', 'pending', 'failed'
    collection_id INTEGER,
    collection_name TEXT,
    downloaded_at TIMESTAMP,
    file_path TEXT,
    error_message TEXT
);
```

**DiffusionToolkit Database:**
- Complex schema with images, albums, metadata
- Managed by `Diffusion.Database` project
- Don't modify schema directly without migration

---

## Future Improvements

### Planned Enhancements

1. **Better Download Feedback**
   - Show download progress in UI
   - Real-time status updates
   - Cancel/pause functionality

2. **Embedded Python Distribution**
   - Package Python with application
   - Eliminate dependency on system Python
   - More reliable deployment

3. **API Key Support**
   - Alternative to cookie-based auth
   - More reliable authentication
   - Easier for users to configure

4. **Smarter Deduplication**
   - Perceptual hashing for duplicate detection
   - Handle renamed files
   - Cross-collection deduplication

---

## Quick Reference Commands

### Git

```bash
# Status
git status

# Stage changes
git add <files>

# Commit
git commit -m "message"

# Push to fork
git push myfork feature/civitai-enhancements

# Pull latest from upstream
git fetch upstream
git merge upstream/master
```

### Build

```bash
# Clean build
dotnet clean && dotnet build

# Run tests
dotnet test

# Publish release
dotnet publish -c Release
```

### Python

```bash
# Activate venv
.venv\Scripts\activate

# Install dependencies
pip install -r requirements.txt

# Run script
python main.py --db-path "{path}" sync

# Dry run
python main.py --db-path "{path}" sync --dry-run
```

---

## Contact and Resources

**Repository:** https://github.com/RupertAvery/DiffusionToolkit
**Your Fork:** https://github.com/trunksn1/DiffusionToolkit
**Branch:** feature/civitai-enhancements

**Key Documentation Files:**
- `PHASE2_ROOT_CAUSE_AND_FIX.md` - Argument order fix analysis
- `DATABASE_PATH_FIX_IMPLEMENTATION.md` - Database synchronization
- `CIVITAI_INTEGRATION_IMPLEMENTATION_SUMMARY.md` - Integration overview
- `DOWNLOAD_FAILURE_INVESTIGATION.md` - Troubleshooting guide

---

## Notes for Claude

When working on this project:

1. **Always read logs first** - Check `DiffusionToolkit.log` before making assumptions
2. **Test Python standalone** - Run Python scripts manually to verify behavior
3. **Check argument order** - Python argparse is sensitive to argument placement
4. **Verify database paths** - Ensure C# and Python use same database file
5. **Don't over-engineer** - Keep solutions simple and focused
6. **Respect existing patterns** - Follow established code conventions
7. **Test the integration** - Don't just test components, test the whole workflow

**Common Pitfalls:**
- Assuming temp tables work reliably (they don't in this codebase)
- Forgetting that Python console closes immediately on error
- Not checking process exit codes
- Missing the argument order issue with argparse subcommands

**Remember:**
- User stores images at: `E:\BACKUP D Disperato\ARCHIVIO\SD Outputs\`
- Collections are in: `__My Collections` subfolder
- Database is at: `C:\Users\trunk\AppData\Roaming\DiffusionToolkit\`
- User runs from: `E:\Clouding\Dropbox\INFORMATICA\GitHub\Diffusion-Toolkit`
