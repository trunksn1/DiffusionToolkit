"""
Fetches the user's own CivitAI posts (published + scheduled) into a JSON cache
for the Diffusion Toolkit "CivitAI Posts Calendar", and schedules new posts.

All network calls go through CivitAIClient (Bearer-first with cookie fallback
for reads; cookie session for write mutations). Endpoint shapes for scheduled
posts and uploads are validated by probe_posts.py; parsing here is defensive
so shape drift degrades to warnings instead of crashes.

Cache file (atomic write): %APPDATA%/DiffusionToolkit/Civitai/posts_cache.json
"""

import datetime
import json
import logging
import os
import struct
import sys
import time
from pathlib import Path
from typing import Any, Dict, List, Optional

logger = logging.getLogger(__name__)

CACHE_VERSION = 1
PAGE_DELAY_SECONDS = 0.5
INCREMENTAL_OVERLAP_DAYS = 7
# Hard ceiling per section scan. 400 pages x 50 = 20k posts - far beyond any
# real account, but finite if the endpoint misbehaves (bad cursor, ignored
# filters). Without this a bad response shape means an infinite loop.
MAX_PAGES_PER_SECTION = 400
# Queue flags are only trustworthy near the present: the anonymous feed hides
# some old NSFW posts, so deep-history diffs produce false "queued" flags.
# Only posts newer than this window (or future-dated) may be flagged.
QUEUE_FLAG_WINDOW_DAYS = 30
# Queued-post detection, verified live 2026-07-19:
# - post.getInfinite section="draft" is BROKEN server-side: it returns the real
#   pending posts first, then keeps paginating into the user's entire published
#   history (the civitai website's draft section shows the same wrong listing).
# - post.getEdit's wasPublished is False even for posts that are publicly
#   visible - useless as a signal.
# - The only reliable ground truth: what an ANONYMOUS request can see of the
#   user's feed is published; anything the authenticated fetch returns beyond
#   that is the pending queue. queued = authenticated ids - anonymous ids.
#
# FUTURE-scheduled posts (publishedAt > now), verified live 2026-07-19:
# - Feeds clamp publishedAt <= now UNLESS the input carries {"scheduled": true}
#   (per civitai's post.service.ts, the flag relaxes the clamp to include the
#   requesting user's own future posts). Works with Bearer AND cookie sessions.
# - Without that flag NOTHING helps: sections, draftOnly, synthetic future
#   cursors, and the SSR page all come back clamped.


def default_cache_path() -> Path:
    appdata = os.environ.get("APPDATA") or str(Path.home())
    return Path(appdata) / "DiffusionToolkit" / "Civitai" / "posts_cache.json"


def _parse_iso(value: Optional[str]) -> Optional[datetime.datetime]:
    if not value or not isinstance(value, str):
        return None
    try:
        dt = datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=datetime.timezone.utc)
        return dt.astimezone(datetime.timezone.utc)
    except ValueError:
        return None


def _trpc_query(client, procedure: str, input_json: Dict[str, Any], timeout=30):
    params = {"input": json.dumps({"json": input_json}, separators=(",", ":"))}
    return client._auth_get(f"{client.trpc_url}/{procedure}", family="trpc",
                            params=params, timeout=timeout)


def _trpc_mutate(client, procedure: str, input_json: Dict[str, Any], timeout=30,
                 meta: Optional[Dict[str, Any]] = None):
    """Write mutations use the cookie session (write-tRPC generally requires it).

    meta is the superjson annotation block: CivitAI's tRPC layer deserializes
    input with superjson, so typed values (e.g. Date) must be declared there
    or they arrive as plain strings and fail Zod validation. Example:
    meta={"values": {"publishedAt": ["Date"]}}
    """
    client._ensure_cookies()
    body: Dict[str, Any] = {"json": input_json}
    if meta:
        body["meta"] = meta
    return client.session.post(f"{client.trpc_url}/{procedure}",
                               json=body, timeout=timeout)


def _trpc_json(resp) -> Any:
    try:
        return resp.json().get("result", {}).get("data", {}).get("json")
    except Exception:
        return None


