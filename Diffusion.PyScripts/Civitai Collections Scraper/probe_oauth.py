"""
Standalone probe: can Diffusion Toolkit replace the API key with Civitai OAuth?

Runs the real OAuth 2.0 Authorization Code + PKCE (S256) flow that a desktop WPF
app would run - public client, no secret, loopback redirect - and then checks
whether the resulting access token actually works on the endpoints this repo
depends on. Answers the unknowns behind the OAuth migration:

  0. Does Civitai accept an http://127.0.0.1 loopback redirect?   (pre-flight)
  1. Does the full authorize -> code -> token exchange complete?
  2. Which scope bitmask does Civitai actually grant? (only 8 bit values are
     published; this reads the real one back off the token response)
  3. /userinfo identity                    (replaces the calendar's username guess)
  4. REST /v1/users/me on .com and .red    (does a .com token work on the mirror?)
  5. tRPC collection.getAllUser            (THE collections picker for the scraper)
  6. tRPC collection.getById               (collection metadata)
  7. tRPC image.getInfinite, browsingLevel 31, by collectionId  (scraper core loop)
  8. tRPC image.getGenerationData          (the C# metadata-fetch path)
  9. Does refresh_token work without re-consent?
 10. tRPC post.create/delete with Bearer   (calendar scheduling; --posting only)

Register the app first: civitai.com -> Account Settings -> OAuth Applications ->
Register App, then in the dialog:
  - App type:      Browser / Mobile App (public)  <- NOT Server App. Civitai
                   lists desktop apps under this type; it uses PKCE and issues
                   no client secret, which is what a shipped .exe requires.
  - App name:      Diffusion Toolkit
  - Redirect URIs: http://127.0.0.1:8765/callback   (exact match, one per line)
  - Permissions:   set the preset to Custom and tick only
                     Profile & Settings  Read
                     Media & Posts       Read (+ Write for calendar scheduling)
                     Collections         Read (+ Write to add images to collections)
                     Models              Read
                   Leave every Delete column unticked.

Usage:
    set CIVITAI_OAUTH_CLIENT_ID=<client id from Civitai>
    .venv\\Scripts\\python.exe probe_oauth.py

    # ask for an explicit read-only bitmask instead of the registered permissions
    .venv\\Scripts\\python.exe probe_oauth.py --scope dt

    # request every bit, so the consent screen enumerates what Civitai can grant
    # (reading that screen is the point - you may cancel it)
    .venv\\Scripts\\python.exe probe_oauth.py --scope discover

    # also probe write access for the calendar (creates and deletes a draft post)
    .venv\\Scripts\\python.exe probe_oauth.py --posting

    # retry registration with a different exact redirect URI
    .venv\\Scripts\\python.exe probe_oauth.py --redirect http://localhost:8765/callback

A public client ID is an identifier, not a credential, so --client-id is also
accepted. Tokens are never printed. A paste-able summary is printed at the end.
"""

import argparse
import base64
import hashlib
import json
import os
import secrets
import sys
import time
import urllib.parse
import webbrowser
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

import requests

AUTH_BASE = "https://auth.civitai.com"
AUTHORIZE_ENDPOINT = f"{AUTH_BASE}/api/auth/oauth/authorize"
TOKEN_ENDPOINT = f"{AUTH_BASE}/api/auth/oauth/token"
USERINFO_ENDPOINT = f"{AUTH_BASE}/api/auth/oauth/userinfo"

COM = "https://civitai.com"
RED = "https://civitai.red"
SCRIPT_DIR = Path(__file__).parent

DEFAULT_REDIRECT = "http://127.0.0.1:8765/callback"
CALLBACK_TIMEOUT_S = 300

# Scope is an integer bitmask, not a string. These eight values are confirmed
# (Civitai-MultiHub-Android ships against them). Civitai's registration screen
# offers a wider Read/Write/Delete grid - Profile & Settings, Models, Media &
# Posts, Articles, Bounties, AI Services, Buzz, Collections, Social,
# Notifications, Vault - whose remaining bit values are NOT published. Bits we
# cannot name are reported as "bit N (undocumented)"; use --scope registered to
# avoid guessing them.
SCOPE_NAMES = {
    1: "Profile & Settings Read",
    4: "Models Read",
    32: "Media & Posts Read",
    64: "Media & Posts Write",  # verified 2026-08-04: granted, and post.create succeeded
    131072: "Collections Read",
    262144: "Collections Write",
    524288: "Social Write",
    2097152: "Notifications Read",
    4194304: "Notifications Write",
}

