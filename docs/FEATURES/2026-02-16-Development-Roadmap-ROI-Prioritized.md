# Development Roadmap — ROI-Prioritized

**Date:** 2026-02-16
**Source:** Product Engineering Analysis reviewed with Gemini AI
**Goal:** Maximize return on development investment — highest impact, lowest effort first

---

## Prioritization Rationale

1. **Fix what's broken before building what's new.** Users won't pay for features if the app hangs or bulk operations crawl. Phase 1 is all stability and performance.
2. **Monetize with "obvious wins."** Phase 2 delivers features that solve daily pain points for AI artists — the kind of features that justify a Pro tier.
3. **Differentiate from competitors.** Phase 3 builds the moat — features nobody else offers.
4. **Ecosystem for long-term growth.** Phase 4 creates a platform, not just a tool.
5. **Refactor incrementally, not all at once.** The God Object problem is addressed continuously, not as a big-bang rewrite.

---

## Phase 1: Foundation & Stability (8–12 weeks)

Fix the core before building on top of it. Every item here directly improves the experience for existing users and makes future features viable.

### 1.1 Thumbnail Loading Hang Fix

| Attribute | Detail |
|-----------|--------|
| **Location** | `ThumbnailView.xaml.cs:862` |
| **Problem** | Random UI hangs during thumbnail scrolling — likely dispatcher deadlock or blocking I/O on UI thread |
| **Effort** | 2–3 weeks |
| **Risk** | Medium — deadlocks are hard to reproduce; requires profiling with WPF diagnostic tools |
| **Dependencies** | None |
| **ROI** | Immediate — this is the #1 thing users notice. A gallery that freezes kills trust. |

**How to approach:**
1. Profile with Visual Studio Diagnostic Tools + PerfView to capture the deadlock
2. Check for `Dispatcher.Invoke` calls from background threads (should be `BeginInvoke` or `async`)
3. Look for synchronous file I/O in the thumbnail loading pipeline
4. Add `ConfigureAwait(false)` where appropriate in async chains
5. Consider replacing any `lock` patterns with `SemaphoreSlim` for async-safe synchronization

---

### 1.2 Temp Table Bug — Chunked IN Clauses

| Attribute | Detail |
|-----------|--------|
| **Location** | `DataStore.Image.cs` — `GetImageIdsByPaths()` |
| **Problem** | N individual queries instead of 1 bulk query; breaks down at 1000+ images |
| **Effort** | 1–2 weeks |
| **Risk** | Low — well-understood SQLite pattern |
| **Dependencies** | None |
| **ROI** | Medium — unblocks bulk operations (album assignment, batch tagging, CivitAI imports) |

**How to approach:**
1. Replace the loop with chunked parameterized queries (SQLite limit: 999 params)
2. Build queries like: `SELECT Id FROM Image WHERE Path IN (?, ?, ?, ...)` with 999-item chunks
3. Concatenate results across chunks
4. Add a helper method `QueryChunked<T>(string sql, IEnumerable<object> params, int chunkSize = 900)` to `DataStore` for reuse
5. Test with 5000+ paths to verify performance improvement

---

### 1.3 Search Performance — Vocabulary Indexing

| Attribute | Detail |
|-----------|--------|
| **Location** | `DataStore.Search.cs:486` and `:575` |
| **Problem** | Hamming distance on raw prompt strings — O(n) full-table scan, unusable at 50k+ images |
| **Effort** | 3–4 weeks |
| **Risk** | Medium — requires understanding existing search logic and schema changes |
| **Dependencies** | None |
| **ROI** | High — search is how users find their work. Slow search = app feels broken. |

**How to approach:**
1. Tokenize prompts into word lists at scan time, store as a vocabulary index (new table: `PromptToken(ImageId, Token, Position)`)
2. Implement trigram indexing for fuzzy matching: split each token into 3-char subsequences
3. Pre-compute token frequency for ranking (TF-IDF style)
4. Replace raw hamming distance with token-set comparison: Jaccard similarity on token sets
5. Add a migration to backfill the index for existing images
6. Keep the old hamming distance as a fallback option in advanced search

---

### 1.4 Metadata Scanning — Adaptive Worker Pool

| Attribute | Detail |
|-----------|--------|
| **Location** | Scanner service — currently hardcoded to 2 workers |
| **Problem** | 2 workers is conservative for modern hardware; large imports take too long |
| **Effort** | 2–3 weeks |
| **Risk** | Medium — must avoid overwhelming I/O subsystem or causing database write contention |
| **Dependencies** | None |
| **ROI** | Medium — directly cuts first-scan time for new users and large CivitAI imports |