def _resolve_username(client, explicit: Optional[str]) -> str:
    """The username is MANDATORY: post.getInfinite without a username filter is
    the sitewide community feed - paging it looks like a hang and pollutes the
    cache with other people's posts. Better to fail loudly than fetch the world.

    Resolution order: explicit > REST /v1/users/me (needs the Profile scope on
    granular API keys) > NextAuth /api/auth/session (accepts Bearer regardless
    of scopes, and cookie sessions too - verified live 2026-07-19).
    """
    if explicit:
        return explicit

    username = client.test_api_key()
    if username and username != "unknown":
        return username

    try:
        base = client.api_base.rsplit("/api", 1)[0]
        resp = client._auth_get(f"{base}/api/auth/session", family="rest", timeout=15)
        if resp.status_code == 200:
            user = (resp.json() or {}).get("user") or {}
            name = user.get("username")
            if name:
                logger.info(f"Resolved username via session endpoint: {name}")
                return name
    except Exception as e:
        logger.debug(f"Session endpoint username resolution failed: {e}")

    raise RuntimeError(
        "Could not determine your CivitAI username. Set a valid CivitAI API key "
        "in Settings > CivitAI (recommended), or add your username under "
        "'posts: username:' in config.yaml.")


def _extract_post_images(client, post: Dict[str, Any], warnings: List[str]) -> List[Dict[str, Any]]:
    """Images from the post item itself, else a per-post image.getInfinite call."""
    raw = post.get("images")
    items: List[Dict[str, Any]] = []
    if isinstance(raw, list) and raw and isinstance(raw[0], dict):
        items = raw
    else:
        post_id = post.get("id")
        if post_id is None:
            return []
        try:
            resp = _trpc_query(client, "image.getInfinite",
                               {"postId": post_id, "pending": True, "limit": 100, "authed": True})
            data = _trpc_json(resp) or {}
            items = data.get("items", []) if isinstance(data, dict) else []
            time.sleep(PAGE_DELAY_SECONDS)
        except Exception as e:
            warnings.append(f"images fetch failed for post {post_id}: {e}")
            return []

    images = []
    for img in items:
        if not isinstance(img, dict) or img.get("id") is None:
            continue
        images.append({
            "id": img["id"],
            "name": img.get("name"),
            "url": img.get("url"),
            "width": img.get("width"),
            "height": img.get("height"),
            "nsfwLevel": img.get("nsfwLevel"),
        })
    return images


def _emit(progress, msg: str):
    """Send a human-readable progress line to the caller (C#), if one is listening."""
    if callable(progress):
        try:
            progress(msg)
        except Exception:
            pass
    logger.info(msg)


def _fetch_posts_pages(client, username: str, section: Optional[str],
                       stop_before: Optional[datetime.datetime],
                       warnings: List[str], progress=None,
                       seen_ids: Optional[set] = None) -> List[Dict[str, Any]]:
    """Cursor-page post.getInfinite; stops once a page is entirely older than stop_before.

    seen_ids is shared across section passes: if a whole page yields nothing new,
    this section is just replaying the same feed (server ignored the filter) and
    we bail out instead of re-paging the entire history per section.
    """
    posts: List[Dict[str, Any]] = []
    cursor = None
    pages = 0
    label = section or "all posts"
    while True:
        input_json: Dict[str, Any] = {"period": "AllTime", "sort": "Newest",
                                      "limit": 50, "authed": True,
                                      "username": username}
        if section:
            input_json["section"] = section
        if cursor is not None:
            input_json["cursor"] = cursor

        resp = _trpc_query(client, "post.getInfinite", input_json)
        if resp.status_code != 200:
            # Unknown sections and auth problems both land here; callers decide
            # severity. 401 on the baseline (no section) is a real auth failure.
            if section:
                logger.debug(f"post.getInfinite section={section} -> HTTP {resp.status_code}")
                return posts
            if resp.status_code == 401:
                raise PermissionError("Authentication failed (401) fetching posts. "
                                      "Set a CivitAI API key or refresh your cookies.")
            warnings.append(f"post.getInfinite HTTP {resp.status_code}")
            return posts

        data = _trpc_json(resp) or {}
        items = data.get("items", []) if isinstance(data, dict) else []
        next_cursor = data.get("nextCursor") if isinstance(data, dict) else None
        if not items:
            return posts

        page_dates = []
        new_on_page = 0
        for post in items:
            if not isinstance(post, dict) or post.get("id") is None:
                continue
            # Own-posts guard: if the item carries an owner and it isn't us,
            # the server ignored the username filter - skip, never cache.
            owner = (post.get("user") or {}).get("username")
            if owner and owner.lower() != username.lower():
                continue
            if seen_ids is not None:
                if post["id"] in seen_ids:
                    continue
                seen_ids.add(post["id"])
            new_on_page += 1
            posts.append(post)
            dt = _parse_iso(post.get("publishedAt"))
            if dt:
                page_dates.append(dt)

        pages += 1
        _emit(progress, f"Scanning {label}: page {pages} ({len(posts)} posts)…")

        if section and new_on_page == 0:
            # Newest-first: anything new (incl. scheduled) would be on page 1.
            logger.info(f"  section={section}: nothing new, skipping")
            return posts
        if stop_before and page_dates and max(page_dates) < stop_before:
            logger.info(f"  section={label}: stopped after {pages} pages "
                        f"(reached posts older than {stop_before.date()})")
            return posts
        if not next_cursor or next_cursor == cursor:
            return posts
        if pages >= MAX_PAGES_PER_SECTION:
            warnings.append(f"section {label}: hit the {MAX_PAGES_PER_SECTION}-page "
                            "safety cap; results may be incomplete")
            return posts
        cursor = next_cursor
        time.sleep(PAGE_DELAY_SECONDS)


