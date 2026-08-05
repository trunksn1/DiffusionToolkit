# CivitAI Calendar & Posting — Plan

> **Status: implemented.** Every section below is in the code as of the commit
> that follows this document; the build is clean and the Python changes are
> exercised by the checks in "Verification". Two things remain measurements
> rather than facts, both handled tolerantly rather than assumed:
> the exact names of CivitAI's stats fields, and whether `/v1/image-upload`
> accepts an OAuth token. `probe_stats.py` answers both in about two minutes —
> run it when convenient and paste the summary into
> `CIVITAI_OAUTH_IMPLEMENTATION_PLAN.md`'s verified table.
>
> Two aesthetic requests arrived after the plan was written and are also
> implemented: a stronger "today" marker, and greying out days past CivitAI's
> 90-day scheduling ceiling. See "Calendar date affordances" at the end.
>
> Two decisions were taken during implementation, on request:
> the WebView2 uploader stays the **default** for "Post to CivitAI" and the API
> path is a second menu item; and the OAuth scope mask is **unchanged**, so a
> failed upload reports the orphaned draft's URL instead of deleting it.

Four related pieces of work, all on top of the OAuth client added in
`Services/CivitaiOAuthService.cs`:

- **A.** Make the calendar genuinely OAuth-native (it is only half-way there).
- **B.** "Open in app" should reveal the image in the Search page, not launch
  the Windows photo viewer.
- **C.** Show reaction / comment / collection / tip counts in the calendar.
- **D.** Replace the WebView2 "Post to CivitAI" window with the internal API
  path that scheduling already uses.

Everything below is written against the code as it stands on
`feature/metadata-overlay` (commit `79d0309`). Claims marked **verified** come
from `probe_oauth.py`'s live run on 2026-08-04 (recorded in
`CIVITAI_OAUTH_IMPLEMENTATION_PLAN.md`); claims marked **unverified** need a
probe before any code is written — the same discipline that made the OAuth work
land cleanly.

---

## Answers up front

| Question | Answer |
| --- | --- |
| Do the calendar buttons work with OAuth? | **Reads yes, with two gaps.** Fetch Upcoming / Recover History already send the token; Download Missing never needed auth. But username resolution and drag-to-schedule still fall back to the API key / cookies. |
| Is "Open in App" doing what you wanted? | No — it shell-executes the file. Opening it in the Search panel is straightforward; `path:` search already exists in the query language. |
| Can the calendar show reactions / buzz / comments / collections? | Yes, but none of it is fetched today. Needs a probe, then a cache-schema bump. |
| Does "Post to CivitAI" work thanks to OAuth? | No. It is a WebView2 browser window that automates the website — it does not touch OAuth at all and never will. |
| Do I need to change the OAuth app's permissions? | **Almost certainly not.** The app is registered with Full Access; what limits us is the scope mask constant in the code (`131169`), and posting (bit 64) is already in it. |
| Can posting be done internally instead of via the browser? | Yes — the code to do it already exists in `posts_fetcher.schedule_post`. One handshake step needs probing first. |

---

## A. OAuth coverage of the calendar

### What already works

`CivitaiPostsService.RunPythonAsync` (`Services/CivitaiPostsService.cs:144-171`)
refreshes the token and passes it as `CIVITAI_ACCESS_TOKEN`. On the Python side
`api_client.py::_auth_get` prefers it for `family='trpc'`
(`_credentials_for`, `api_client.py:136-144`). Every read the calendar makes —
`post.getInfinite`, `image.getInfinite` — is tRPC, and OAuth on tRPC is
**verified**.

- **Fetch Upcoming** → `main.py posts --from …` → OAuth. ✅
- **Recover History** → `main.py posts --full --from …` → OAuth. ✅
- **Download Missing** → pure C# `HttpClient` against the public image CDN
  (`CivitaiPostsService.cs:265-322`). No credential of any kind is sent, by
  design. Unaffected by OAuth, works signed-in or not. ✅

> These three buttons have since been merged into one `⬇ Download…` dialog (see
> "The Download dialog" below). The credential story per operation is unchanged
> — only which button starts it.

### Gap A1 — username resolution can't use OAuth (blocking for key-less users)

`posts_fetcher._resolve_username` (`posts_fetcher.py:101-132`) tries, in order:

1. `client.test_api_key()` → returns `None` immediately when no API key is set
   (`api_client.py:359-360`).