**How to approach:**
1. Use `Environment.ProcessorCount` as a starting point, cap at `Math.Min(ProcessorCount, 8)`
2. Monitor I/O throughput: if workers are mostly waiting on I/O, reduce count; if CPU-bound, increase
3. Use `Channel<T>` (already in codebase) with bounded capacity to prevent memory pressure
4. Add a configurable setting: "Scan workers: Auto / 2 / 4 / 8" in Settings > General
5. Ensure SQLite writes use a single writer with queued batch inserts (avoid SQLITE_BUSY)

---

### Phase 1 Total: ~8–12 weeks | Impact: Every user benefits | Revenue: Indirect (retention, trust)

---

## Phase 2: Monetization Core (10–15 weeks)

These are the "Pro tier" features. Each one solves a real, daily problem that AI artists currently work around manually.

### 2.1 Smart Duplicate Finder (Perceptual Hashing)

| Attribute | Detail |
|-----------|--------|
| **Original Tier** | Tier 1-A |
| **Effort** | 4–6 weeks |
| **Risk** | Medium — pHash library integration, tuning similarity thresholds, false positive management |
| **Dependencies** | Phase 1.2 (chunked queries for bulk hash comparison) |
| **ROI** | Very High — #1 most-requested feature in AI art communities. Immediate Pro-tier justification. |

**How to approach:**
1. Add a `PerceptualHash` column to the Image table (64-bit integer for dHash, or byte array for pHash)
2. Use a C# pHash library (e.g., `Shipwreck.Phash` or `ImageHash`) — or implement dHash directly (resize to 9x8 grayscale, compare adjacent pixels = 64-bit hash)
3. Compute perceptual hash during metadata scanning (parallel with existing hash computation)
4. Add migration to backfill existing images (background task, interruptible)
5. Create a "Find Duplicates" window:
   - Hamming distance threshold slider (0 = exact, 1-5 = near-duplicate, 5-10 = similar)
   - Side-by-side comparison of duplicate groups
   - Bulk actions: keep best rated, delete others, move to album
6. Add "Find Similar" to context menu (right-click image > "Find Similar Images")

**Why first in Phase 2:** Lowest risk of the Tier 1 features, infrastructure already half-built (hash storage exists), and the AI art community vocally demands this.

---

### 2.2 Prompt Library / Workflow Templates

| Attribute | Detail |
|-----------|--------|
| **Original Tier** | Tier 1-B |
| **Effort** | 5–7 weeks |
| **Risk** | Medium — UI/UX complexity for prompt composition, data modeling for templates |
| **Dependencies** | None direct |
| **ROI** | Very High — power users iterate on prompts constantly. A "recipe book" is uniquely valuable. |

**How to approach:**
1. New database tables:
   - `PromptTemplate(Id, Name, Category, Prompt, NegativePrompt, Model, Sampler, CFG, Steps, Tags, CreatedDate, UsageCount)`
   - `PromptCategory(Id, Name, SortOrder)`
2. Transform the existing read-only Prompts page:
   - Add "Save as Template" button on any prompt in the list
   - Add "New Template" for composing from scratch
   - Category sidebar (Landscapes, Portraits, Anime, Custom...)
   - Search/filter templates
3. Template composition:
   - Drag-and-drop prompt fragments to compose
   - Variable placeholders: `{subject}`, `{style}`, `{quality_tags}`
   - Preview: show images generated with this template (linked by prompt match)
4. Integration points:
   - "Copy to Clipboard" button (formatted for A1111 or ComfyUI)
   - "Send to ComfyUI" (reuse existing ComfyUI integration)
   - "Generate with Template" context menu on templates

---

### 2.3 AI-Powered Auto-Tagging

| Attribute | Detail |
|-----------|--------|
| **Original Tier** | Tier 1-C (auto-tagging portion) |
| **Effort** | 5–8 weeks |
| **Risk** | High — ML model integration, large model files (~300MB for WD-Tagger), performance on CPU-only machines |
| **Dependencies** | Phase 1.4 (scanner parallelism for efficient batch processing) |
| **ROI** | Very High — eliminates the biggest manual chore. Transforms the app from browser to intelligent manager. |

**How to approach:**
1. Choose model: **WD-Tagger v3** (community standard, ~300MB, runs on CPU via ONNX Runtime)
   - Alternative: CLIP for more general labels, but WD-Tagger is purpose-built for anime/AI art