def _fetch_public_ids(client, username: str,
                      stop_before: Optional[datetime.datetime],
                      warnings: List[str], progress=None) -> Optional[set]:
    """Ids of the user's posts visible WITHOUT authentication (= published).

    Uses a fresh session with no cookies and no Bearer token, so the server
    treats us as an anonymous visitor. browsingLevel=31 requests all NSFW
    tiers so published NSFW posts are not misread as queued. Returns None
    when the public feed cannot be fetched - callers must then flag nothing.
    """
    import requests

    host = client._api_host()
    session = requests.Session()
    session.headers.update({
        'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        'Accept': 'application/json',
        'Referer': f'https://{host}/',
        'Origin': f'https://{host}',
    })

    ids: set = set()
    cursor = None
    pages = 0
    try:
        while True:
            input_json: Dict[str, Any] = {"username": username, "period": "AllTime",
                                          "sort": "Newest", "limit": 50,
                                          "browsingLevel": 31}
            if cursor is not None:
                input_json["cursor"] = cursor
            params = {"input": json.dumps({"json": input_json}, separators=(",", ":"))}
            resp = session.get(f"{client.trpc_url}/post.getInfinite",
                               params=params, timeout=30)
            if resp.status_code != 200:
                warnings.append(f"public feed HTTP {resp.status_code}; "
                                "queued detection skipped this run")
                return None

            data = resp.json().get("result", {}).get("data", {}).get("json") or {}
            items = data.get("items", []) if isinstance(data, dict) else []
            next_cursor = data.get("nextCursor") if isinstance(data, dict) else None
            if not items:
                return ids

            page_dates = []
            for post in items:
                if isinstance(post, dict) and post.get("id") is not None:
                    ids.add(post["id"])
                    dt = _parse_iso(post.get("publishedAt"))
                    if dt:
                        page_dates.append(dt)

            pages += 1
            _emit(progress, f"Checking public visibility: page {pages}…")

            if stop_before and page_dates and max(page_dates) < stop_before:
                return ids
            if not next_cursor or next_cursor == cursor:
                return ids
            if pages >= MAX_PAGES_PER_SECTION:
                warnings.append("public feed: hit the page safety cap")
                return ids
            cursor = next_cursor
            time.sleep(PAGE_DELAY_SECONDS)
    except Exception as e:
        warnings.append(f"public feed fetch failed: {e}; "
                        "queued detection skipped this run")
        return None