# Media & Posts Delete is presumably 128 (the grid's third column), but that is
# unverified - post.delete returns 403 without it, which strands the draft post
# that --posting creates. See the warning printed by _probe_posting.
MEDIA_WRITE_SCOPE = 64

# What Diffusion Toolkit needs to read: identity + images + collections.
DT_READ_SCOPE = 1 | 32 | 131072  # 131105
# Every bit up to 2^25, to make the consent screen enumerate what exists.
DISCOVER_SCOPE = (1 << 25) - 1

# Known-public image on civitai.com (any SFW front-page id works).
PUBLIC_IMAGE_ID = 728718

results = {}  # summary key -> "yes" / "no" / "skip" / "error"


def describe_scope(mask):
    """Render a bitmask as documented names plus any undocumented bits."""
    if not mask:
        return "(none)"
    parts = [name for bit, name in sorted(SCOPE_NAMES.items()) if mask & bit]
    unknown = [bit for bit in (1 << n for n in range(32))
               if mask & bit and bit not in SCOPE_NAMES]
    parts += [f"bit {bit} (undocumented)" for bit in unknown]
    return ", ".join(parts)


def fresh_session(host_base, bearer=None):
    """Cookie-free session with the same spoofed headers the scraper uses.

    civitai.red enforces an origin check on authenticated tRPC endpoints, so
    Referer/Origin must match the host even when a token is present.
    """
    s = requests.Session()
    host = host_base.split("//", 1)[1]
    s.headers.update({
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
        "Accept": "application/json",
        "Accept-Language": "en-US,en;q=0.9",
        "Referer": f"https://{host}/",
        "Origin": f"https://{host}",
    })
    if bearer:
        s.headers["Authorization"] = f"Bearer {bearer}"
    return s


def trpc_get(session, base, procedure, input_json, timeout=20):
    params = {"input": json.dumps({"json": input_json}, separators=(",", ":"))}
    return session.get(f"{base}/api/trpc/{procedure}", params=params, timeout=timeout)


def trpc_post(session, base, procedure, input_json, timeout=20):
    return session.post(f"{base}/api/trpc/{procedure}", json={"json": input_json}, timeout=timeout)


try:
    # Reuse the shipped parser rather than reimplementing it. CivitAI is
    # migrating tRPC responses from {"json": ...} to a reference-table format
    # delivered as a JSON *string*; image.getInfinite already returns the new
    # shape while collection.getAllUser still returns the old one. Using
    # api_client's own extractor also makes this probe a test of the production
    # parsing path, not just of the transport.
    from api_client import _extract_trpc_json as _shipped_extract
except Exception:  # pragma: no cover - probe must run even if imports break
    _shipped_extract = None


def trpc_json(resp):
    try:
        body = resp.json()
    except Exception:
        return None
    if _shipped_extract is not None:
        try:
            return _shipped_extract(body)
        except Exception:
            pass
    payload = body.get("result", {}).get("data") if isinstance(body, dict) else None
    if isinstance(payload, dict):
        return payload.get("json", payload)
    return payload


def report(label, key, ok, status, evidence):
    print(f"  [{'PASS' if ok else 'FAIL'}] {label} (HTTP {status}) - {evidence}")
    results[key] = "yes" if ok else "no"
    return ok


def get_config_collection_id():
    """First collection id from config.yaml, as a fallback probe target."""
    try:
        import yaml
        with open(SCRIPT_DIR / "config.yaml", "r", encoding="utf-8") as f:
            cfg = yaml.safe_load(f)
        return cfg["collections"][0]["id"]
    except Exception:
        return 1107870


# --- OAuth flow ------------------------------------------------------------

def base64url(raw):
    return base64.urlsafe_b64encode(raw).decode("ascii").rstrip("=")