2. `GET /api/auth/session` through `_auth_get(family='rest')` — and
   `_credentials_for('rest')` offers **only the API key**, so with OAuth alone
   this silently degrades to the cookie jar.

Consequence: a user who signs in with OAuth and deletes their API key gets
`"Could not determine your CivitAI username"` and the whole fetch aborts —
unless stale cookies happen to still work. This is the one place where the
calendar is not yet OAuth-capable.

**Fix (chosen):** the C# side *already knows* the username — `SignInAsync`
stores it from `/userinfo` (`CivitaiOAuthService.cs:284-316`,
`ConnectedUsername`). Pass it down. `main.py posts` already accepts
`--username` (`main.py:669`), and a username is not a credential, so putting it
on the command line is fine even though arguments are logged.

- `CivitaiPostsService.FetchUpcomingAsync` / `RecoverHistoryAsync`
  (`CivitaiPostsService.cs:78-92`): append
  `--username "{ServiceLocator.CivitaiOAuthService?.ConnectedUsername}"` when
  non-empty.
- Leave `_resolve_username` untouched as the fallback for API-key and
  cookie users.

**Fix (secondary, cheap):** also let `/api/auth/session` try the OAuth token.
The plan doc records that this endpoint accepts Bearer regardless of scopes, but
that was measured with an API key, so treat it as **unverified**. Add a third
family (`'session'`) whose credential list is `[OAuth, API key]` rather than
widening `'rest'`, which must keep rejecting OAuth so `/v1/*` doesn't waste a
401 per call.

### Gap A2 — every write still requires cookies

`posts_fetcher._trpc_mutate` (`posts_fetcher.py:77-91`) calls
`client._ensure_cookies()` and posts with the raw session. So drag-to-schedule
(`post.create` → `post.addImage` → `post.update`) is cookie-only, even though
`post.create` over OAuth with scope bit 64 is **verified working**.

**Fix:** give `_trpc_mutate` the same Bearer-first / cookie-fallback shape as
`_auth_get` — add `_auth_post(url, family, json=…)` to `CivitAIClient`, memoized
in the same `_bearer_ok` map, and route `_trpc_mutate` through it. This is the
single change that makes scheduling work without any cookie file, and it is a
prerequisite for section D.

One step in the chain is *not* tRPC: the `POST {api_base}/v1/image-upload`
handshake (`posts_fetcher.py:651`). REST v1 rejects OAuth tokens
(**verified** for `/v1/users/me`), so this may still need the API key or
cookies. See D for the probe.

### Gap A3 — auth-failure guidance is stale in one place

`CivitaiPostsService.AuthFailureMessage` already leads with "Connect CivitAI
Account". Fine. But `main.py:178` still prints only the `CIVITAI_API_KEY` hint.
Cosmetic; fold it into the same commit.

---

## B. "Open in app" → reveal in the Search panel

Today both buttons — the day-row one (`CivitaiCalendar.xaml:286`) and the
preview one (`:319`) — call `OpenLocalFile`, which is
`Process.Start(path) { UseShellExecute = true }`
(`CivitaiCalendar.xaml.cs:558-564`). That hands the file to the default image
viewer, which is not what "in app" should mean.

### The mechanism that already exists

- The query language supports `path:` with an optional criteria word —
  `QueryBuilder.cs:18`:
  `path: [starts with|contains|ends with] "<value>"`.
- The Search page exposes `SetQuery(string)` (`Search.xaml.cs:2072`) and
  `SetView(mode, context)` (`:1752`), and `SmartAlbum_OnClick` (`:2085-2109`)
  is a working precedent for "switch to images view, run a custom query".
- Pages talk to Search through `SearchService` events, never directly — the
  `Search` instance is private to `MainWindow` (`MainWindow.xaml.cs:69`).
  `OpenPath` (`SearchService.cs:77-80` → `Search.xaml.cs:252`) is the pattern to
  copy.

### Implementation

1. **`Services/SearchService.cs`** — add
   `public event EventHandler<string> ShowImagePath;` and
   `public void ExecuteShowImagePath(string path) => ShowImagePath?.Invoke(this, path);`