def _fetch_future_posts(client, username: str, warnings: List[str],
                        progress=None) -> List[Dict[str, Any]]:
    """Posts scheduled for the FUTURE, via post.getInfinite {scheduled: true}.

    Found in civitai's source (post.service.ts): the `scheduled` input flag
    relaxes the publishedAt <= NOW() clamp to also return the requesting
    user's own future posts. Verified live 2026-07-19: works with both the
    Bearer key and cookie sessions (goes through _auth_get, so the usual
    Bearer-first / cookie-fallback applies).
    """
    _emit(progress, "Checking future-scheduled posts…")

    future: List[Dict[str, Any]] = []
    seen: set = set()
    cursor = None
    pages = 0
    while True:
        input_json: Dict[str, Any] = {"username": username, "period": "AllTime",
                                      "sort": "Newest", "limit": 50,
                                      "scheduled": True, "authed": True}
        if cursor is not None:
            input_json["cursor"] = cursor
        try:
            resp = _trpc_query(client, "post.getInfinite", input_json)
        except Exception as e:
            warnings.append(f"future-posts fetch failed: {e}")
            return future
        if resp.status_code != 200:
            warnings.append(f"future-posts fetch HTTP {resp.status_code}")
            return future

        try:
            data = resp.json().get("result", {}).get("data", {}).get("json") or {}
        except Exception:
            data = {}
        items = data.get("items", []) if isinstance(data, dict) else []
        next_cursor = data.get("nextCursor") if isinstance(data, dict) else None
        if not items:
            break

        new_on_page = 0
        for post in items:
            if not isinstance(post, dict) or post.get("id") is None:
                continue
            owner = (post.get("user") or {}).get("username")
            if owner and owner.lower() != username.lower():
                continue
            if post["id"] in seen:
                continue
            seen.add(post["id"])
            # The scheduled feed mixes the future queue (first, sort=Newest)
            # with published history further down - keep only future posts;
            # past pending detection is the anonymous-diff's job.
            dt = _parse_iso(post.get("publishedAt"))
            if dt and dt > datetime.datetime.now(datetime.timezone.utc):
                future.append(post)
                new_on_page += 1

        pages += 1
        _emit(progress, f"Checking future-scheduled posts: page {pages} "
                        f"({len(future)} found)…")
        # Future posts sort first; a page adding none means we are past them.
        if new_on_page == 0 or not next_cursor or next_cursor == cursor:
            break
        if pages >= MAX_PAGES_PER_SECTION:
            warnings.append("future-posts: hit the page safety cap")
            break
        cursor = next_cursor
        time.sleep(PAGE_DELAY_SECONDS)

    logger.info(f"Future-scheduled posts: {len(future)}")
    return future


