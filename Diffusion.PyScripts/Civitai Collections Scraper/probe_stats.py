"""
Standalone probe: what engagement data does CivitAI report, and can OAuth upload?

The calendar shows reaction / comment / collection / tip counts, and posting
now runs over the API instead of the embedded browser. Both rest on shapes we
had not measured. This answers them against the live service:

  1. Which counters does a post's image item actually carry, and under which
     names? (`likeCountAllTime` vs `likeCount`, and whether the tip counter is
     present at all.)
  2. Are tips (Buzz) reported under Media & Posts Read, or do they need a Buzz
     scope bit we are not requesting?
  3. Do post items carry post-level stats, which would save a per-post
     image.getInfinite round trip?
  4. Does the /v1/image-upload handshake accept an OAuth Bearer token, or does
     posting keep a hard API-key/cookie dependency?

Nothing is uploaded and nothing is posted: the upload probe stops at the
handshake, which only mints a pre-signed URL.

The reader in posts_fetcher.py is deliberately tolerant of all of this - it
takes whichever key names it finds and treats absent counters as unknown - so
this probe confirms behavior rather than gating it.

Usage:
    set CIVITAI_OAUTH_CLIENT_ID=<client id from Civitai>
    .venv\\Scripts\\python.exe probe_stats.py

    # probe the mirror instead of civitai.com
    .venv\\Scripts\\python.exe probe_stats.py --host red

Tokens are never printed. A paste-able summary is printed at the end.
"""

import argparse
import json
import os
import sys

import requests

from probe_oauth import (
    COM,
    RED,
    DEFAULT_REDIRECT,
    USERINFO_ENDPOINT,
    describe_scope,
    fresh_session,
    run_oauth_flow,
    trpc_get,
    trpc_json,
)

# The mask Diffusion Toolkit ships with (CivitaiOAuthService.RequestedScope):
# Profile Read | Media & Posts Read | Media & Posts Write | Collections Read.
# Probing with exactly this is the point - it tells us what the *shipped* app
# can see, not what a maximally-scoped token could.
SHIPPED_SCOPE = 1 | 32 | 64 | 131072

# Counter names the calendar reads, newest naming first. Mirrors
# posts_fetcher._STAT_FIELDS; kept as a literal so the probe reports on the
# real contract even if that module is refactored.
EXPECTED_STATS = {
    "likeCount": ("likeCountAllTime", "likeCount"),
    "dislikeCount": ("dislikeCountAllTime", "dislikeCount"),
    "heartCount": ("heartCountAllTime", "heartCount"),
    "laughCount": ("laughCountAllTime", "laughCount"),
    "cryCount": ("cryCountAllTime", "cryCount"),
    "commentCount": ("commentCountAllTime", "commentCount"),
    "collectedCount": ("collectedCountAllTime", "collectedCount"),
    "tippedAmountCount": ("tippedAmountCountAllTime", "tippedAmountCount"),
    "viewCount": ("viewCountAllTime", "viewCount"),
}

summary = {}


def say(label, ok, detail):
    print(f"  [{'PASS' if ok else 'FAIL'}] {label} - {detail}")
    summary[label] = "yes" if ok else "no"
    return ok


def resolve_identity(token):
    """Username + id from /userinfo - the posts feed needs a username filter."""
    try:
        resp = requests.get(USERINFO_ENDPOINT, timeout=20,
                            headers={"Authorization": f"Bearer {token}",
                                     "Accept": "application/json"})
        if resp.status_code != 200:
            say("userinfo", False, f"HTTP {resp.status_code}")
            return None
        body = resp.json() or {}
        username = body.get("username") or body.get("preferred_username")
        say("userinfo", bool(username), f"username={username!r}")
        return username
    except Exception as e:
        say("userinfo", False, str(e))
        return None


def first_own_post(session, base, username):
    """Newest published post for the user, with its raw item dict."""
    resp = trpc_get(session, base, "post.getInfinite", {
        "username": username, "period": "AllTime", "sort": "Newest",
        "limit": 5, "authed": True,
    })
    if resp.status_code != 200:
        say("post.getInfinite", False, f"HTTP {resp.status_code}")
        return None
    data = trpc_json(resp) or {}
    items = data.get("items") if isinstance(data, dict) else None
    if not items:
        say("post.getInfinite", False, "no posts returned for this account")
        return None
    say("post.getInfinite", True, f"{len(items)} post(s)")
    return items[0]


def probe_post_stats(post):
    """Probe 3: does the post item itself carry stats?"""
    print("\n3. Post-level stats")
    print(f"   post item keys: {sorted(post.keys())}")
    stats = post.get("stats")
    if isinstance(stats, dict):
        say("post.stats present", True, f"keys={sorted(stats.keys())}")
    else:
        say("post.stats present", False,
            "absent - image stats must be fetched per post via image.getInfinite")