2. **`Pages/Search.xaml.cs`** — subscribe next to the existing `OpenPath`
   handler (`:252`) and implement:
   ```csharp
   private void ShowImagePath(string path)
   {
       SetView("images", Path.GetFileName(path));
       SetQuery($"path: \"{path}\"");
       SearchImages(null);
   }
   ```
   `path:` with no criteria word is an exact match, so a full path yields the
   one file. `SetView` does not clear `SearchText` (the line that did is
   commented out at `:1776`), so the order above is safe.
3. **`Pages/CivitaiCalendar.xaml.cs`** — replace `OpenLocalFile`:
   ```csharp
   private static void ShowInSearch(ResolvedPostImage image)
   {
       if (image.LocalPath == null || !File.Exists(image.LocalPath)) return;
       ServiceLocator.NavigatorService.Goto("search");
       ServiceLocator.SearchService.ExecuteShowImagePath(image.LocalPath);
   }
   ```
   `Goto("search")` first: `Search.Navigate` runs `SetView("images")` +
   `SearchImages(null)` (`Search.xaml.cs:169-188`), which would otherwise wipe
   the query we just set.
4. **`Pages/CivitaiCalendar.xaml`** — relabel both buttons to
   **"Show in Search"**; keep the `LocalPath`/`SelectedImageHasLocal` visibility
   bindings as they are.
5. Keep shell-open reachable — move it to a right-click context menu item
   ("Open with default viewer") on the same rows, so nothing is lost.

**Detail worth deciding:** after the search runs, the single result is not
auto-selected, so the right-hand preview stays empty until you click it. If you
want the metadata panel populated immediately, `SearchImages` would need a
"select first result" follow-up. I'd add it — a one-image result set with
nothing selected looks broken. It rides on `UpdateResults()`
(`Search.xaml.cs:926`) completing, so it must be a continuation, not a
same-tick call.

---

## C. Reactions, comments, collections, buzz in the calendar

### Current state: none of this data is fetched

`posts_fetcher._extract_post_images` (`:155-167`) keeps exactly five fields per
image — `id`, `name`, `url`, `width`, `height`, `nsfwLevel` — and the C# cache
model mirrors that (`CivitaiPostImage`, `CivitaiPostsService.cs:597-605`).
There is no stats anywhere in the pipeline. So this is a data-plumbing job
first and a XAML job second.

### C1 — Probe (must come first)

CivitAI's `image.getInfinite` items are widely understood to carry a `stats`
object with per-reaction all-time counters, but **we have not measured it** and
this repo's rule is to measure. Extend `probe_posts.py` (or add
`probe_stats.py`) to dump the raw keys of one image item and one post item, with
an OAuth token, on both `civitai.com` and `civitai.red`. Specifically confirm:

- Which of `likeCountAllTime`, `heartCountAllTime`, `laughCountAllTime`,
  `cryCountAllTime`, `dislikeCountAllTime`, `commentCountAllTime`,
  `collectedCountAllTime`, `tippedAmountCountAllTime`, `viewCountAllTime` are
  present, and their exact names.
- Whether tip/buzz amounts come back under Media & Posts Read (32) or need a
  **Buzz Read** bit — the registration UI lists Buzz as a separate resource, and
  its bit value is unknown.
- Whether `post.getInfinite` items carry post-level stats, which would save a
  per-post `image.getInfinite` round trip.
- Whether queued/unpublished posts return zeroed or absent stats (they should —
  handle absence, never assume zero means zero).

Record the findings in `CIVITAI_OAUTH_IMPLEMENTATION_PLAN.md`'s verified table,
the same way the OAuth bits were.

### C2 — Pipeline

- `posts_fetcher._extract_post_images`: pass through a `stats` sub-dict with the
  confirmed keys, defensively (`img.get("stats") or {}`), missing → `None` not
  `0`.
- Bump `CACHE_VERSION` to `2` (`posts_fetcher.py:25`). Old caches must not be
  read as "everything has zero reactions" — the C# loader should treat a v1
  cache as "stats unknown" and hide the badges until the next fetch, rather than
  forcing a full history re-fetch.
- C#: add a `CivitaiImageStats` class, an optional `Stats` property on
  `CivitaiPostImage` and on `ResolvedPostImage`, plus computed helpers:
  `TotalReactions` (sum of the reaction counters), `HasStats`, and
  pre-formatted strings so the XAML stays free of converters.

### C3 — UI

Both panels bind to the same `ResolvedPostImage`, so this is purely template
work:

- **Day view rows** (the upper panel, `CivitaiCalendar.xaml:246-296`) — one
  compact line under the filename: `♥ 128 · 💬 4`. Total reactions only, plus
  comments. Collapse the whole line when `HasStats` is false.
- **Preview panel** (the lower panel, `:307-331`) — a full breakdown strip above
  the existing button row: `👍 n 👎 n ❤ n 😂 n 😢 n`, then `💬 n`, `🗂 n`
  (collections), `⚡ n` (buzz tipped). Hide individual chips whose value is 0 so
  the strip doesn't turn into a wall of zeros; show the whole strip only when
  `HasStats`.
- **Month cells** — deliberately *not* touched. At 40×40 thumbnails there is no
  room, and the cell already carries a count badge and the ⏱ marker.

**Refresh semantics to be explicit about:** stats are a snapshot from the last
fetch, not live. The panel should say so — reuse the existing
`Last updated: …` line (`CivitaiCalendar.xaml.cs:145`) rather than implying the
numbers are current.

---

## D. Posting to CivitAI without the browser window

### Why OAuth does nothing for the current button

`CivitaiPostService.PostImage` (`Services/CivitaiPostService.cs:15-43`) opens
`CivitaiUploadWindow`, a WebView2 that navigates to
`https://civitai.red/posts/create` and injects the file into the page's
`<input type="file">` with JavaScript (`CivitaiUploadWindow.xaml.cs:52`,
`:93-140`). It authenticates as *the embedded browser's own cookie jar*, stored
under `%APPDATA%/DiffusionToolkit/CivitAI`. It never sees the OAuth token, and
there is no way to give it one — it is website automation, not an API client.

It is also fragile by construction: it depends on CivitAI's DOM, it can only
handle one image per window (`PostImages` uploads the first and tells you to
repeat), and it cannot report success back to the app.

### The internal path already exists

`posts_fetcher.schedule_post` (`:697-765`) does the whole thing over the API:

```
post.create  →  /v1/image-upload handshake  →  PUT bytes  →
post.addImage (per file, indexed)  →  post.update (title + publishedAt)
```

with rollback via `post.delete` on failure. It handles multiple files in one
post, reports progress, and returns a structured result. The Schedule tab in the
metadata panel (`Controls/MetadataPanel.xaml.cs:121-189`) already drives it.
"Post now" is the same call with `publishedAt = now`.

### D1 — Probe the one unknown

The upload handshake is `POST {api_base}/v1/image-upload` — REST v1, the family
that **verified** as rejecting OAuth tokens. Before committing to an
OAuth-only posting path, measure:

- Does `/v1/image-upload` accept `Authorization: Bearer <oauth token>`?
- If not, is there a tRPC equivalent the website uses?
- Does the pre-signed `uploadURL` returned by the handshake accept a plain
  unauthenticated `PUT`? (It should — it's pre-signed — but the current code
  sends it through the cookie session, so this has never been isolated.)
- Do `post.addImage` and `post.update` accept Bearer, as `post.create`
  **verified** it does?

If the handshake refuses OAuth, posting keeps a hard API-key-or-cookie
dependency and that must be stated plainly in the UI rather than discovered as a
mystery failure.

### D2 — Implementation

1. `main.py`: add `--publish-now` to the `schedule-post` subparser (or a `post`
   subcommand aliasing it) that skips `--publish-at` and sets `publishedAt` to
   now. Argument order rules still apply — globals before the subcommand.
2. `CivitaiPostsService`: add
   `PostNowAsync(IEnumerable<string> filePaths, string? title, …)` next to
   `SchedulePostAsync` (`:108`); identical plumbing, already token-aware.
3. Rewrite `CivitaiPostService.PostImage`/`PostImages` to call it, with a
   progress toast and a real success/failure result, and to add the posted
   images to the `Posted` album via the existing `MarkImagesAsPosted`
   (`CivitaiPostsService.cs:350`).
4. Keep `CivitaiUploadWindow` in the tree behind an "Open the CivitAI uploader
   in a browser window" fallback — useful when the API path fails or when you
   want the site's own tagging/resource UI. Do not delete it in the same commit
   that replaces it.

**Is internal actually better?** Yes, for this use case: no DOM dependency,
multi-image posts, progress in the app, structured errors, automatic album
tagging, and the same code path the calendar already trusts. The browser window
keeps one genuine advantage — CivitAI's own post editor (resource tagging,
NSFW rating, techniques) — which the API path would have to reimplement field
by field. That is the argument for keeping it as a fallback rather than an
argument against the change.