def build_authorize_url(client_id, redirect_uri, scope, state, challenge):
    """scope=None omits the parameter entirely.

    Verified 2026-08-04: Civitai REJECTS that with
    {"error":"invalid_scope","error_description":"Invalid scope value"}, so an
    explicit bitmask is mandatory - the server will not fall back to the
    permissions the application was registered with.
    """
    params = {
        "client_id": client_id,
        "redirect_uri": redirect_uri,
        "response_type": "code",
        "state": state,
        "code_challenge": challenge,
        "code_challenge_method": "S256",
    }
    if scope is not None:
        params["scope"] = str(scope)
    return f"{AUTHORIZE_ENDPOINT}?{urllib.parse.urlencode(params)}"


def preflight_redirect(authorize_url):
    """Probe 0: does the AS reject the loopback redirect_uri before login?

    An unauthenticated GET normally 302s to Civitai's login page. A *rejected*
    redirect_uri fails earlier, with an error in the status, body, or Location.
    This is evidence, not a verdict - the interactive flow is authoritative.
    """
    print("0. Pre-flight: authorize endpoint with a loopback redirect_uri")
    try:
        r = requests.get(authorize_url, allow_redirects=False, timeout=20)
    except Exception as e:
        print(f"  [ERROR] {e}")
        results["OAUTH_PREFLIGHT"] = "error"
        return

    location = r.headers.get("Location", "")
    body = (r.text or "")[:4000].lower()
    rejected = ("redirect_uri" in body and ("invalid" in body or "mismatch" in body)) \
        or "invalid_request" in location or "invalid_client" in location \
        or "unauthorized_client" in location
    looks_like_login = bool(location) and not rejected
    report("redirect_uri not rejected pre-login", "OAUTH_PREFLIGHT",
           not rejected, r.status_code,
           f"redirects_to_login={looks_like_login}, location={location[:80]!r}")


class _CallbackHandler(BaseHTTPRequestHandler):
    """Single-shot loopback listener for the authorization response."""

    captured = None
    callback_path = "/callback"

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        if parsed.path != self.callback_path:
            self.send_response(404)
            self.end_headers()
            return
        _CallbackHandler.captured = urllib.parse.parse_qs(parsed.query)
        ok = "code" in _CallbackHandler.captured
        message = ("Diffusion Toolkit received the authorization code. "
                   "You can close this tab.") if ok else \
                  ("Civitai returned an error. Check the probe output in your terminal.")
        payload = (f"<!doctype html><meta charset=utf-8>"
                   f"<title>Civitai OAuth probe</title>"
                   f"<body style='font:16px system-ui;padding:3rem'>{message}</body>"
                   ).encode("utf-8")
        self.send_response(200 if ok else 400)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *args):
        pass  # keep the probe output clean


def await_callback(redirect_uri):
    """Serve the loopback redirect until the browser delivers the callback.

    Browsers fire unrelated requests at this port (favicon prefetch, most
    commonly). handle_request() returns for those exactly as it does on a
    timeout, so a wall-clock deadline - not the return itself - decides when to
    give up. Short server timeouts just keep the deadline responsive.
    """
    parsed = urllib.parse.urlparse(redirect_uri)
    _CallbackHandler.captured = None
    _CallbackHandler.callback_path = parsed.path or "/callback"
    server = HTTPServer((parsed.hostname, parsed.port or 80), _CallbackHandler)
    server.timeout = 1
    deadline = time.monotonic() + CALLBACK_TIMEOUT_S
    try:
        while _CallbackHandler.captured is None and time.monotonic() < deadline:
            server.handle_request()
    finally:
        server.server_close()
    if _CallbackHandler.captured is None:
        print(f"  [FAIL] no callback within {CALLBACK_TIMEOUT_S}s")
        return None
    return _CallbackHandler.captured


def exchange_code(client_id, redirect_uri, code, verifier):
    return requests.post(TOKEN_ENDPOINT, data={
        "grant_type": "authorization_code",
        "code": code,
        "redirect_uri": redirect_uri,
        "client_id": client_id,
        "code_verifier": verifier,
    }, headers={"Accept": "application/json"}, timeout=30)


def parse_granted_scope(value):
    """Civitai has returned scope as an int, a string, or a single-item array."""
    if isinstance(value, list):
        value = value[0] if value else 0
    try:
        return int(value)
    except (TypeError, ValueError):
        return 0


