# Feature Design — Manual Metadata Overlay & Civitai Auto-Fetch

**Author:** Claude
**Date:** 2026-06-12
**Context:** Follow-up to `Adversarial-Review-Claude-2026-06-12.md`. Design and feasibility analysis for a user-requested feature: manually attaching metadata (prompt, sampler, etc.) to images whose files contain no embedded metadata, without ever touching the values parsed from the file itself — plus an optional "fetch it from Civitai" button.

---

## 1. The idea, restated

Many images (especially ones downloaded from Civitai) have had their embedded generation metadata stripped, but the creator published that metadata on the image's web page. The request:

1. Let the user attach metadata to such an image **manually**, as free-form parameter/value pairs.
2. Show it in a **new section of the right-side panel, under the preview**, behind an explicit button.
3. **Never write it into the image file**, and **never write it into the fields the scanner owns** (Prompt, NegativePrompt, etc. in the `Image` table) — those must keep reflecting what is actually in the file, and user data must survive rescans and app updates.
4. Store it in the **same SQLite database but in a separate table**.
5. Optionally: a button that fetches the metadata **automatically from the image's Civitai page**.

## 2. Verdict: good idea or stupid?

**It's a good idea, and your instincts about storage are correct.** This is a well-established pattern — it's exactly what XMP sidecar files are to photos and what Lightroom's catalog-only edits are to RAW files: a *non-destructive overlay* that never mutates the source of truth. The specific instincts worth endorsing:

- **Separate table = right call.** The scanner's rescan path rewrites columns on the `Image` table; anything you manually put there *would* be wiped on the next "Rescan" of that file. A separate table is untouched by rescans. It's also safe across app updates: upstream migrations only operate on tables they know about, and SQLite happily ignores extra tables — pulling a new upstream version will not drop or alter your table.
- **Keeping the parsed fields empty = right call.** Mixing "what the file says" with "what the user typed" in the same columns destroys your ability to ever tell them apart, and makes the UI lie about what's embedded in the file.

One correction to the plan, though, and it's the most important design decision in this document:

> **Do not key the overlay table on `Image.Id` or on the file path. Key it on the file's SHA-256 hash.**

The scanner already computes a SHA-256 of every file during scanning and stores it in **`Image.Hash`** (`Diffusion.Database/Models/Image.cs:61`, computed in `Metadata.cs:149` via `CopyAndHash`). Why hash instead of Id/path:

| Key | Survives file move/rename | Survives "Remove from database" + rescan | Survives full DB rebuild | Survives duplicate copies |
|---|---|---|---|---|
| `Image.Id` | yes | **no** (row deleted, new Id on rescan) | **no** | no |
| `Path` | **no** | yes | yes | no |
| `SHA-256 hash` | **yes** | **yes** | **yes** | **yes** (overlay applies to every copy — arguably a feature) |

Your stated fear is "if we update the program we risk losing the user's data." Keying by hash means the overlay reattaches itself to the image no matter what happens to the `Image` table — even if you delete `diffusion-toolkit.db`'s index and rescan everything from scratch, as long as the overlay table (or an export of it, §6) survives, every value finds its image again by content.

## 3. Before building: the zero-code alternative you should know about

DiffusionToolkit **already supports companion `.txt` files**: if `image.png` has no embedded metadata, the scanner looks for `image.txt` next to it and parses it as A1111-format parameters (`Diffusion.Scanner/Metadata.cs:477-506`). So today, with no code at all, you can paste the Civitai generation text into `image.txt`, rescan the folder, and the prompt becomes visible *and searchable*.

Why this is *not* a full substitute for the feature (and why the overlay is still worth building):

- It populates the same `Image` columns as embedded metadata, so the UI can't distinguish "really embedded" from "user-supplied" — which you explicitly don't want.
- It litters your image folders with `.txt` files.
- It only handles A1111-style parameter text, not arbitrary key/value pairs.
- The fuzzy filename match it uses (`StartsWith`, `Metadata.cs:492`) can attach the wrong txt to the wrong image.

But it's useful as a mental model: the overlay feature is essentially "sidecar files, but stored in a table, keyed by hash, and visually distinct."

## 4. Recommended design

### 4.1 Schema

One row per (image-hash, parameter) — an EAV layout, because you asked for arbitrary user-chosen parameter names:

```sql
CREATE TABLE UserMetadata (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    FileHash    TEXT NOT NULL,             -- SHA-256, matches Image.Hash
    Key         TEXT NOT NULL,             -- 'Prompt', 'Negative Prompt', 'Sampler', or anything
    Value       TEXT,
    Source      TEXT NOT NULL DEFAULT 'manual',   -- 'manual' | 'civitai'
    SourceUrl   TEXT,                      -- the Civitai page it came from, if fetched
    CreatedDate TEXT NOT NULL,
    UNIQUE (FileHash, Key)
);
CREATE INDEX IX_UserMetadata_FileHash ON UserMetadata (FileHash);
```