def probe_image_stats(session, base, post):
    """Probes 1-2: which counters come back on an image, and are tips there?"""
    print("\n1. Image-level stats")

    images = post.get("images")
    source = "post item"
    if not (isinstance(images, list) and images and isinstance(images[0], dict)):
        resp = trpc_get(session, base, "image.getInfinite", {
            "postId": post.get("id"), "pending": True, "limit": 10, "authed": True,
        })
        if resp.status_code != 200:
            say("image.getInfinite", False, f"HTTP {resp.status_code}")
            return
        data = trpc_json(resp) or {}
        images = data.get("items") if isinstance(data, dict) else None
        source = "image.getInfinite"

    if not images:
        say("image stats", False, "no images on the newest post")
        return

    image = images[0]
    print(f"   source: {source}")
    print(f"   image item keys: {sorted(image.keys())}")

    stats = image.get("stats")
    if not isinstance(stats, dict):
        say("image.stats present", False,
            "absent - the calendar will show 'no data' for every image")
        return

    say("image.stats present", True, f"{len(stats)} keys")
    print(f"   raw stats: {json.dumps(stats, indent=2, sort_keys=True)[:1500]}")

    print("\n2. Counter names the calendar looks for")
    for name, candidates in EXPECTED_STATS.items():
        matched = next((k for k in candidates if k in stats), None)
        if matched:
            say(f"  {name}", True, f"found as {matched!r} = {stats[matched]}")
        else:
            say(f"  {name}", False, f"none of {candidates} present")

    extra = sorted(set(stats) - {k for c in EXPECTED_STATS.values() for k in c})
    if extra:
        print(f"   unread keys also present: {extra}")

    tip_key = next((k for k in EXPECTED_STATS["tippedAmountCount"] if k in stats), None)
    if tip_key is None:
        print("   NOTE: no tip counter under the shipped scope. If tips show on the")
        print("         website for this image, Buzz data needs a scope bit we do not")
        print("         request - add it to CivitaiOAuthService.RequestedScope.")


def probe_upload_handshake(token, base):
    """Probe 4: does /v1/image-upload accept a Bearer OAuth token?

    Only the handshake runs. It mints a pre-signed upload URL; without the
    follow-up PUT nothing is stored and no post is touched.
    """
    print("\n4. /v1/image-upload handshake auth")
    payload = {"filename": "diffusion-toolkit-probe.png", "metadata": {}}

    anon = fresh_session(base)
    try:
        resp = anon.post(f"{base}/api/v1/image-upload", json=payload, timeout=20)
        baseline = resp.status_code
    except Exception as e:
        baseline = f"error ({e})"
    print(f"   unauthenticated baseline: HTTP {baseline}")

    bearer = fresh_session(base, bearer=token)
    try:
        resp = bearer.post(f"{base}/api/v1/image-upload", json=payload, timeout=20)
    except Exception as e:
        say("image-upload accepts OAuth", False, str(e))
        return

    if resp.status_code == 200:
        body = resp.json() if resp.content else {}
        say("image-upload accepts OAuth", True,
            f"HTTP 200, keys={sorted(body.keys())} (no bytes uploaded)")
        return

    say("image-upload accepts OAuth", False, f"HTTP {resp.status_code}")
    print("   => posting keeps an API-key or cookie dependency for the upload step;")
    print("      the rest of the pipeline (post.create/addImage/update) can still")
    print("      run on OAuth. Say so in the UI rather than failing opaquely.")


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--client-id", default=os.environ.get("CIVITAI_OAUTH_CLIENT_ID"),
                        help="Public OAuth client id (or set CIVITAI_OAUTH_CLIENT_ID)")
    parser.add_argument("--redirect", default=DEFAULT_REDIRECT,
                        help=f"Registered redirect URI (default {DEFAULT_REDIRECT})")
    parser.add_argument("--host", choices=("com", "red"), default="com",
                        help="Which CivitAI host to query (default com)")
    parser.add_argument("--scope", type=int, default=SHIPPED_SCOPE,
                        help=f"Scope bitmask to request (default {SHIPPED_SCOPE}, "
                             "the mask the shipped app uses)")
    args = parser.parse_args()

    if not args.client_id:
        print("No client id. Set CIVITAI_OAUTH_CLIENT_ID or pass --client-id.")
        return 2

    base = COM if args.host == "com" else RED
    print(f"Probing {base} with scope {args.scope}: {describe_scope(args.scope)}\n")

    token, _refresh = run_oauth_flow(args.client_id, args.redirect, args.scope)
    if not token:
        print("\nAuthorization did not complete; nothing further to probe.")
        return 1

    print("\n0. Identity")
    username = resolve_identity(token)
    if not username:
        print("\nWithout a username the posts feed is the sitewide feed, which "
              "would measure other people's data. Stopping.")
        return 1

    session = fresh_session(base, bearer=token)
    post = first_own_post(session, base, username)
    if post is None:
        print("\nNo posts to read stats from. Publish something, then re-run.")
        return 1

    probe_image_stats(session, base, post)
    probe_post_stats(post)
    probe_upload_handshake(token, base)

    print("\n" + "=" * 70)
    print("SUMMARY (paste into CIVITAI_OAUTH_IMPLEMENTATION_PLAN.md)")
    print("=" * 70)
    for label, value in summary.items():
        print(f"  {label}: {value}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
