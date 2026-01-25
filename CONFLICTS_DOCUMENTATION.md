# Cherry-Pick Conflicts Documentation

## Branch: personal-features-clean
## Commit: 99a2d37 (Add personal features: CivitAI extension data tab and tag editor)

---

## Summary

This commit contains **TWO SEPARATE FEATURES**:
1. **CivitAI Extension Tab** (what you want)
2. **Tag Editor System** (conflicts with upstream's tag system)

The conflicts arise because the **upstream master branch has implemented their own tag system** with different database schema and API, which conflicts with the tag system in your commit.

---

## Conflict Files

### 1. `Diffusion.Database/DataStore.Tag.cs` - BOTH ADDED
**Conflict Type**: Both branches created this file independently

**Upstream (HEAD) Implementation**:
- Methods: `GetTags()`, `GetTagsWithCount()`, `CreateTag()`, `CreateTags()`, `UpdateTag()`, `RemoveTag()`
- Returns `TagCount` model with `Id`, `Name`, `Count`
- Simple tag operations focused on individual tag management

**Your Implementation**:
- Methods: `GetTagsView()`, `GetTags()`, `GetOrCreateTag()`, `GetTag()`, `AddTagsToImages()`, `SetTagsForImages()`, `GetTagsForImage()`, `DeleteUnusedTags()`, `RenameTag()`, `DeleteTag()`, `MigrateCustomTagsToTables()`
- Returns `TagListItem` model with `Id`, `Name`, `CreatedDate`, `ImageCount`
- More comprehensive system with batch operations and migration support
- Uses case-insensitive tag matching (COLLATE NOCASE)

**Why It Conflicts**: Completely different APIs and return types.

**Recommended Solution**:
- **Keep upstream's implementation** since you want to use their tag system
- Extract only CivitAI-related code from your commit

---

### 2. `Diffusion.Database/Models/ImageTag.cs` - BOTH ADDED
**Conflict Type**: Both branches created this file

**Upstream (HEAD)**:
```csharp
public class ImageTag
{
    public int ImageId { get; set; }
    public int TagId { get; set; }
}
```

**Your Version**:
```csharp
[Indexed(Name = "IDX_ImageTag", Order = 1, Unique = true)]
public int ImageId { get; set; }

[Indexed(Name = "IDX_ImageTag", Order = 2, Unique = true)]
public int TagId { get; set; }
```

**Why It Conflicts**: Your version adds SQLite index attributes.

**Recommended Solution**:
- **Keep upstream's version** (simpler, database handles indexing)
- The composite unique index is a nice optimization but not critical

---

### 3. `Diffusion.Database/Models/Tag.cs` - BOTH ADDED
**Conflict Type**: Both branches created this file

**Upstream (HEAD)**:
```csharp
public class Tag
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    public string Name { get; set; }
}

public class TagCount
{
    public int Id { get; set; }
    public string Name { get; set; }
    public int Count { get; set; }
}
```

**Your Version**:
```csharp
public class Tag
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Unique, Collation("NOCASE")]  // Case-insensitive
    public string Name { get; set; }

    public DateTime CreatedDate { get; set; }
}
```

**Why It Conflicts**: Different schema - you added `CreatedDate` field and case-insensitive uniqueness.

**Recommended Solution**:
- **Keep upstream's version** to maintain compatibility
- Their `TagCount` helper class is useful for queries

---

### 4. `Diffusion.Toolkit/Controls/FilterControlModel.cs` - BOTH MODIFIED
**Conflict Type**: Code structure difference

**Issue**:
- Upstream uses C# 10+ **field-scoped properties** (auto-properties with `field` keyword)
- Your version explicitly declares private fields at the top of the class

**Example**:
```csharp
// Upstream (modern C# 10+)
public bool UsePrompt { get; set => SetField(ref field, value); }

// Your version (traditional)
private bool _usePrompt;
public bool UsePrompt { get => _usePrompt; set => SetField(ref _usePrompt, value); }
```

**Additional Changes in Your Version**:
- Added `UseTags`, `Tags`, `TagsMode` properties (lines 584-600)
- Added tags to `IsActive` property check (line 652)
- Added tags to `Clear()` method (lines 705-706, 733-735)

**Why It Conflicts**: Upstream upgraded to .NET 10 and refactored property syntax.

**Recommended Solution**:
- **Keep upstream's modern syntax** (cleaner, more maintainable)
- **Manually re-add** only the 3 tag-related properties if you want tag filtering in your personal branch
- Note: Tag filtering might not be needed if using upstream's tag UI

---

### 5. `Diffusion.Toolkit/Controls/MetadataPanel.xaml` - BOTH MODIFIED
**Conflict Type**: Needs investigation (file too large, only read 50 lines)

**What Changed in Your Commit**:
- Added new "CivitAI" tab with accordion sections
- Added tab header button for CivitAI link
- This is the **core UI for your CivitAI feature**

**Likely Upstream Changes**:
- Possible UI refinements or new metadata sections
- May have touched the same tab control structure

**Recommended Solution**:
- **This needs manual review** - read full file to see exact conflict
- The CivitAI tab addition should be compatible with upstream
- May need to carefully merge both sets of changes

---

### 6. `Diffusion.Toolkit/Models/ImageEntry.cs` - BOTH MODIFIED
**Conflict Type**: Duplicate private field declarations

**Issue**: Both branches added the **exact same private fields** (lines 14-33):
```csharp
private EntryType _entryType;
private string? _score;
private int _albumCount;
private IEnumerable<string> _albums;
private int _tagCount;
private bool _unavailable;
private bool _hasError;
private bool _isEmpty;
private string _path;
private int _width;
private int _height;
private double _thumbnailHeight;
private double _thumbnailWidth;
private bool _isWatched;
private bool _isRecursive;
private int _count;
private long _size;
```

**Why It Conflicts**: Upstream also refactored to use explicit field declarations (moving away from C# 10+ field-scoped properties).

**Recommended Solution**:
- **Keep upstream's version entirely** (they're identical)
- The `TagCount` property (lines 166-170) exists in both versions

---

### 7. `Diffusion.Toolkit/Services/ServiceLocator.cs` - BOTH MODIFIED
**Conflict Type**: Different service registrations

**Upstream Added** (modern C# field-scoped properties):
- Uses `field` keyword for lazy initialization
- All existing services use auto-property pattern

**Your Commit Added** (lines 32-37):
```csharp
private static TaggingService? _taggingService;
private static NotificationService? _notificationService;
private static ScanningService? _scanningService;
private static ContextMenuService? _contextMenuService;
private static CivitaiPostService? _civitaiPostService;
private static CivitAiExtensionDataStore? _civitAiExtensionDataStore;
```

**Later in File**:
- Your commit has properties for these services
- Upstream **already has** most of these services defined using the modern syntax!
- Your commit duplicated some services that already exist

**What You Actually Need**:
- **ONLY** `CivitAiExtensionDataStore` - this is unique to your CivitAI feature
- `SetCivitAiExtensionDataStore()` method (lines 134-137)

**Why It Conflicts**: Service registration overlap + syntax differences.

**Recommended Solution**:
- **Keep upstream's version**
- **Manually add only**:
  - `private static CivitAiExtensionDataStore? _civitAiExtensionDataStore;` (line 30)
  - The getter property at line 132: `public static CivitAiExtensionDataStore? CivitAiExtensionDataStore => _civitAiExtensionDataStore;`
  - The setter method at lines 134-137

---

## Files Successfully Cherry-Picked (No Conflicts)

These files from your commit were applied successfully:
- ✅ `Diffusion.Database/CivitAiExtensionDataStore.cs` - NEW FILE (your external DB access layer)
- ✅ `Diffusion.Database/Models/CivitAiExtensionData.cs` - NEW FILE (your model for external DB)
- ✅ `Diffusion.Toolkit/Models/ImageViewModel.cs` - MODIFIED (CivitAI properties added)
- ✅ `Diffusion.Toolkit/Pages/Search.xaml.cs` - MODIFIED (CivitAI data loading logic)
- ✅ Many other files with minor changes

---

## Strategy: Extract Only CivitAI Feature

### Option A: Manual Resolution (Recommended)
1. **Abort current cherry-pick**: `git cherry-pick --abort`
2. **Create a new commit** with only CivitAI changes:
   - Copy the CivitAI-specific files from personal-features branch:
     - `Diffusion.Database/CivitAiExtensionDataStore.cs`
     - `Diffusion.Database/Models/CivitAiExtensionData.cs`
   - Manually add CivitAI properties to `ImageViewModel.cs`
   - Manually add CivitAI tab to `MetadataPanel.xaml`
   - Manually add CivitAI loading logic to `Search.xaml.cs`
   - Manually add CivitAiExtensionDataStore to `ServiceLocator.cs`
   - Initialize it in `MainWindow.xaml.cs`
3. **Skip all tag-related changes** - let upstream handle tags

### Option B: Resolve Conflicts Strategically
1. For **tag-related conflicts** → Choose upstream (--theirs)
2. For **ServiceLocator** → Keep upstream, manually add CivitAI store only
3. For **MetadataPanel.xaml** → Needs careful manual merge
4. For **FilterControlModel** → Keep upstream entirely (no tag filtering needed)
5. For **ImageEntry** → Keep upstream (they're identical)

---

## Second Commit: 46e7cea (Editable Lora Riforgiati + Link Button)

This commit builds on top of commit 99a2d37, so it **must be applied AFTER resolving the first commit's conflicts**.

**Changes in this commit**:
- Added editable Lora_riforgiati field with Edit/Save/Cancel buttons
- Added link button in tab header (Ⓒ)
- Added Link accordion section with clickable CivitAI URL
- Extracts CivitAI image ID from filename pattern `__CIV_ID__123456`

**Files Modified**:
- `Diffusion.Toolkit/Models/ImageViewModel.cs` - Added edit mode properties
- `Diffusion.Toolkit/Controls/MetadataPanel.xaml` - Added Edit/Save/Cancel buttons and link UI
- `Diffusion.Toolkit/Pages/Search.xaml.cs` - Added edit mode command logic and URL extraction

**This commit should apply cleanly** once the first commit's conflicts are resolved.

---

## Recommended Action Plan

1. **Abort current cherry-pick** to start fresh with a clear strategy
2. **Decide**: Do you want ANY tag functionality, or ONLY CivitAI features?
   - If only CivitAI → Use Option A (manual extraction)
   - If you want to keep your tag system → Major refactoring needed
3. **Document your decision** before proceeding
4. **Test thoroughly** after resolution to ensure no breaking changes

---

## Notes

- Upstream upgraded from .NET 6 → .NET 10
- Upstream refactored to use modern C# field-scoped properties
- Your personal-features branch is based on older codebase
- Tag system conflicts are **fundamental** - not just syntax differences
- CivitAI feature is **independent** and should be extractable cleanly