2. Ship ONNX Runtime as a NuGet dependency (Microsoft.ML.OnnxRuntime)
3. Implement `AutoTagService`:
   - Load model on first use (lazy init, ~2-3 seconds)
   - Accept image path, return list of `(tag, confidence)` pairs
   - Configurable confidence threshold (default: 0.35)
   - Batch mode: process N images with progress bar
4. UI integration:
   - Right-click > "Auto-Tag" for single image
   - "Auto-Tag All" in batch operations menu
   - Settings: model path, confidence threshold, tag categories to include/exclude
   - Review UI: show suggested tags with checkboxes before applying
5. Model download:
   - First-run download dialog (or bundle with installer)
   - Store model in `AppData/DiffusionToolkit/Models/`
   - Version check for model updates

**Why this is the riskiest in Phase 2:** External dependency on ML model, potential for large download, performance concerns on older hardware. But the payoff is transformative.

---

### Phase 2 Total: ~10–15 weeks | Impact: Power users, Pro-tier subscribers | Revenue: Direct (justifies paid tier)

---

## Phase 3: Competitive Differentiation (12–18 weeks)

Features nobody else does well. These are the "why Diffusion Toolkit and not just a folder" answers.

### 3.1 Smart Albums (Rule-Based)

| Attribute | Detail |
|-----------|--------|
| **Original Tier** | Tier 1-C (smart albums portion) |
| **Effort** | 3–4 weeks |
| **Risk** | Medium — complex query building for dynamic rules |
| **Dependencies** | Benefits enormously from Phase 2.3 (auto-tagging), but works with manual tags too |
| **ROI** | High — organization is the core value prop. Smart albums make it effortless. |

**How to approach:**
1. New table: `SmartAlbum(Id, Name, RulesJson, CreatedDate, LastEvaluated)`
2. Rule builder UI: visual conditions (AND/OR) for tags, rating, date range, model, sampler, resolution, NSFW flag
3. Evaluate rules on demand (user clicks "Refresh") or on schedule (e.g., after each scan)
4. Smart albums appear in the album sidebar with a distinct icon (e.g., lightning bolt)
5. Rules stored as JSON, evaluated to SQL WHERE clauses at runtime

---

### 3.2 A/B Comparison View

| Attribute | Detail |
|-----------|--------|
| **Original Tier** | Tier 1-D |
| **Effort** | 5–7 weeks |
| **Risk** | High — synchronized zoom/pan is complex WPF work; large image performance |
| **Dependencies** | Phase 1.1 (thumbnail hang fix — need stable image loading) |
| **ROI** | Medium-High — niche but critical for quality-focused users. Strong "wow factor" for marketing. |

**How to approach:**
1. New window: `ComparisonWindow.xaml`
2. Two-pane layout with shared `ScrollViewer` transform (synced zoom/pan via shared `MatrixTransform`)
3. Parameter diff panel below: highlight differences in CFG, sampler, steps, seed, model
4. Swipe mode: single pane with draggable vertical divider (left = image A, right = image B)
5. Access: select 2 images > right-click > "Compare Side by Side" (or Ctrl+D)
6. Bonus: "Compare with Previous" — auto-compare with the image generated just before (by timestamp + same prompt)

---

### 3.3 Generation Analytics Dashboard

| Attribute | Detail |
|-----------|--------|
| **Original Tier** | Tier 2-E |
| **Effort** | 6–8 weeks |
| **Risk** | Medium — charting in WPF (LiveCharts2 or OxyPlot), complex aggregation queries |
| **Dependencies** | Metadata already stored; benefits from Phase 1.3 (search optimization for faster queries) |
| **ROI** | High — turns passive storage into active insight. Unique selling point. |

**How to approach:**
1. New page: `AnalyticsPage.xaml` (accessible from left toolbar)
2. Charts to implement:
   - **Model Performance**: bar chart of average rating per model (min 10 images)
   - **Sampler Analysis**: heatmap of sampler x step count vs average rating
   - **Prompt Keywords**: word cloud of most-used tokens in top-rated images
   - **Generation Timeline**: line chart of images generated per day/week/month
   - **CFG Sweet Spot**: scatter plot of CFG value vs rating
3. Filter bar: date range, model filter, minimum rating
4. Use LiveCharts2 (MIT, WPF-native) for charting
5. Pre-compute aggregates in a background task after each scan, cache in a `AnalyticsCache` table

---

### 3.4 Advanced Search — Embedding-Based (Optional / Stretch)