def run_oauth_flow(client_id, redirect_uri, scope):
    """Probes 1-2. Returns (access_token, refresh_token) or (None, None)."""
    verifier = base64url(secrets.token_bytes(32))
    challenge = base64url(hashlib.sha256(verifier.encode("ascii")).digest())
    state = base64url(secrets.token_bytes(32))
    authorize_url = build_authorize_url(client_id, redirect_uri, scope, state, challenge)

    preflight_redirect(authorize_url)

    if urllib.parse.urlparse(redirect_uri).hostname not in ("127.0.0.1", "localhost"):
        print("\n  [SKIP] --redirect is not an http loopback address; the probe can "
              "only serve loopback callbacks. Pre-flight result above still applies.")
        results["OAUTH_TOKEN_EXCHANGE"] = "skip"
        return None, None

    print(f"\n1. Interactive authorization (listening on {redirect_uri})")
    if scope is None:
        print("   No scope parameter sent: Civitai should apply the permissions "
              "the app was registered with.")
    else:
        print(f"   Requesting scope {scope}: {describe_scope(scope)}")
    print("   Opening your browser. Approve the consent screen to continue.")
    print("   Read the permission list on that screen - it is the authoritative")
    print("   list of what Civitai can grant.\n")
    webbrowser.open(authorize_url)

    captured = await_callback(redirect_uri)
    if captured is None:
        results["OAUTH_TOKEN_EXCHANGE"] = "no"
        return None, None

    returned_state = (captured.get("state") or [""])[0]
    if not secrets.compare_digest(returned_state, state):
        report("state validation", "OAUTH_STATE", False, "-", "state mismatch")
        results["OAUTH_TOKEN_EXCHANGE"] = "no"
        return None, None
    results["OAUTH_STATE"] = "yes"

    error = (captured.get("error") or [""])[0]
    if error:
        detail = (captured.get("error_description") or [""])[0]
        report("authorization", "OAUTH_TOKEN_EXCHANGE", False, "-",
               f"error={error!r} {detail[:120]!r}")
        return None, None

    code = (captured.get("code") or [""])[0]
    print("  callback received; exchanging the code (no client secret sent)")
    try:
        r = exchange_code(client_id, redirect_uri, code, verifier)
        body = r.json() if r.text else {}
    except Exception as e:
        print(f"  [ERROR] {e}")
        results["OAUTH_TOKEN_EXCHANGE"] = "error"
        return None, None

    access = body.get("access_token", "")
    refresh = body.get("refresh_token", "")
    ok = report("code -> token exchange", "OAUTH_TOKEN_EXCHANGE",
                r.status_code == 200 and bool(access), r.status_code,
                f"token_type={body.get('token_type')!r}, expires_in={body.get('expires_in')}, "
                f"has_refresh_token={bool(refresh)}")
    if not ok:
        detail = body.get("error_description") or body.get("error") or (r.text or "")[:200]
        print(f"         detail: {str(detail)[:200]!r}")
        return None, None

    granted = parse_granted_scope(body.get("scope"))
    print("\n2. Granted scope")
    if scope is None:
        # Nothing was requested, so any non-zero mask is the app's registered
        # permission set - which is exactly what we wanted to learn.
        report("registered permissions returned", "OAUTH_SCOPE_GRANTED",
               granted != 0, granted, f"granted={granted} -> {describe_scope(granted)}")
    else:
        report("granted covers requested", "OAUTH_SCOPE_GRANTED",
               granted != 0 and (granted | scope) == granted, granted,
               f"granted={granted} -> {describe_scope(granted)}")
        missing = scope & ~granted
        if missing:
            print(f"         NOT granted: {describe_scope(missing)}")
    results["OAUTH_SCOPE_VALUE"] = str(granted)
    return access, refresh