def fetch_posts(client, config: dict, username: Optional[str],
                range_from: datetime.date, range_to: datetime.date,
                existing_cache: Optional[dict], progress=None) -> dict:
    """Build the cache dict. Incremental when existing_cache is provided."""
    warnings: List[str] = []
    _emit(progress, "Signing in to CivitAI…")
    username = _resolve_username(client, username)  # raises if unknown - never fetch the world

    # Incremental boundary: keep old past posts, refetch everything newer than
    # (newest cached PAST post - overlap). Future/scheduled posts are always
    # refetched wholesale because they can be rescheduled or deleted.
    # range_from also bounds the scan: a "quick" fetch of the current month must
    # not silently turn into a months-long rescan because the cache is stale.
    now_utc = datetime.datetime.now(datetime.timezone.utc)
    range_start = datetime.datetime.combine(range_from, datetime.time.min,
                                            tzinfo=datetime.timezone.utc)
    stop_before = range_start
    if existing_cache and isinstance(existing_cache.get("posts"), list):
        past_dates = [d for d in (_parse_iso(p.get("publishedAt"))
                                  for p in existing_cache["posts"] if isinstance(p, dict))
                      if d and d < now_utc]
        if past_dates:
            boundary = max(past_dates) - datetime.timedelta(days=INCREMENTAL_OVERLAP_DAYS)
            stop_before = max(boundary, range_start)

    # Everything older than the scan window is kept as-is from the cache.
    # Future-dated posts are also kept: posts scheduled through Diffusion
    # Toolkit are recorded locally at schedule time, and the scheduled feed
    # can lag or hide them - a fetch that misses one must not wipe it. When
    # a fetch does return the post, the fresh entry replaces the kept one.
    kept_posts: List[Dict[str, Any]] = []
    if existing_cache and isinstance(existing_cache.get("posts"), list):
        def _keep(p):
            dt = _parse_iso(p.get("publishedAt"))
            return dt is not None and (dt < stop_before or dt > now_utc)
        kept_posts = [p for p in existing_cache["posts"]
                      if isinstance(p, dict) and _keep(p)]
        logger.info(f"Incremental fetch: keeping {len(kept_posts)} cached posts "
                    f"(older than {stop_before.date()} or future-scheduled)")

    # Single authenticated pass: the baseline feed includes both published
    # posts and the owner's pending queue.
    _emit(progress, f"Fetching posts for {username}…")
    fetched: Dict[Any, Dict[str, Any]] = {}
    seen_ids: set = set()
    try:
        for post in _fetch_posts_pages(client, username, None, stop_before,
                                       warnings, progress, seen_ids):
            fetched.setdefault(post.get("id"), post)
    except PermissionError:
        raise

    # Anonymous pass: whatever the public cannot see is the pending queue.
    # It always covers the FULL requested range (it is cheap - published-only,
    # a page covers weeks), so queue flags can be re-evaluated even for posts
    # the incremental authed pass kept from cache. On failure, flag nothing.
    public_ids = _fetch_public_ids(client, username, range_start, warnings, progress)
    flag_floor = now_utc - datetime.timedelta(days=QUEUE_FLAG_WINDOW_DAYS)
    queued_ids: set = set()
    if public_ids is not None:
        for pid, post in fetched.items():
            dt = _parse_iso(post.get("publishedAt"))
            if dt and dt >= range_start and dt >= flag_floor and pid not in public_ids:
                queued_ids.add(pid)
    if queued_ids:
        logger.info(f"Pending queue: {len(queued_ids)} post(s) not publicly visible")

    # Future-scheduled posts come from the cookie-session pass; they are queued
    # by definition and bypass the range filter via queued_ids membership.
    for post in _fetch_future_posts(client, username, warnings, progress):
        pid = post.get("id")
        fetched[pid] = post
        queued_ids.add(pid)

    range_end = datetime.datetime.combine(range_to, datetime.time.max,
                                          tzinfo=datetime.timezone.utc)
    in_range = []
    for post in fetched.values():
        published = _parse_iso(post.get("publishedAt"))
        if published is None:
            # Unscheduled drafts have no date - not calendar material.
            continue
        # Fetched posts inside the requested range are always used (the authed
        # pass's last page may straddle past stop_before - those stragglers are
        # valid data, not noise). Queued posts bypass the range check entirely.
        if post.get("id") not in queued_ids and (published < range_start
                                                 or published > range_end):
            continue
        in_range.append((post, published))

    new_posts: List[Dict[str, Any]] = []
    total = len(in_range)
    for idx, (post, published) in enumerate(in_range, start=1):
        _emit(progress, f"Loading images for post {idx} of {total}…")
        new_posts.append({
            "postId": post.get("id"),
            "publishedAt": post.get("publishedAt"),
            # Queued = still in CivitAI's draft/scheduled section, even when the
            # scheduled time has already passed without the post going public.
            "scheduled": post.get("id") in queued_ids or published > now_utc,
            "title": post.get("title"),
            "images": _extract_post_images(client, post, warnings),
        })

    # A kept post is dropped only when this run actually produced a replacement
    # for it - dropping on mere fetch-membership would lose posts the range
    # filter discarded. Kept posts inside the anonymous pass's window get their
    # queue flag re-evaluated (a queued post may have published since the last
    # refresh, and vice versa); older kept posts keep whatever flag they had.
    new_ids = {p.get("postId") for p in new_posts}
    kept_posts = [p for p in kept_posts if p.get("postId") not in new_ids]
    for p in kept_posts:
        dt = _parse_iso(p.get("publishedAt"))
        if dt is None:
            continue
        if public_ids is not None and range_start <= dt < stop_before:
            p["scheduled"] = dt >= flag_floor and p.get("postId") not in public_ids
        elif p.get("scheduled") and dt < flag_floor:
            # Self-heal: flags outside the trustworthy window are noise from
            # older runs (the anonymous feed hides some old NSFW posts).
            p["scheduled"] = False

    all_posts = kept_posts + new_posts
    all_posts.sort(key=lambda p: p.get("publishedAt") or "", reverse=True)

    # A quick (scoped) fetch must not shrink the recorded history range.
    cache_from = range_from.isoformat()
    if existing_cache and isinstance(existing_cache.get("rangeFrom"), str):
        cache_from = min(existing_cache["rangeFrom"], cache_from)

    return {
        "version": CACHE_VERSION,
        "generatedAt": now_utc.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "username": username,
        "rangeFrom": cache_from,
        "rangeTo": range_to.isoformat(),
        "posts": all_posts,
        "warnings": warnings,
    }