| Attribute | Detail |
|-----------|--------|
| **Original Tier** | Part 1, item 1 (advanced) |
| **Effort** | 6–10 weeks |
| **Risk** | Very High — requires embedding model, vector storage, significant complexity |
| **Dependencies** | Phase 1.3 (basic search improvements) |
| **ROI** | High long-term — semantic search ("find images that look like a sunset") is the future |

**How to approach:**
1. Only pursue if Phase 2.3 (auto-tagging) ships successfully — proves ML model integration works
2. Use CLIP text/image embeddings (same model family as auto-tagging)
3. Store embeddings as BLOBs in SQLite or use a sidecar vector DB (SQLite-vss extension)
4. Enable "Search by description" and "Search by example image"
5. This is a stretch goal — defer if timeline is tight

---

### Phase 3 Total: ~12–18 weeks | Impact: Differentiates from all competitors | Revenue: Pro retention, marketing material

---

## Phase 4: Ecosystem & Long-Term Growth (10–15+ weeks)

Platform features that create a flywheel of community engagement and long-term value.

### 4.1 Batch Export with Presets

| Attribute | Detail |
|-----------|--------|
| **Original Tier** | Tier 3-H |
| **Effort** | 3–5 weeks |
| **Risk** | Low-Medium — standard image processing, well-understood problem |
| **Dependencies** | None |
| **ROI** | Medium — quality-of-life that artists appreciate daily |

**How to approach:**
1. Export dialog with preset system:
   - Output format: PNG, JPG (quality slider), WebP
   - Resize: percentage, max dimension, exact dimensions
   - Metadata: strip all / keep generation params / keep custom tags only
   - Watermark: text or image overlay, position, opacity
   - Filename template: `{date}_{model}_{seed}_{rating}.{ext}`
2. Save presets: "For Instagram", "For Portfolio", "For Archive"
3. Progress bar with cancel support
4. Batch operation: works with current selection or entire album

---

### 4.2 Workflow Provenance Chain

| Attribute | Detail |
|-----------|--------|
| **Original Tier** | Tier 2-G |
| **Effort** | 7–10 weeks |
| **Risk** | High — data modeling for image relationships, graph visualization |
| **Dependencies** | Stored ComfyUI workflows (already exist) |
| **ROI** | Medium-High — unique feature nobody else offers. Appeals to serious artists and researchers. |

**How to approach:**
1. New table: `ImageRelation(SourceImageId, DerivedImageId, RelationType, CreatedDate)`
   - RelationTypes: `txt2img`, `img2img`, `upscale`, `inpaint`, `controlnet`
2. Auto-detect relationships:
   - Same seed + different parameters = variation
   - img2img source path in metadata = parent-child
   - ComfyUI workflow node connections
3. Visual graph: tree view or node graph (use a WPF graph layout library)
4. Navigate: click any node to view that image's full metadata and preview

---

### 4.3 Cloud Sync / Multi-Device

| Attribute | Detail |
|-----------|--------|
| **Original Tier** | Tier 2-F |
| **Effort** | 8–12 weeks |
| **Risk** | Very High — conflict resolution, security, infrastructure costs |
| **Dependencies** | Stable DataStore, business model established |
| **ROI** | Very High long-term — but requires server infrastructure and ongoing costs |

**How to approach:**
1. Start simple: **Export/Import** of albums, tags, and ratings as a portable JSON/SQLite bundle
2. Then: file-based sync via user's own cloud storage (OneDrive, Dropbox, Google Drive)
   - Sync the metadata DB file only (not images)
   - Conflict resolution: last-write-wins with merge log
3. Eventually: dedicated sync service (requires backend infrastructure — significant investment)
4. Defer full cloud sync until revenue from Pro tier justifies infrastructure costs

---

### 4.4 Plugin / Extension System

| Attribute | Detail |
|-----------|--------|
| **Original Tier** | Tier 3-I |
| **Effort** | 6–10 weeks (initial framework) |
| **Risk** | Very High — API design, security sandboxing, documentation |
| **Dependencies** | God Object refactor progress (need modular architecture) |
| **ROI** | Very High long-term — community-driven growth, but slow to materialize |

**How to approach:**
1. Define extension points: context menu actions, export targets, metadata providers, custom viewers
2. Plugin interface: .NET assembly loading with a simple `IPlugin` interface
3. Plugin manifest: `plugin.json` with name, version, permissions, entry point
4. Plugin manager UI in settings
5. Ship 2-3 first-party plugins as examples: Discord webhook, DeviantArt upload, custom watermark

---

### Phase 4 Total: ~10–15+ weeks | Impact: Platform creation | Revenue: Long-term ecosystem value

---

## Ongoing: God Object Refactor