def probe_refresh(client_id, refresh_token):
    print("9. refresh_token grant (no re-consent)")
    if not refresh_token:
        print("  [SKIP] no refresh token issued")
        results["OAUTH_REFRESH"] = "skip"
        return
    try:
        r = requests.post(TOKEN_ENDPOINT, data={
            "grant_type": "refresh_token",
            "refresh_token": refresh_token,
            "client_id": client_id,
        }, headers={"Accept": "application/json"}, timeout=30)
        body = r.json() if r.text else {}
        report("refresh exchange", "OAUTH_REFRESH",
               r.status_code == 200 and bool(body.get("access_token")), r.status_code,
               f"rotated_refresh_token={bool(body.get('refresh_token'))}, "
               f"expires_in={body.get('expires_in')}")
    except Exception as e:
        print(f"  [ERROR] {e}")
        results["OAUTH_REFRESH"] = "error"


# --- Endpoint probes -------------------------------------------------------

def probe_endpoints(token, run_posting):
    """Probes 3-8 (+10): does the OAuth token work where the API key does?"""
    username = None

    print("3. OIDC /userinfo (identity for the posts calendar)")
    try:
        r = requests.get(USERINFO_ENDPOINT, timeout=20, headers={
            "Accept": "application/json", "Authorization": f"Bearer {token}",
        })
        body = r.json() if r.status_code == 200 else {}
        username = body.get("username") or body.get("preferred_username")
        report("userinfo", "OAUTH_USERINFO", r.status_code == 200 and bool(username),
               r.status_code, f"username={username!r}, id={body.get('id') or body.get('sub')!r}")
    except Exception as e:
        print(f"  [ERROR] {e}")
        results["OAUTH_USERINFO"] = "error"

    print("4. REST /v1/users/me (does a .com token work on the .red mirror?)")
    for base, key in ((COM, "OAUTH_REST_COM"), (RED, "OAUTH_REST_RED")):
        try:
            r = fresh_session(base, bearer=token).get(f"{base}/api/v1/users/me", timeout=20)
            body = r.json() if r.status_code == 200 else {}
            user = body.get("username") or body.get("user", {}).get("username")
            if user and not username:
                username = user
            report(f"users/me on {base}", key, r.status_code == 200, r.status_code,
                   f"username={user!r}")
        except Exception as e:
            print(f"  [ERROR] {e}")
            results[key] = "error"

    print("5. tRPC collection.getAllUser (the collections picker)")
    image_id = None
    for base, key in ((COM, "OAUTH_TRPC_COLLECTIONS_COM"), (RED, "OAUTH_TRPC_COLLECTIONS_RED")):
        try:
            r = trpc_get(fresh_session(base, bearer=token), base, "collection.getAllUser",
                         {"limit": 50, "sort": "Newest"})
            data = trpc_json(r)
            rows = data if isinstance(data, list) else (data or {}).get("items", []) \
                if isinstance(data, dict) else []
            rows = [c for c in rows if isinstance(c, dict)]
            report(f"collection.getAllUser on {base}", key,
                   r.status_code == 200 and len(rows) > 0, r.status_code,
                   f"collections={len(rows)}")
            if base != COM or not rows:
                continue
            # This listing is the raw material for the C# picker, so show what
            # it actually returns - the type field decides which entries are
            # even scrapable.
            print("       id       type        owner  name")
            for c in rows[:15]:
                print(f"       {str(c.get('id')):<8} {str(c.get('type')):<11} "
                      f"{str(bool(c.get('isOwner'))):<6} {str(c.get('name'))[:40]}")
            # Only Image collections hold images; picking an Article collection
            # here is what made the first run report a false negative.
            images = [c for c in rows if str(c.get("type", "")).lower() == "image"]
            pick = next((c for c in images if c.get("isOwner")), None) or \
                (images[0] if images else None)
            if pick:
                image_id = pick.get("id")
                print(f"       -> image collection for probes 6-7: "
                      f"{image_id} {pick.get('name')!r}")
        except Exception as e:
            print(f"  [ERROR] {e}")
            results[key] = "error"

    collection_id = image_id or get_config_collection_id()

    print("6. tRPC collection.getById (collection metadata)")
    try:
        r = trpc_get(fresh_session(COM, bearer=token), COM, "collection.getById",
                     {"id": int(collection_id)})
        data = trpc_json(r) or {}
        collection = data.get("collection") or data
        report("collection.getById", "OAUTH_COLLECTION_BYID",
               r.status_code == 200 and bool(collection.get("id")), r.status_code,
               f"name={collection.get('name')!r}")
    except Exception as e:
        print(f"  [ERROR] {e}")
        results["OAUTH_COLLECTION_BYID"] = "error"

    print("7. tRPC image.getInfinite by collectionId, browsingLevel 31 (scraper core)")
    first_image_id = None
    # The config.yaml target is the workload that actually matters: an NSFW
    # collection paged off civitai.red. Probe it alongside the picked one.
    config_id = get_config_collection_id()
    targets = [("picked", collection_id, RED, "OAUTH_IMAGES_RED"),
               ("picked", collection_id, COM, "OAUTH_IMAGES_COM")]
    if config_id and int(config_id) != int(collection_id):
        targets += [("config.yaml", config_id, RED, "OAUTH_IMAGES_CONFIG_RED"),
                    ("config.yaml", config_id, COM, "OAUTH_IMAGES_CONFIG_COM")]
    for label, target_id, base, key in targets:
        try:
            r = trpc_get(fresh_session(base, bearer=token), base, "image.getInfinite", {
                "collectionId": int(target_id), "period": "AllTime", "sort": "Newest",
                "browsingLevel": 31, "include": ["cosmetics"], "disablePoi": True,
                "disableMinor": False, "authed": True,
            }, timeout=30)
            data = trpc_json(r) or {}
            items = [i for i in (data.get("items", []) if isinstance(data, dict) else [])
                     if isinstance(i, dict)]
            if items and first_image_id is None:
                first_image_id = items[0].get("id")
            max_lvl = max((i.get("nsfwLevel", 0) for i in items), default=0)
            report(f"{label} collection {target_id} on {base}", key,
                   r.status_code == 200 and len(items) > 0, r.status_code,
                   f"items={len(items)}, max nsfwLevel={max_lvl}")
        except Exception as e:
            print(f"  [ERROR] {e}")
            results[key] = "error"

    print("8. tRPC image.getGenerationData (the C# metadata path)")
    for label, img_id in (("public", PUBLIC_IMAGE_ID),
                          ("collection", first_image_id)):
        if img_id is None:
            continue
        key = f"OAUTH_GENDATA_{label.upper()}"
        try:
            r = trpc_get(fresh_session(COM, bearer=token), COM,
                         "image.getGenerationData", {"id": img_id})
            data = trpc_json(r)
            has_meta = bool(data and (data.get("meta") or data.get("resources")))
            report(f"{label} image {img_id}", key, r.status_code == 200, r.status_code,
                   f"has_meta_or_resources={has_meta}")
        except Exception as e:
            print(f"  [ERROR] {e}")
            results[key] = "error"

    print("10. tRPC post.create with Bearer (calendar scheduling)")
    if not run_posting:
        print("  [SKIP] run with --posting to include write probes")
        results["OAUTH_POST_CREATE"] = "skip"
        return
    print("  WARNING: post.delete needs Media & Posts DELETE permission, which the")
    print("  recommended registration does not grant. Without it the draft post this")
    print("  creates cannot be removed by the probe and must be deleted by hand.")
    try:
        session = fresh_session(RED, bearer=token)
        r = trpc_post(session, RED, "post.create", {"authed": True})
        data = trpc_json(r)
        post_id = data.get("id") if isinstance(data, dict) else None
        report("post.create", "OAUTH_POST_CREATE",
               r.status_code == 200 and post_id is not None, r.status_code,
               f"draft post id={post_id}")
        if post_id is not None:
            rd = trpc_post(session, RED, "post.delete", {"id": post_id})
            deleted = rd.status_code == 200
            outcome = "deleted" if deleted else \
                f"DELETE FAILED - remove the draft manually at {RED}/posts/{post_id}"
            print(f"  cleanup: post.delete -> HTTP {rd.status_code} ({outcome})")
            results["OAUTH_POST_CLEANUP"] = "yes" if deleted else "no"
    except Exception as e:
        print(f"  [ERROR] {e}")
        results["OAUTH_POST_CREATE"] = "error"