---

## Do the OAuth application's permissions need changing?

**No, not the registration.** The screenshots show it registered with the *Full
Access* preset — every Read/Write/Delete box ticked. That is the ceiling.

What actually limits the app is the mask it *requests* at authorize time:
`CivitaiOAuthService.RequestedScope` = `1 | 32 | 64 | 131072` = `131169`
(`CivitaiOAuthService.cs:59`). CivitAI grants that verbatim and does **not**
fall back to the registration's permissions — omitting `scope` entirely fails
with `invalid_scope` (that's the third screenshot).

So changes, if any, are one-line edits to that constant, driven by the probes:

| Want | Needs | Status |
| --- | --- | --- |
| Read reactions/comments/collections counts | Media & Posts Read (32) | Already requested |
| Buzz / tip amounts | possibly a **Buzz Read** bit | Bit value unknown — probe |
| Create and publish posts | Media & Posts Write (64) | Already requested |
| Delete a post / roll back a failed upload | Media & Posts **Delete** | Not requested; `post.delete` currently 403s |
| Add posted images to a CivitAI collection | Collections **Write** | Bit value unknown — probe |

The rollback case deserves a decision: `schedule_post` tries `post.delete` when
an upload fails mid-way (`posts_fetcher.py:761`), and with the current mask that
returns 403, so failed attempts leave orphan drafts on the account. Either
request the delete bit, or stop pretending the rollback works and tell the user
which draft to clean up. **Increasing scope is a user-visible consent change**
— every existing user gets re-prompted — so bundle any mask change into a single
release, don't drip it.

---

## Sequencing

Each step is independently shippable and leaves the API-key path working.

1. **B — Show in Search.** Pure C#/XAML, no network, no probe. Immediate payoff.
2. **A1 — pass `--username`.** Two lines; unblocks key-less OAuth users.
3. **A2 — `_auth_post` Bearer-first mutations.** Makes scheduling cookie-free
   and is the prerequisite for D.
4. **C1 — stats probe**, then **C2/C3** if the fields are there.
5. **D1 — upload probe**, then **D2 — internal posting**, with the WebView2
   window demoted to a fallback.
6. Scope-mask change, if the probes demand one — last, and alone, because it
   re-prompts every user for consent.

## Verification

- **B:** click "Show in Search" on a matched image → Search page, one result,
  correct file. Then on an image whose path contains spaces, quotes, and
  non-ASCII (your library has plenty) — that's where `path:` quoting breaks.
- **A1:** remove the API key, delete every `civitai*_cookies.txt`, sign in with
  OAuth only, run Fetch Upcoming. It must succeed, not fall back.
- **A2:** same cookie-less state, drag an image onto a future day. Check the
  post appears on CivitAI at the right time.
- **C:** compare three posts' badge numbers against the website.
- **D:** post one image and a three-image set; confirm one post with three
  images in order, correct title, and both landing in the `Posted` album.
- Regression for all of the above: `DiffusionToolkit.log` — the Python exit code
  and the `auth_mechanism` line tell you *which* credential actually did the
  work, which is the thing that silently regresses.

## Calendar date affordances

Added after the original plan, both purely local (no network, no fetch):

- **Today** reads at a glance: a blue 3px border, a tinted cell background, the
  day number in bold blue, and a small `TODAY` label. **The day being viewed**
  gets the same treatment in green, plus a green dot beside the day number. The
  trigger order is `HasScheduled` → `IsToday` → `IsSelected`, so the day you are
  looking at always wins the border while today keeps its `TODAY` chip — the two
  never collapse into one ambiguous cell.

  **The precedence trap:** the first version of this set `BorderBrush`,
  `Background` and `BorderThickness` as attributes on the cell `Border` *and*
  in the triggers. A local value outranks every Style trigger in WPF, so the
  triggers ran and changed nothing — only `Opacity`, which had no local value,
  ever moved. The defaults now live in `Style` setters. If a day-cell trigger
  ever "does nothing" again, look for a local value on the element first.

- **Beyond the scheduling ceiling** is greyed (`OutOfRangeBrush`, reduced
  opacity) with a 🔒 glyph and a tooltip naming the exact last schedulable date.

  The ceiling is **3 calendar months**, not 90 days. From civitai's own
  `SchedulePostModal.tsx`: `maxDate = increaseDate(now, 3, 'months')`, i.e.
  `dayjs().add(3, 'month')`. The same file enforces a **floor** of
  `POST_MINIMUM_SCHEDULE_MINUTES = 60` — a post must be at least an hour out,
  so "in the future" is not sufficient validation. Both are client-side in
  civitai's form; no server-side max was found in `post.service.ts`, but
  matching the form is the right call — it is what a user's own browser would
  allow. Verified against `civitai/civitai@main` on 2026-08-05.

  Both limits live in one place — `CivitaiPostsService.MaxScheduleMonthsAhead` /
  `MinScheduleMinutesAhead`, with `LastSchedulableDate`,
  `EarliestSchedulableTime` and the two shared message strings — and are
  enforced in four: the month cell styling, drag-over and drop on the calendar,
  `SchedulePostWindow`, and the metadata panel's Schedule tab (both the date
  picker's `DisplayDateEnd` and the submit-time check, since a picker limit
  alone is not a validation).