**Not a phase — a continuous practice.**

| Attribute | Detail |
|-----------|--------|
| **Location** | `MainWindow.xaml.cs` + 10 partial class files |
| **Approach** | Incremental — extract one service per sprint |
| **Effort** | 1–2 days per sprint, ongoing |
| **Risk** | Medium — regression risk if not tested carefully |
| **Rule** | Every time you touch MainWindow for a new feature, extract the touched area into a proper service/viewmodel first |

**Extraction order (by frequency of change):**
1. CivitAI integration logic → already partially in services
2. ComfyUI integration → `ComfyUIService` exists, move remaining logic
3. Album management → `AlbumService` exists, move UI orchestration
4. Tag management → `TagService` exists, move UI orchestration
5. Search/filter logic → move to `SearchViewModel`
6. Preview pane logic → move to `PreviewViewModel`

---

## Summary: The Full Timeline

| Phase | Duration | Key Deliverables | Revenue Impact |
|-------|----------|-----------------|----------------|
| **Phase 1** | Weeks 1–12 | Thumbnail fix, chunked queries, search indexing, scanner parallelism | Retention + trust |
| **Phase 2** | Weeks 13–27 | Perceptual dedup, prompt library, auto-tagging | **Pro tier launch** |
| **Phase 3** | Weeks 28–45 | Smart albums, A/B comparison, analytics dashboard | Pro retention + differentiation |
| **Phase 4** | Weeks 46–60+ | Batch export, provenance, cloud sync, plugins | Platform + ecosystem |
| **Ongoing** | Every sprint | God Object refactor | Developer velocity |

**Estimated time to first revenue (Pro tier launch): ~6 months** (end of Phase 2)

---

## ROI Ranking: Quick Reference

From highest to lowest return on investment:

| Rank | Item | Effort | Impact | Phase |
|------|------|--------|--------|-------|
| 1 | Thumbnail hang fix | 2–3 wk | Every user | 1 |
| 2 | Temp table chunked queries | 1–2 wk | Bulk operations | 1 |
| 3 | Smart Duplicate Finder | 4–6 wk | Pro-tier flagship | 2 |
| 4 | Search vocabulary indexing | 3–4 wk | Large libraries | 1 |
| 5 | Smart Albums | 3–4 wk | Organization | 3 |
| 6 | Prompt Library | 5–7 wk | Power users | 2 |
| 7 | Batch Export | 3–5 wk | All users | 4 |
| 8 | Scanner parallelism | 2–3 wk | New user experience | 1 |
| 9 | Auto-Tagging (WD-Tagger) | 5–8 wk | Transforms the app | 2 |
| 10 | A/B Comparison View | 5–7 wk | Quality iteration | 3 |
| 11 | Analytics Dashboard | 6–8 wk | Insights | 3 |
| 12 | Workflow Provenance | 7–10 wk | Unique feature | 4 |
| 13 | Cloud Sync | 8–12 wk | Multi-device | 4 |
| 14 | Plugin System | 6–10 wk | Ecosystem | 4 |
| 15 | Embedding Search | 6–10 wk | Future-proofing | 3 (stretch) |

---
---

# Post-Review Corrections — Code-Verified Analysis

**Reviewed by:** Claude Opus 4.6 after reading actual source code
**Date:** 2026-02-16

After reading every file referenced in the plan above, I found several factual errors, misdiagnosed problems, over-scoped features, and a fundamental business model issue. Here are the corrections.

---

## CRITICAL: The Licensing Problem

The plan above casually proposes a "Free / Pro / Lifetime" business model. **This needs a reality check.**

Diffusion Toolkit is licensed under **MIT** (Copyright 2022, David Khristepher Santos — RupertAvery). This is an open-source project you've forked. The MIT license allows commercial use, but:

1. **You cannot monetize the upstream codebase as-is** without making it clear this is a fork with substantial new value added. Users can always go use the free upstream version.
2. **A Pro tier only works if your fork offers enough unique value** that users choose to pay for your version over the free upstream. This means Phase 2 features (dedup, prompt library, auto-tagging) need to be genuinely differentiated and polished.
3. **Alternative model:** Instead of tiered pricing, consider a **donation/sponsor model** (GitHub Sponsors, Ko-fi) tied to your fork's unique features. The AI art community responds well to "support the developer" when value is clear.
4. **Or:** Contribute your features upstream via PR, build reputation, and monetize through consulting/customization rather than licensing.

**Action:** Decide the business relationship with upstream before investing 60 weeks of development. The entire roadmap's monetization assumptions depend on this.