def load_cookies_into(session, host_base):
    """Minimal Netscape cookies.txt loader (mirrors api_client.py behavior).

    Returns the number of cookies loaded. Mirrors .civitai.com cookies onto the
    target host so they are actually sent (requests matches cookie domains).
    """
    from http.cookiejar import Cookie

    host = host_base.split("//", 1)[1]
    candidates = [
        SCRIPT_DIR / f"{host}_cookies.txt",
        SCRIPT_DIR / "civitai.red_cookies.txt",
        SCRIPT_DIR / "civitai.com_cookies.txt",
    ]
    cookie_file = next((c for c in candidates if c.exists()), None)
    if cookie_file is None:
        return 0

    count = 0
    with open(cookie_file, "r", encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if not line.strip():
                continue
            if line.startswith("#"):
                if line.startswith("#HttpOnly_"):
                    line = line[len("#HttpOnly_"):]
                else:
                    continue
            parts = line.split("\t")
            if len(parts) != 7:
                continue
            domain, _flag, path, secure, expiration, name, value = parts
            domains = [domain]
            bare = domain.lstrip(".")
            if bare != host and not host.endswith("." + bare):
                domains.append("." + host)
            for d in domains:
                try:
                    expires = int(expiration) if expiration not in ("0", "") else None
                except ValueError:
                    expires = None
                session.cookies.set_cookie(Cookie(
                    version=0, name=name, value=value, port=None,
                    port_specified=False, domain=d, domain_specified=True,
                    domain_initial_dot=d.startswith("."), path=path or "/",
                    path_specified=True, secure=secure == "TRUE", expires=expires,
                    discard=False, comment=None, comment_url=None, rest={},
                    rfc2109=False,
                ))
            count += 1
    return count


def diagnose_images(token, collection_id):
    """Why does image.getInfinite return HTTP 200 with zero items on OAuth?

    Separates the candidate causes: NSFW browsing-level gating, the `authed`
    flag, the collectionId filter itself, or the endpoint simply not working
    with a Bearer token. The cookie run at the end is the control - it uses the
    path the scraper works with today.
    """
    cid = int(collection_id)
    base_input = {
        "collectionId": cid, "period": "AllTime", "sort": "Newest",
        "browsingLevel": 31, "include": ["cosmetics"], "disablePoi": True,
        "disableMinor": False, "authed": True,
    }

    def variant(name, changes, drop=()):
        payload = dict(base_input)
        payload.update(changes)
        for field in drop:
            payload.pop(field, None)
        return name, payload

    variants = [
        variant("scraper default (browsingLevel=31, authed)", {}),
        variant("browsingLevel=1 (PG only)", {"browsingLevel": 1}),
        variant("no browsingLevel", {}, drop=("browsingLevel",)),
        variant("no authed flag", {}, drop=("authed",)),
        variant("minimal input (collectionId only)", {},
                drop=("browsingLevel", "authed", "include", "disablePoi", "disableMinor")),
        variant("no collectionId, browsingLevel=1 (endpoint control)",
                {"browsingLevel": 1}, drop=("collectionId",)),
        variant("no collectionId, browsingLevel=31",
                {"browsingLevel": 31}, drop=("collectionId",)),
    ]

    print(f"\nIMAGE DIAGNOSIS on collection {cid} (Bearer token)")
    for base in (RED, COM):
        print(f"\n  --- {base} ---")
        for name, payload in variants:
            try:
                r = trpc_get(fresh_session(base, bearer=token), base,
                             "image.getInfinite", payload, timeout=30)
                data = trpc_json(r)
                if isinstance(data, dict):
                    items = [i for i in data.get("items", []) if isinstance(i, dict)]
                    keys = ",".join(sorted(data.keys()))[:60]
                else:
                    items, keys = [], f"payload_type={type(data).__name__}"
                lvl = max((i.get("nsfwLevel", 0) for i in items), default=0)
                print(f"    [{'PASS' if items else 'FAIL'}] {name}: HTTP {r.status_code}, "
                      f"items={len(items)}, maxNsfw={lvl}, keys={keys}")
                if not items and r.status_code != 200:
                    print(f"           body: {(r.text or '')[:220]!r}")
            except Exception as e:
                print(f"    [ERROR] {name}: {e}")

    print("\n  --- control: same scraper-default call with COOKIES, not Bearer ---")
    for base in (RED, COM):
        session = fresh_session(base)
        n = load_cookies_into(session, base)
        if n == 0:
            print(f"    [SKIP] {base}: no cookies.txt found")
            results[f"COOKIE_CONTROL_{base[-3:].upper()}"] = "skip"
            continue
        try:
            r = trpc_get(session, base, "image.getInfinite", base_input, timeout=30)
            data = trpc_json(r) or {}
            items = [i for i in (data.get("items", []) if isinstance(data, dict) else [])
                     if isinstance(i, dict)]
            lvl = max((i.get("nsfwLevel", 0) for i in items), default=0)
            report(f"cookies on {base} ({n} cookies)", f"COOKIE_CONTROL_{base[-3:].upper()}",
                   r.status_code == 200 and len(items) > 0, r.status_code,
                   f"items={len(items)}, maxNsfw={lvl}")
        except Exception as e:
            print(f"    [ERROR] {e}")
            results[f"COOKIE_CONTROL_{base[-3:].upper()}"] = "error"


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[1])
    parser.add_argument("--client-id", default="",
                        help="Public OAuth client ID (or set CIVITAI_OAUTH_CLIENT_ID)")
    parser.add_argument("--redirect", default=DEFAULT_REDIRECT,
                        help=f"Exact redirect URI registered at Civitai (default {DEFAULT_REDIRECT})")
    parser.add_argument("--scope", default="dt",
                        help="'dt' (identity+media+collections, the default), 'discover' "
                             "(every bit), 'registered' (send no scope at all - Civitai "
                             "rejects this with invalid_scope, kept only to re-test), or an "
                             "explicit integer bitmask")
    parser.add_argument("--posting", action="store_true",
                        help="Also probe write access (creates then deletes a private draft post)")
    parser.add_argument("--diagnose-images", metavar="COLLECTION_ID", nargs="?",
                        const="config", default=None,
                        help="After signing in, run an input matrix against image.getInfinite "
                             "plus a cookie control, to explain zero-item results. Defaults to "
                             "the first config.yaml collection.")
    args = parser.parse_args()

    client_id = (args.client_id or os.environ.get("CIVITAI_OAUTH_CLIENT_ID", "")).strip()
    if not client_id:
        print("No OAuth client ID. Register a public app at")
        print("  civitai.com -> Account Settings -> OAuth Applications -> Register App")
        print(f"with the exact redirect URI: {args.redirect}")
        print("then:")
        print("  set CIVITAI_OAUTH_CLIENT_ID=<client id>")
        print("  .venv\\Scripts\\python.exe probe_oauth.py")
        sys.exit(1)

    if args.scope == "registered":
        scope = None
    elif args.scope == "dt":
        scope = DT_READ_SCOPE
    elif args.scope == "discover":
        scope = DISCOVER_SCOPE
    else:
        try:
            scope = int(args.scope)
        except ValueError:
            print("--scope must be 'registered', 'dt', 'discover', or an integer; "
                  f"got {args.scope!r}")
            sys.exit(1)
    if args.posting and args.scope == "dt":
        print("NOTE: --posting with the read-only 'dt' scope will fail. Register the app")
        print("      with Media & Posts Write and use --scope registered.\n")

    print(f"Client ID present. Redirect URI: {args.redirect}")
    print("This URI must be registered at Civitai as an EXACT match, on a public "
          "(browser/mobile) app with no client secret.\n")

    access, refresh = run_oauth_flow(client_id, args.redirect, scope)
    if access:
        print()
        probe_endpoints(access, args.posting)
        if args.diagnose_images is not None:
            target = get_config_collection_id() if args.diagnose_images == "config" \
                else args.diagnose_images
            diagnose_images(access, target)
        print()
        probe_refresh(client_id, refresh)
    else:
        print("\nAuthorization did not complete; endpoint probes skipped.")

    print("\n" + "=" * 60)
    print("SUMMARY (paste this back)")
    print("=" * 60)
    print(f"# redirect_uri: {args.redirect}")
    print(f"# requested scope: {scope} ({describe_scope(scope)})")
    for k in sorted(results):
        print(f"{k}={results[k]}")


if __name__ == "__main__":
    main()