Notes:

- Created through the existing migration system (`Diffusion.Database/Migrations.cs`, a `[Migrate]`-attributed method following the `RupertAveryYYYYMMDD_NNNN_...` naming) so it plays nicely with the version tracking already in place. Name it with your own prefix so it never collides with an upstream migration.
- `UNIQUE(FileHash, Key)` makes writes a clean `INSERT ... ON CONFLICT(FileHash, Key) DO UPDATE`, the same upsert idiom the codebase already uses.
- A single-JSON-blob-per-image alternative (one row, `Json TEXT`) is simpler but loses per-key querying and makes the future search integration (§5) much uglier. EAV is the right shape here.
- Offer a **suggested-keys dropdown** in the editor (Prompt, Negative Prompt, Steps, Sampler, CFG Scale, Seed, Model, Size…) that still allows free text. This keeps key names consistent enough to be useful later without restricting the user.

### 4.2 Service

A small `UserMetadataService` registered in `ServiceLocator` like your `CivitaiPostService`:

```csharp
List<UserMetadataEntry> GetForHash(string fileHash);
void Upsert(string fileHash, string key, string value, string source, string? sourceUrl);
void Delete(string fileHash, string key);
void DeleteAll(string fileHash);
```

DB access goes through a new `DataStore.UserMetadata.cs` partial, consistent with the existing partial-per-domain convention. Use the cached read-only connection for reads.

### 4.3 UI

In `PreviewPane.xaml`, under the existing metadata display:

- A collapsed **"User metadata"** expander, with an **"Add metadata"** button shown when the section is empty. Per your request, the section is opt-in per image — nothing appears unless the user acts.
- Inside: a simple two-column list of Key/Value rows, each with edit/delete, plus an "Add parameter" row (key combo-box + value textbox).
- **Visual distinction is essential**: a small pencil/user icon and/or italic styling, and a tooltip "Manually added — not embedded in the image file." The whole point of the separate store is that the user can always tell the two apart; the UI must preserve that.
- If the entry came from Civitai, show a small link icon opening `SourceUrl`.
- Long values (prompts) need a multi-line editor — reuse the app's existing prompt-display styling.

One wiring detail: `PreviewPane`'s view model must expose the image's `Hash` (it already has Id/Path; Hash is one more property fetched with the image). For images scanned before hashing existed (very old DBs) `Hash` can be null — in that case compute it on demand with the existing `HashFunctions.CalculateSHA256` and store it back.

### 4.4 What rescans and updates do to this data: nothing

- **Rescan / Rebuild metadata:** rewrites `Image` columns only. `UserMetadata` untouched. ✔
- **Remove from database + rescan:** `Image` row deleted and recreated with a new Id; overlay reattaches via hash. ✔
- **App update / upstream merge:** upstream migrations don't know the table exists; SQLite ignores it. ✔
- **File moved/renamed:** hash unchanged; overlay follows. ✔
- The only thing that loses the data is deleting the database file itself — which §6 covers.

## 5. Search integration (optional, phase 2)

Out of the box, overlay metadata is *not* searchable — searching `prompt: castle` won't find an image whose prompt lives only in `UserMetadata`. Two options, in increasing effort:

1. **A dedicated search term**, e.g. `user: castle`, implemented in `QueryBuilder` as an `EXISTS (SELECT 1 FROM UserMetadata um WHERE um.FileHash = Image.Hash AND um.Value LIKE ?)`. Cheap, explicit, doesn't blur the embedded/manual distinction.
2. **Transparent fallback**: prompt searches also match `UserMetadata` rows with `Key = 'Prompt'`. More magical, but it re-blurs the distinction you're trying to keep — I'd skip it.

Recommendation: ship without search first; add option 1 when you feel the need.

## 6. Backup: export/import

Since the database file is the single copy of this hand-entered data, add a trivial **Export user metadata** / **Import** menu pair that dumps the table to JSON (`[{hash, key, value, source, sourceUrl, createdDate}]`). Twenty lines of code, and it makes the data indestructible — importable into any future rebuild of the database. Do this in v1; it's the cheapest insurance in the whole design.

## 7. The Civitai auto-fetch button

### 7.1 Honest feasibility assessment

The crucial constraint: **there is no public Civitai endpoint to look up an image by file content or hash.** Civitai's API lets you look up *models* by hash (the app already uses this — `CivitaiClient.GetModelVersionsByHashAsync`), but not images. So a fully automatic "press button, app finds the page by itself" is **not reliably possible** for arbitrary files. What *is* practical:

**Tier 1 — paste the URL (reliable, recommended for v1).**
The user right-clicks → "Fetch metadata from Civitai…", pastes `https://civitai.com/images/12345678`, and the app extracts the image id and fetches the generation data. UX cost is one paste; reliability is high. Implementation options for the fetch, in order of preference:

1. **Official REST API** (`Diffusion.Civitai` project already has the client plumbing): the `/api/v1/images` endpoint returns items whose `meta` object contains `prompt`, `negativePrompt`, `sampler`, `cfgScale`, `steps`, `seed`, `Model`, etc. Whether it supports direct single-image lookup by id has historically been undocumented/inconsistent — verify against the current API docs first; if it works, this is the clean path. Supports an API key header for rate limits.
2. **Unofficial tRPC endpoint** (what the website itself calls), e.g. the image generation-data route. Works, returns exactly what's displayed on the page, but it's unversioned and can change without notice.
3. **Scraping the HTML page** (the embedded Next.js JSON payload). Most fragile, last resort.

Build it as: try (1), fall back to (2), and wrap the whole thing behind one `FetchCivitaiGenerationData(imageId)` method so the strategy can be swapped when (not if) something breaks.

**Tier 2 — filename heuristic (nice-to-have).**
Files downloaded from Civitai often keep an identifiable name (frequently the media UUID, sometimes the numeric id). When the heuristic finds a usable id in the filename, the button can pre-fill or skip the paste step. Treat it as a convenience that silently falls back to Tier 1 — don't promise it.

**What I'd explicitly not do:** reverse-image search via third-party services to *find* the page automatically. It's slow, ToS-murky, and wrong often enough to corrupt your overlay data with confidently-fetched garbage.

### 7.2 Two practical caveats

- **NSFW/login-gated images** return nothing to anonymous API calls. You already have a persistent logged-in Civitai session in the WebView2 profile from the upload feature (`%APPDATA%\DiffusionToolkit\CivitAI`). A neat synergy: as a fallback, run the fetch *inside* an off-screen WebView2 using that profile, where the user's cookies grant access. Save this for a later iteration — it's the same fragility class as the upload automation.
- **ToS and rate limits:** using the documented REST API with an API key is sanctioned; hammering tRPC or scraping is tolerated-but-unsanctioned. This is a manual, one-image-at-a-time button, so volume is a non-issue — but resist the temptation to add "auto-fetch for all 5,000 images missing metadata" without rate limiting and an API key.

### 7.3 What the fetch writes

Fetched values go through exactly the same `UserMetadataService.Upsert` path as manual entry, with `Source = 'civitai'` and `SourceUrl` set, **never** into the `Image` columns — same overlay, different pen. Show a confirmation dialog listing the fetched key/values before saving, so the user can deselect junk; Civitai `meta` blobs often contain noisy extras (`"hashes"`, resource lists) you'll want filtered to a sane allowlist by default.

## 8. Implementation plan & effort

| Phase | Work | Effort |
|---|---|---|
| 1 | Migration + `UserMetadata` table + `DataStore.UserMetadata.cs` + service | ~half a day |
| 2 | PreviewPane section: expander, add/edit/delete rows, hash plumbing | ~1 day (the fiddly part is XAML, not logic) |
| 3 | Export/import JSON | ~1 hour |
| 4 | Civitai fetch, Tier 1 (paste URL → REST, tRPC fallback, confirm dialog) | ~1 day |
| 5 (later) | `user:` search term; filename heuristic; WebView2 cookie fallback | as needed |

Total for a solid v1 (phases 1–4): **roughly 2–3 days** of work, all additive — no upstream files need invasive changes beyond PreviewPane and the migration list, which keeps future upstream merges painless.

## 9. Pitfalls checklist

- **Null `Image.Hash`** on rows scanned by ancient versions → compute on demand, backfill.
- **Duplicate files** share a hash → overlay shows on all copies. Intended behavior; just don't be surprised.
- **Don't auto-open the section** for images that have overlay data but also have embedded data — overlay should *supplement* display, never visually override what the file actually says. Render embedded metadata first, overlay clearly second.
- **Editor must escape nothing** — values are bound parameters end to end (the codebase's existing `Bind()` style); no string-built SQL, ever, for user-typed values.
- **Localization**: add the new strings to `Localization/default.json` rather than hardcoding, unlike the "Post to CivitAI" menu item (see previous review).

## 10. Bottom line

- The idea is **good, not stupid** — it's the standard non-destructive sidecar/overlay pattern, and your two core instincts (separate table, never touch the scanner-owned fields) are exactly right.
- The one improvement over your plan as stated: **key by SHA-256 file hash, not by image id or path** — the hash is already in the database and it's the only key that survives every rescan, move, and rebuild scenario you're worried about.
- Add **JSON export/import** in v1 so the data survives even database deletion.
- The Civitai button is **feasible and worth building**, but as *paste-the-URL → fetch → confirm → save into the overlay*. A fully automatic content-based lookup isn't possible with Civitai's public API; the filename heuristic can quietly upgrade the UX where it applies.
- Know that the existing `.txt` companion-file support already gives you a searchable stopgap today, at the cost of folder clutter and losing the embedded-vs-manual distinction — fine as a bridge until the overlay ships.