---

## CORRECTION 1.1: The "Thumbnail Hang" is NOT a Thumbnail Loading Issue

The plan diagnoses a dispatcher deadlock in the thumbnail loading pipeline. **This is wrong.** I read the actual code at `ThumbnailView.xaml.cs:862`:

```csharp
// TODO : Randomly hangs?
DragDrop.DoDragDrop(source, dataObject, DragDropEffects.Move | DragDropEffects.Copy);
```

This is a **WPF `DragDrop.DoDragDrop()` hang**, not a thumbnail loading bug. `DoDragDrop` is a synchronous, blocking Win32 OLE drag-and-drop call that enters a modal message loop. It's a **known WPF issue** that can hang when:

- The drag source and drop target are in the same visual tree and there's a reentrancy issue
- An exception is thrown inside a drag event handler (silently swallowed by OLE)
- Another modal operation (tooltip, context menu) is active when the drag starts
- Touch/pen input interferes with mouse drag detection

**Corrected approach:**
1. Wrap `DoDragDrop` in a `Dispatcher.BeginInvoke` with `DispatcherPriority.Background` to decouple it from the mouse event
2. Add a minimum drag distance check before initiating (prevent accidental drags)
3. Add a try-catch around the call — OLE drag exceptions are notoriously silent
4. Test with touch-enabled displays if applicable

**Corrected effort:** 2–5 days, not 2–3 weeks. This is a targeted fix, not a system-wide profiling exercise.

**Corrected severity:** Medium, not Critical. The app doesn't hang during normal browsing — only when the user initiates a drag-and-drop on thumbnails. Most users may never encounter it.

---

## CORRECTION 1.2: Temp Table Fix — Also Fix the Logging and SELECT *

The plan correctly identifies the N-query loop. But reading the actual code at `DataStore.Image.cs:642-676`, there are two additional problems:

```csharp
foreach (var path in pathsList)
{
    Logger.Log($"GetImageIdsByPaths: Querying for path: {path}");           // <-- logging EVERY path
    var images = db.Query<Image>("SELECT * FROM Image WHERE Path = ?", path); // <-- SELECT * not SELECT Id
    var image = images.FirstOrDefault();
    // ...
    imageIds.Add(0); // Or skip it entirely  // <-- adds 0 for missing, causing FK errors downstream
}
```

**Three fixes needed, not one:**
1. Chunked `IN` clause (as planned) — but use `SELECT Id, Path FROM Image` not `SELECT *`
2. Remove per-path logging — this writes thousands of log lines during CivitAI imports, which itself causes I/O bottleneck
3. Stop adding `0` for missing images — filter them out. The downstream album assignment code hits FK constraint errors because it tries to insert `ImageId=0`

**Corrected effort:** 3–5 days. This is a focused database method fix.

---

## CORRECTION 1.3: Search — SQLite FTS5 is Simpler Than Vocabulary Tables

The plan proposes building a custom vocabulary index with trigram tables and TF-IDF. This is over-engineered. **SQLite already has FTS5** (Full-Text Search), which is purpose-built for this exact problem.

Looking at the actual code at `DataStore.Search.cs:486-495`:

```csharp
// Loads ALL prompts into memory, then loops with HammingDistance()
allResults = db.Query<UsedPrompt>(query).ToList();
foreach (var result in allResults.Where(r => r.Prompt != null && r.Prompt.Length >= prompt.Length))
{
    if (HammingDistance(prompt, result.Prompt) <= distance)
        yield return result;
}
```

The `HammingDistance` method (line 619) is a character-by-character comparison — it counts positions where characters differ. This is O(n * m) where n = number of prompts and m = average prompt length. At 50k images it loads ALL prompts into RAM and compares each one.

**Corrected approach:**
1. Create an FTS5 virtual table: `CREATE VIRTUAL TABLE PromptFTS USING fts5(Prompt, content=Image, content_rowid=Id)`
2. Populate it during migration: `INSERT INTO PromptFTS(rowid, Prompt) SELECT Id, Prompt FROM Image`
3. Keep it synced via triggers or during the scan write pipeline
4. Replace the hamming loop with: `SELECT * FROM PromptFTS WHERE Prompt MATCH ?` for keyword search
5. For fuzzy matching, use FTS5's built-in `NEAR` operator or prefix queries
6. Keep the existing hamming distance as a "similarity search" option for users who specifically want character-level fuzzy matching (niche use case)

**Corrected effort:** 1–2 weeks, not 3–4. FTS5 is a well-documented SQLite feature. No custom tables, no trigrams, no TF-IDF.

