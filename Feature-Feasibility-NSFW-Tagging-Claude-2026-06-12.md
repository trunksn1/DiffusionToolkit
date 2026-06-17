# Feasibility — Local NSFW Screening & Auto-Tagging (Safe Mode + WD14 Tagger)

**Author:** Claude
**Date:** 2026-06-12
**Context:** Third design note for this fork (after the adversarial review and the manual-metadata-overlay design). Feasibility analysis only — **no code** — for two related features:
1. A **safe mode** that blurs/hides NSFW images, driven by *actual image content* rather than prompt keywords.
2. A **content tagger** that labels images with descriptive (booru-style) tags for easier searching and recollection.

The trigger was research showing Civitai uses **Amazon Rekognition + an open-source tagger (WD14-style)** with a confidence threshold. The question: can this project leverage the same approach?

---

## 1. Short answer

**Yes — and this is the most natural fit of the three features we've discussed, because the app already contains ~80% of the plumbing.** What's missing is the one hard part: an actual vision model that looks at the pixels. Everything *downstream* of that — the NSFW flag, the blur, the hide-from-results filter, a tag field, a tagging service, batch-update jobs with progress bars — is already built and shipping. You are not building a feature from scratch; you are replacing a weak detector with a strong one and adding a tag store.

And critically, this feature **directly fixes the blind spot in the manual-metadata feature**: images with no embedded metadata. The current NSFW detection can't see those at all (it reads the prompt text). A vision classifier doesn't care whether metadata exists — it looks at the image.

---

## 2. What already exists (and why that changes everything)

I went through the codebase before writing this. The relevant infrastructure already present:

| Capability | Where | Status |
|---|---|---|
| Per-image `NSFW` boolean flag | `Diffusion.Database/Models/Image.cs`, `DataStore` `SetNSFW` | ✅ working, persisted |
| **NSFW blur on thumbnails** | `ThumbnailView.xaml:219-227` — `BlurEffect` radius driven by a MultiBinding of the image's `NSFW` flag × global `NSFWBlur` toggle | ✅ working |
| Blur toggle + hotkey (**B**) | `MainWindow.xaml:27,379`, `ToggleNSFWBlur` | ✅ working |
| **Hide NSFW from results** (Ctrl+Shift+N) | `QueryBuilder.HideNSFW`, `MainWindow.xaml.Events.cs:112` | ✅ working |
| `TaggingService` + `TagType` enum + change events | `Services/TaggingService.cs` | ✅ working |
| Batch update job w/ progress | `MainWindow.xaml.Tools.cs` `UpdateByBatch`, `ProgressService` | ✅ working |
| `CustomTags` text column on Image | `Image.cs:39`; setter at `DataStore.MetaData.cs:191` | ⚠️ **commented-out stub** — half-built, unused |
| Current "AutoTag NSFW" | `MainWindow.xaml.Tools.cs:141` | ⚠️ keyword-only (see below) |
| Image decoding/resizing (SixLabors.ImageSharp 3.1.7) | already a dependency in `Diffusion.IO.csproj` | ✅ reusable for model preprocessing |

### The weakness this replaces

The current `AutoTagNSFW()` does this (`MainWindow.xaml.Tools.cs:157`):

```
ids = images where Prompt contains any of Settings.NSFWTags ("nsfw","nude","naked")
```

It's a **substring match on the prompt text**. That means it:
- **Cannot tag any image without metadata** — exactly the images you're trying to handle.
- Misses explicit images whose prompts don't happen to contain those three words.
- False-positives on safe images that mention the words.
- Depends entirely on the user's keyword list.

Replacing this single function's *input* (pixels instead of prompt text) is the whole NSFW feature. The blur, the flag, the filter — all already consume the result.

---

## 3. The right technical approach for this app

Civitai's stack is **Rekognition (cloud) + WD14 (local)**. For a *local desktop tool indexing a user's private image collection*, the cloud half is the wrong choice, and the local half is exactly right.

### 3.1 Why local, not Amazon Rekognition

- **Privacy.** These are private, often-NSFW personal collections. Uploading every image to AWS for moderation is a non-starter for a tool whose whole appeal is local ownership.
- **Cost.** Rekognition is per-image. A library of 50k–500k images turns into a real bill, and rescans re-bill.
- **Offline.** The app works without a network; a cloud dependency breaks that.
- **It needs AWS credentials**, key management, error handling for throttling, etc.