## Images CivitAI kept no filename for

Matching a posted image to a library file is done by filename, which is all
CivitAI gives us. Some ingestion paths throw the filename away.

Measured 2026-08-05 against post `30113552`, submitted through a challenge
page: every one of its images comes back with `name: null` and a `metadata`
block holding only `nsfwLevelReason`, from `post.getInfinite`, from
`image.getInfinite`, authenticated and anonymous alike. An ordinary post from
the same account carries both the filename and a full `metadata` block
(`hash`, `size`, `width`, `height`). So the name is genuinely absent
server-side, not something a different endpoint or credential would reveal.

Consequence: **the file you uploaded can never be matched** for those images —
there is nothing to match it against. What can be matched is a copy downloaded
from CivitAI, so both ends use one deterministic stem,
`CivitaiPostsService.NamelessStem(id)` = `civitai-{id}`:

- `DownloadMissingAsync` writes the file under that stem.
- `ResolveMatches` looks nameless images up under that stem
  (`MatchNameFor`), extension-tolerantly, like any other name.

So the loop closes: Download → rescan → matched, tagged `Posted`, previewable
in-app. Before this, downloading a nameless image left it permanently unmatched
and the calendar kept telling the user it could never match — after they had
already done the only thing that would fix it.

## The Download dialog

The header used to carry three buttons (Fetch Upcoming, Recover History,
Download missing). They are one `⬇ Download…` button opening
`CivitaiDownloadWindow`, because the choices are not independent: engagement
counters are fetched over the same period as the posts they belong to, and the
period selector would otherwise have had to be duplicated per button.

- **What**: posts and scheduled queue · engagement counters · missing image
  files. Any combination; they run in that order, and a failed or cancelled
  fetch stops the chain rather than downloading against stale matches.
- **How far back**: 7 days · 30 days · 6 months · 1 year · everything (since
  the founding) · a date you pick. Applies to the first two only; the future
  queue is always fetched whole, and missing files are downloaded for whatever
  is already in the calendar.
- **Rebuild from scratch** maps to `--full`, replacing what Recover History did.
- Each option states its request cost in the dialog, and the summary box
  restates it for the actual selection. Engagement is one request *per post* —
  invisible unless it is written down, and the reason it stays opt-in.

`FetchUpcomingAsync` survives as the post-scheduling refresh (current month,
with engagement); everything else goes through `FetchPostsAsync(from,
withEngagement, rebuild)`.

## Risks

- **The stats fields may not be there**, or may be gated behind a scope we
  haven't identified. C is the one section that could come back "not feasible as
  specified" — hence probing before plumbing.
- **`/v1/image-upload` may hard-require the API key.** Then "OAuth replaces the
  key" stays untrue for posting, and the UI must say so.
- **Cache version bump.** Anything that reads `posts_cache.json` must tolerate
  both versions; a v1 cache read as v2 shows fabricated zeros, which is worse
  than showing nothing.
- **Scope re-consent.** Any change to `RequestedScope` invalidates the stored
  session's usefulness silently — the token keeps working with the *old* scope
  until re-authorized, so new features would 403 with no obvious cause. Compare
  `session.Scope` against `RequestedScope` on startup and prompt to reconnect
  when they differ.