def write_cache_atomic(cache: dict, path: Path):
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".json.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(cache, f, ensure_ascii=False, indent=1)
    os.replace(tmp, path)


def record_scheduled_post(client, cache_path: Path, post_id, published_at: str,
                          title: Optional[str], images: List[Dict[str, Any]]) -> bool:
    """Insert a just-scheduled post straight into the calendar cache.

    The calendar shows it immediately, with no dependency on the scheduled
    feed returning it (feeds have lagged/hidden fresh posts before). A later
    fetch that does see the post replaces this entry with server data.
    """
    cache = load_cache(cache_path)
    if cache is None or not cache.get("username"):
        try:
            username = _resolve_username(client, None)
        except Exception as e:
            logger.warning(f"record_scheduled_post: no cache and no username ({e}); skipping")
            return False
        today = datetime.date.today()
        cache = {"version": CACHE_VERSION, "username": username,
                 "rangeFrom": today.isoformat(),
                 "rangeTo": (today + datetime.timedelta(days=92)).isoformat(),
                 "posts": [], "warnings": []}

    posts = [p for p in cache.get("posts", [])
             if isinstance(p, dict) and p.get("postId") != post_id]
    posts.append({
        "postId": post_id,
        "publishedAt": published_at,
        "scheduled": True,
        "title": title,
        "images": images,
    })
    posts.sort(key=lambda p: p.get("publishedAt") or "", reverse=True)
    cache["posts"] = posts
    cache["generatedAt"] = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    write_cache_atomic(cache, cache_path)
    logger.info(f"Recorded scheduled post {post_id} in {cache_path}")
    return True


def load_cache(path: Path) -> Optional[dict]:
    try:
        with open(path, "r", encoding="utf-8") as f:
            cache = json.load(f)
        return cache if isinstance(cache, dict) else None
    except (OSError, json.JSONDecodeError):
        return None


# ---------------------------------------------------------------------------
# schedule-post pipeline
# ---------------------------------------------------------------------------

def _image_dimensions(path: Path) -> tuple:
    """Width/height for PNG/JPEG/WebP without external deps. (0,0) if unknown."""
    try:
        with open(path, "rb") as f:
            head = f.read(32)
            if head.startswith(b"\x89PNG\r\n\x1a\n"):
                w, h = struct.unpack(">II", head[16:24])
                return int(w), int(h)
            if head.startswith(b"RIFF") and head[8:12] == b"WEBP":
                if head[12:16] == b"VP8X":
                    w = int.from_bytes(head[24:27], "little") + 1
                    h = int.from_bytes(head[27:30], "little") + 1
                    return w, h
                return 0, 0
            if head.startswith(b"\xff\xd8"):
                f.seek(2)
                while True:
                    marker = f.read(2)
                    if len(marker) < 2 or marker[0] != 0xFF:
                        return 0, 0
                    length = struct.unpack(">H", f.read(2))[0]
                    if marker[1] in (0xC0, 0xC1, 0xC2, 0xC3):
                        data = f.read(5)
                        h, w = struct.unpack(">HH", data[1:5])
                        return int(w), int(h)
                    f.seek(length - 2, 1)
    except Exception:
        pass
    return 0, 0