---

## CORRECTION 1.4: Scanner Parallelism — Already Well-Architected

The plan says the scanner is "hardcoded to 2 workers." This is correct — `MetadataScannerService.cs:29`:

```csharp
private readonly int _degreeOfParallelism = 2;
```

But the architecture is already solid: it uses `Channel<FileScanJob>` with N consumer tasks (`ProcessTaskAsync`). The fix is literally changing one number or making it configurable. The plan's approach of "adaptive I/O monitoring" is over-engineering.

**Corrected approach:**
1. Change `_degreeOfParallelism` from `2` to `Math.Max(2, Environment.ProcessorCount / 2)` with a cap of 8
2. Or: add a settings dropdown (as the plan suggests)
3. That's it. The channel-based architecture already handles the rest.

**Corrected effort:** 1–3 days, not 2–3 weeks. The infrastructure is already built correctly.

**Important caveat:** The real bottleneck might be the `DatabaseWriterService`, which uses a single writer channel. Increasing scanner parallelism could create a queue backlog if writes can't keep up. Test before shipping.

---

## CORRECTION 2.1: Perceptual Hashing — Effort is Overestimated

4–6 weeks for perceptual hashing is inflated. dHash is ~20 lines of C#:

```csharp
// dHash: resize to 9x8, grayscale, compare adjacent pixels = 64-bit hash
using var bitmap = new Bitmap(image, 9, 8);
ulong hash = 0;
for (int y = 0; y < 8; y++)
    for (int x = 0; x < 8; x++)
        if (bitmap.GetPixel(x, y).GetBrightness() > bitmap.GetPixel(x + 1, y).GetBrightness())
            hash |= 1UL << (y * 8 + x);
```

The real work is the UI for browsing duplicate groups and the backfill migration. But even with that:

**Corrected effort:** 2–3 weeks total:
- 2–3 days: dHash computation + database column + migration
- 3–5 days: "Find Duplicates" window with grouping
- 2–3 days: "Find Similar" context menu
- 2–3 days: testing, threshold tuning

---

## CORRECTION 2.2: Prompt Library — Scope It Down

The plan describes drag-and-drop composition, variable placeholders, and template previews. For a solo dev on a fork, this is a 3-month project, not 5–7 weeks.

**Corrected scope for V1:**
1. "Save as Template" button on any prompt (name + prompt + negative + model + params)
2. Templates page: list, search, copy-to-clipboard
3. Categories: simple tag-based, not a full sidebar
4. Skip: drag-and-drop composition, variable placeholders, ComfyUI integration

**Corrected effort:** 3–4 weeks for V1. Add composition features in a V2 if users ask for them.

---

## CORRECTION 2.3: Auto-Tagging — The Hardest Item, Should Move to Phase 3

WD-Tagger integration in a .NET WPF app is significantly harder than described:

1. **Model size:** WD-Tagger v3 models are **~400-600MB** (SwinV2 variant), not 300MB. The ViT variant is smaller (~300MB) but less accurate.
2. **ONNX Runtime native binaries** add ~150MB to the distribution for CPU-only. GPU support adds more.
3. **Image preprocessing** must exactly match the model's training pipeline (resize to 448x448, RGB normalization with specific mean/std values, PIL-compatible resampling). Getting this wrong produces garbage tags.
4. **Total distribution size increase:** ~500MB minimum. For a tool that's currently ~50MB, this is a 10x bloat.
5. **Cold start:** First inference takes 5-15 seconds on CPU while the model loads.

**Corrected recommendation:** Move auto-tagging to Phase 3. It's high risk, high complexity, and high distribution cost. The Prompt Library and Smart Duplicates are more achievable and already justify a Pro tier.

**Alternative approach:** Instead of bundling the model, offer integration with existing tagger tools (e.g., launch a1111-sd-webui-wd14-tagger as external process, read output). This reduces the integration to a few days but requires users to have the tagger installed separately.

---

## CORRECTION 3.4: Embedding Search — Drop It Entirely

Embedding-based semantic search in a desktop WPF app is a research project, not a product feature. It requires:
- CLIP model (~400MB additional)
- Vector similarity search (SQLite-vss is experimental and poorly documented for .NET)
- Image preprocessing pipeline (same issues as auto-tagging)
- Significant RAM overhead (embeddings for 100k images = ~200MB in memory)

**Recommendation:** Drop this from the roadmap. If auto-tagging ships (Phase 3 corrected), users can search by tags, which covers 90% of the use case. True semantic search is a post-1.0 moonshot.

---