Recommendation: **local inference only.** (A cloud provider could be an *optional* pluggable backend later, but it should never be the default or a requirement.)

### 3.2 The local stack: ONNX Runtime + a WD-style tagger

This maps cleanly onto .NET with no Python and no subprocess:

- **ONNX Runtime for .NET** (`Microsoft.ML.OnnxRuntime`) — a first-class NuGet package, runs the model **in-process** in the existing C# app. No Python interpreter, no IPC, no bundled runtime hell. This is the single most important feasibility fact: the model runs natively inside DiffusionToolkit.
- **WD14 / WD-EVA02 ONNX tagger** (SmilingWolf's models are the de-facto standard, distributed as `.onnx` + a `selected_tags.csv` mapping output indices → tag names). One forward pass gives **per-tag probabilities** for thousands of booru tags, *and* a **rating head** (general / sensitive / questionable / explicit).
- **The elegant part:** a single WD14 model gives you **both features at once** — the descriptive tags *and* the NSFW rating come out of the same inference. You don't need two models. (You *can* add a dedicated NSFW classifier if you want a second opinion on the safety call, but it's optional.)
- **Preprocessing** (resize to the model's input e.g. 448×448, channel order, padding) is doable with **SixLabors.ImageSharp, which is already a dependency** — no new imaging library.
- **Threshold** works exactly as the research described: keep tags whose probability exceeds a tunable cutoff (WD's conventional default ~0.35–0.4). Expose it in Settings.

### 3.3 Performance and GPU

- On **CPU**, a WD14 inference is roughly 0.1–1 s/image depending on model size and core count. For a one-time pass over a large library that's a long background job — but the app *already* has the exact infrastructure for long background passes (the scanning pipeline, `UpdateByBatch`, `ProgressService`, cancellation).
- On **GPU**, the **DirectML execution provider** (`Microsoft.ML.OnnxRuntime.DirectML`) accelerates inference on any DirectX 12 GPU (NVIDIA/AMD/Intel) with no CUDA dependency. This is highly relevant — your users are running local image generation, so they have capable GPUs. This can turn "overnight" into "minutes."
- Tagging is a natural fit to **piggyback on the existing scan**: when a new image is scanned and hashed, optionally run the tagger then, so new images arrive pre-tagged and only a one-time backfill is needed for the existing library.

### 3.4 .NET 6 caveat

ONNX Runtime and the DirectML provider work on .NET 6, so nothing blocks this today — but this is one more reason to do the **.NET 8 migration** recommended in the first review (newer ORT builds, longer support, better perf). Not a blocker, just aligned.

---

## 4. Storage design for the tags

**Do not reuse the existing `CustomTags` string column.** It's a half-built stub (its setter is commented out at `DataStore.MetaData.cs:191`), and cramming many tags into one delimited string makes per-tag confidence, per-tag category, and tag search painful.

Recommended: a dedicated table.

```
ImageTag (
    Id, ImageId (FK), Tag, Category, Confidence, Source, CreatedDate
)
-- Category: 'rating' | 'character' | 'general' | 'artist' ...
-- Source:   'wd14' | 'manual'
-- indexes on (ImageId) and (Tag)
```

Design notes:
- These tags are **machine-derived from the file**, so unlike the *user-entered* overlay from the previous design note, they're safe to live in the main database and be **regenerated on demand** (re-tag with a better model later). They are not precious user input.
- Keep `Confidence` so the UI can sort/filter by it and so you can re-threshold without re-running the model.
- A `Source` column lets machine tags and any future hand-added tags coexist and be told apart.
- For the **NSFW flag specifically**: when the tagger's rating is `questionable`/`explicit` above a threshold, set the existing `Image.NSFW` flag — which instantly lights up the blur and hide features with zero new UI. Store the granular rating in `ImageTag` too, so "safe mode" can later distinguish "sensitive" from "explicit" if you want tiers.

---

## 5. How "Safe Mode" should work

The blur/hide pieces exist but are currently independent view toggles. A coherent **Safe Mode** is a small orchestration layer on top:

- A single persisted setting that, when on, forces **both** `NSFWBlur` *and* `HideNSFW`-from-results (or blur-only, user's choice) and ideally **defaults to on at startup** until the user turns it off — so the app opens safe.
- Optional: tiered behavior using the granular rating — e.g. blur `sensitive`, fully hide `explicit`.
- Optional (only if you actually want it): a lightweight lock/PIN to leave Safe Mode, for shared machines. Note honestly that client-side WPF "locks" are trivially bypassable (the data is right there in the SQLite file) — it's a speed-bump, not security. Don't oversell it.
- The accuracy of all this rides entirely on the classifier from §3 setting the `NSFW` flag well — which is the upgrade over today's keyword guess.

---

## 6. How tagging helps "recollection" (search)

To make tags useful for finding images, add a search term to `QueryBuilder` (the same place the existing `seed:`, `steps:` etc. terms live):

- `tag:catgirl` → `EXISTS (SELECT 1 FROM ImageTag t WHERE t.ImageId = Image.Id AND t.Tag = ? [AND t.Confidence >= threshold])`.
- Support multiple tags (AND/OR) and a confidence floor.
- Bonus: a **tag sidebar / tag cloud** showing the most common tags in the current result set, click-to-filter — cheap to build on top of the table and a big usability win for "I know I made a bunch of X" recollection.

This composes with the existing query language, so `tag:landscape steps:>30` style queries come essentially for free once the term exists.

---

## 7. Honest risks & caveats

- **Model files are large** (~300–700 MB) and **must not be bundled** in the app/installer. Download on first use into the app data folder, with a clear consent prompt and progress — same pattern you'd want for any model. Verify a checksum.
- **WD taggers are anime/illustration-oriented.** They're excellent for that vocabulary (which is most of what this audience generates) but weaker on photorealistic content. If your library is heavily photoreal, results will be patchier; a second general-purpose model could supplement. Set expectations accordingly.
- **NSFW classification is never perfect.** There will be false negatives (explicit image not flagged) and false positives. Safe Mode should be described as "best effort," and the existing **manual NSFW toggle** (already in `StarRating.xaml.cs:147`) stays as the human override. Let users re-run/adjust the threshold.
- **First full-library pass is heavy.** Make it a cancellable, resumable background job (skip already-tagged images by hash), not a modal that locks the app. The infra exists; use it.
- **Threading discipline:** ONNX inference must run off the UI thread (it already would, in the scan pipeline). An ONNX `InferenceSession` is thread-safe for concurrent `Run` calls, but you'll want a small degree-of-parallelism cap to avoid thrashing the GPU/CPU — mirror the scanner's existing parallelism approach.
- **Don't write tags into the image files**, consistent with the previous design note — keep them in the database where they're regenerable and non-destructive.

---

## 8. Suggested phasing

| Phase | Scope | Notes |
|---|---|---|
| 1 | New `Diffusion.Tagging` service wrapping ONNX Runtime + ImageSharp preprocessing; model download/management | the core enabler |
| 2 | NSFW rating → set existing `Image.NSFW` flag; wire a one-shot "Re-tag for NSFW (visual)" job replacing keyword AutoTag | instantly activates existing blur/hide |
| 3 | `ImageTag` table + descriptive tags written during the same pass | storage + capture |
| 4 | `tag:` search term + tag sidebar/cloud | the "recollection" payoff |
| 5 | "Safe Mode" orchestration setting (default-on, tiers, optional PIN) | UX layer |
| 6 (later) | DirectML GPU provider; tag-on-scan for new images; optional cloud backend | performance & polish |

Phases 1–2 alone replace the weakest part of the app (keyword NSFW detection) with real vision and light up features that already exist. Phases 3–4 deliver the searchable-tags goal.

---

## 9. Bottom line

- **Feasible and well-matched** — arguably the best-fitting of the three features discussed, because the consumption side (NSFW flag, blur, hide filter, tag service, batch jobs, ImageSharp) is **already built and shipping**. You're swapping a keyword guess for a vision model and adding a tag table.
- **Use the same approach Civitai's *local* half uses — a WD14-style ONNX tagger — run locally via ONNX Runtime for .NET**, in-process, no Python. One model yields both the NSFW rating and the descriptive tags.
- **Skip Rekognition / cloud** for the default: privacy, cost, and offline use all argue for local-only on a personal image tool.
- **Store tags in a dedicated `ImageTag` table** (regenerable machine data), not the stubbed `CustomTags` string; set the existing `NSFW` flag from the rating to reactivate the blur/hide UI for free.
- **It fixes the metadata-less blind spot**: unlike today's prompt-keyword detector, a vision model tags images that have no embedded metadata at all — the same images the manual-overlay feature targets.
- Main caveats: large model downloads (don't bundle), WD models skew anime over photoreal, NSFW detection is best-effort (keep the manual override), and the first full pass is a heavy background job — for which the infrastructure already exists.