def _upload_and_attach(client, post_id, path: Path, index: int) -> Dict[str, Any]:
    """Upload one image and attach it to the draft post at the given index.

    Returns a cache-shaped image dict (id/name/url/width/height/nsfwLevel)
    built from the post.addImage response, so the caller can record the post
    locally without another fetch."""
    width, height = _image_dimensions(path)
    mime = {"png": "image/png", "jpg": "image/jpeg", "jpeg": "image/jpeg",
            "webp": "image/webp", "gif": "image/gif"}.get(path.suffix.lower().lstrip("."),
                                                          "application/octet-stream")

    # Upload handshake + PUT
    resp = client.session.post(f"{client.api_base}/v1/image-upload",
                               json={"filename": path.name, "metadata": {}},
                               timeout=30)
    if resp.status_code != 200:
        raise RuntimeError(f"upload: image-upload handshake failed for {path.name} "
                           f"(HTTP {resp.status_code})")
    body = resp.json()
    upload_url = body.get("uploadURL") or body.get("uploadUrl") or body.get("url")
    upload_key = body.get("id") or body.get("key")
    if not upload_url or not upload_key:
        raise RuntimeError(f"upload: handshake response missing url/key (keys={sorted(body.keys())})")

    with open(path, "rb") as f:
        put = client.session.put(upload_url, data=f,
                                 headers={"Content-Type": mime}, timeout=300)
    if put.status_code not in (200, 201, 204):
        raise RuntimeError(f"upload: PUT failed for {path.name} (HTTP {put.status_code})")
    logger.info(f"Uploaded image bytes for {path.name}")

    # Attach to post
    resp = _trpc_mutate(client, "post.addImage", {
        "postId": post_id,
        "url": upload_key,
        "name": path.name,
        "width": width,
        "height": height,
        "index": index,
        "mimeType": mime,
    })
    if resp.status_code != 200:
        raise RuntimeError(f"upload: post.addImage failed for {path.name} "
                           f"(HTTP {resp.status_code}): {resp.text[:300]}")
    logger.info(f"Attached {path.name} to post at index {index}")

    created = _trpc_json(resp)
    created = created if isinstance(created, dict) else {}
    return {
        "id": created.get("id") or 0,
        "name": created.get("name") or path.name,
        "url": created.get("url") or upload_key,
        "width": created.get("width") or width,
        "height": created.get("height") or height,
        "nsfwLevel": created.get("nsfwLevel"),
    }


def schedule_post(client, file_paths, publish_at: str, title: Optional[str],
                  progress=None) -> dict:
    """Create a draft, upload the image(s), attach them, set publishedAt.

    file_paths may be a single path string or a list of paths; all files end up
    in one post, in the given order. Returns {"status":"ok","postId":...,
    "publishedAt":...,"images":N} or raises RuntimeError with a category prefix
    ('auth:', 'upload:', 'schedule:').
    """
    if isinstance(file_paths, str):
        file_paths = [file_paths]
    paths = [Path(p) for p in file_paths]
    if not paths:
        raise RuntimeError("upload: no files given")
    for path in paths:
        if not path.is_file():
            raise RuntimeError(f"upload: file not found: {path}")

    publish_dt = _parse_iso(publish_at)
    if publish_dt is None:
        raise RuntimeError(f"schedule: invalid --publish-at value: {publish_at}")
    publish_utc = publish_dt.strftime("%Y-%m-%dT%H:%M:%S.000Z")

    def report(message):
        if progress:
            progress(message)

    # 1. Draft post
    resp = _trpc_mutate(client, "post.create", {"authed": True})
    if resp.status_code == 401:
        raise RuntimeError("auth: post.create rejected (401) - cookies expired?")
    data = _trpc_json(resp)
    post_id = data.get("id") if isinstance(data, dict) else None
    if resp.status_code != 200 or post_id is None:
        raise RuntimeError(f"upload: post.create failed (HTTP {resp.status_code})")
    logger.info(f"Created draft post {post_id}")

    try:
        # 2. Upload + attach every image, in selection order
        attached_images: List[Dict[str, Any]] = []
        for index, path in enumerate(paths):
            report(f"Uploading image {index + 1} of {len(paths)}: {path.name}…")
            attached_images.append(_upload_and_attach(client, post_id, path, index))

        # 3. Title (optional) + schedule
        report("Scheduling the post…")
        update_input: Dict[str, Any] = {"id": post_id, "publishedAt": publish_utc}
        if title:
            update_input["title"] = title
        # publishedAt must be superjson-tagged as a Date or the server's Zod
        # schema rejects it with "expected date, received string".
        resp = _trpc_mutate(client, "post.update", update_input,
                            meta={"values": {"publishedAt": ["Date"]}})
        if resp.status_code != 200:
            raise RuntimeError(f"schedule: post.update failed (HTTP {resp.status_code}): "
                               f"{resp.text[:300]}")
        logger.info(f"Scheduled post {post_id} for {publish_utc}")

        return {"status": "ok", "postId": post_id, "publishedAt": publish_utc,
                "images": len(paths), "postImages": attached_images}

    except Exception:
        # Roll back the draft so failed attempts don't litter the account.
        try:
            _trpc_mutate(client, "post.delete", {"id": post_id})
            logger.info(f"Rolled back draft post {post_id}")
        except Exception as cleanup_error:
            logger.warning(f"Rollback of draft post {post_id} failed: {cleanup_error}")
        raise