## CORRECTION 4.3: Cloud Sync — Scope to Export/Import Only

Full cloud sync is a product in itself. Conflict resolution, multi-device merge, real-time sync — each of these is a multi-month project. For a solo developer:

**Corrected scope:** Only build Export/Import:
1. Export: dump albums, tags, ratings, and templates to a JSON file
2. Import: merge from JSON into existing database (with conflict UI: keep mine / keep theirs / merge)
3. Users can put the JSON file in their own Dropbox/OneDrive manually

**Corrected effort:** 2–3 weeks, not 8–12. Drop "Cloud Sync" from the name entirely.

---

## CORRECTION 4.4: Plugin System — Premature, Drop It

The existing "External Applications" system (10 configurable tools with command-line args) already covers the plugin use case for 95% of users. Building a formal plugin API with assembly loading, manifests, and security sandboxing is:

1. Massive engineering effort for a solo dev
2. Requires stable internal APIs (which don't exist yet — the God Object is still there)
3. Creates a support burden (broken plugins blamed on the host app)
4. Has minimal ROI — the AI art community writes Python scripts, not .NET assemblies

**Recommendation:** Drop plugin system from the roadmap. Instead, expand the External Applications feature to support stdin/stdout communication (pipe selected image paths to external scripts, read tags back). This covers Discord webhooks, DeviantArt uploads, etc. — effort: 1 week.

---

## CORRECTED God Object Refactor Assessment

The plan calls this "ongoing" at "1–2 days per sprint." This is realistic for a team. For a solo dev where every change must maintain backward compatibility with the upstream, it's harder.

**Corrected recommendation:** Don't refactor for the sake of refactoring. Only extract a service when you're actively building a feature that touches that area. The partial class split already provides logical separation — it's not as bad as the plan implies. The services layer (`AlbumService`, `TagService`, `ComfyUIService`, etc.) already exists and is well-structured. The main gap is that MainWindow still owns too much UI orchestration, but this is normal for WPF apps that predate proper MVVM frameworks.

---

## CORRECTED Timeline Summary

| Phase | Original | Corrected | Key Changes |
|-------|----------|-----------|-------------|
| **Phase 1** | 8–12 wk | **3–4 wk** | Drag-drop fix is days not weeks; FTS5 replaces custom indexing; scanner fix is one line |
| **Phase 2** | 10–15 wk | **6–8 wk** | dHash is simpler; prompt library scoped down; auto-tagging moved out |
| **Phase 3** | 12–18 wk | **10–14 wk** | Auto-tagging moved here; embedding search dropped |
| **Phase 4** | 10–15+ wk | **3–5 wk** | Cloud sync scoped to export/import; plugin system dropped |

**Total corrected: ~22–31 weeks** (vs original 40–60+ weeks)

**Time to first "Pro" value: ~10 weeks** (end of Phase 2, with dedup + prompt library)

---

## CORRECTED ROI Ranking

| Rank | Item | Corrected Effort | Impact | Phase |
|------|------|-----------------|--------|-------|
| 1 | Temp table + logging fix | 3–5 days | Bulk ops stop failing | 1 |
| 2 | Scanner parallelism | 1–3 days | Faster imports | 1 |
| 3 | DragDrop hang fix | 2–5 days | Drag reliability | 1 |
| 4 | FTS5 search | 1–2 weeks | Large library search | 1 |
| 5 | Smart Duplicate Finder (dHash) | 2–3 weeks | Pro-tier flagship | 2 |
| 6 | Prompt Library (V1) | 3–4 weeks | Power users | 2 |
| 7 | Smart Albums | 3–4 weeks | Organization | 3 |
| 8 | A/B Comparison View | 5–7 weeks | Quality iteration | 3 |
| 9 | Batch Export | 2–3 weeks | All users | 3 |
| 10 | Auto-Tagging (WD-Tagger) | 5–8 weeks | Transforms the app | 3 |
| 11 | Analytics Dashboard | 6–8 weeks | Insights | 3 |
| 12 | Export/Import | 2–3 weeks | Portability | 4 |
| 13 | Workflow Provenance | 7–10 weeks | Unique feature | 4 |
| ~~14~~ | ~~Plugin System~~ | ~~dropped~~ | ~~Premature~~ | ~~—~~ |
| ~~15~~ | ~~Embedding Search~~ | ~~dropped~~ | ~~Research project~~ | ~~—~~ |
| ~~16~~ | ~~Cloud Sync (full)~~ | ~~dropped~~ | ~~Solo dev can't maintain~~ | ~~—~~ |
