"""
Standalone probe: which CivitAI endpoints accept an API-key Bearer token?

Answers, empirically, the unknowns behind the API-key migration:
  A. Is the key valid at all?                (REST /v1/users/me on civitai.com)
  B. Does civitai.red honor a .com token?    (REST /v1/users/me on civitai.red)
  C. Does tRPC accept Bearer on .com?        (collection.getAllUser)
  D. Does tRPC accept Bearer on .red?        (collection.getAllUser)
  E. NSFW collection paging with Bearer only (image.getInfinite, browsingLevel 31)
  F. Does REST /v1/images accept collectionId? (semantic check, not just HTTP 200)
  G. image.getGenerationData: Bearer vs anonymous (the C# metadata-fetch path)
  H. Does the image CDN require any auth at all?
  I. tRPC posting with cookies only (post.create draft, then post.delete cleanup)
  J. tRPC posting with Bearer only (expected to fail; confirms write-tRPC policy)

Usage (never pass the key as an argument - env var only):
    set CIVITAI_API_KEY=xxxx
    .venv\Scripts\python.exe probe_auth.py            # read-only probes A-H
    .venv\Scripts\python.exe probe_auth.py --posting  # also run I/J (creates and
                                                      # deletes a private draft post)

The key is never printed. Each probe reports PASS/FAIL, the HTTP status, and
one line of evidence. A paste-able summary block is printed at the end.
"""

import argparse
import json
import os
import sys
from pathlib import Path

import requests

COM = "https://civitai.com"
RED = "https://civitai.red"
SCRIPT_DIR = Path(__file__).parent

# Known-public image on civitai.com used for probe G (any SFW front-page id works).
PUBLIC_IMAGE_ID = 728718

results = {}  # summary key -> "yes" / "no" / "skip" / "error"


def fresh_session(host_base, bearer=None):
    """Cookie-free session with the same spoofed headers the scraper uses."""
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


def trpc_get(session, base, procedure, input_json, timeout=20):
    params = {"input": json.dumps({"json": input_json}, separators=(",", ":"))}
    return session.get(f"{base}/api/trpc/{procedure}", params=params, timeout=timeout)


def trpc_post(session, base, procedure, input_json, timeout=20):
    return session.post(
        f"{base}/api/trpc/{procedure}",
        json={"json": input_json},
        timeout=timeout,
    )


def trpc_json(resp):
    try:
        return resp.json().get("result", {}).get("data", {}).get("json")
    except Exception:
        return None


def report(label, key, ok, status, evidence):
    verdict = "PASS" if ok else "FAIL"
    print(f"  [{verdict}] {label} (HTTP {status}) - {evidence}")
    results[key] = "yes" if ok else "no"
    return ok


def get_config_collection_id():
    """First collection id from config.yaml, without requiring PyYAML structure."""
    try:
        import yaml
        with open(SCRIPT_DIR / "config.yaml", "r", encoding="utf-8") as f:
            cfg = yaml.safe_load(f)
        return cfg["collections"][0]["id"]
    except Exception:
        return 1107870


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[1])
    parser.add_argument("--posting", action="store_true",
                        help="Also run posting probes I/J (creates then deletes a private draft post)")
    args = parser.parse_args()

    key = os.environ.get("CIVITAI_API_KEY", "").strip()
    if not key:
        print("CIVITAI_API_KEY environment variable is not set.")
        print("Generate a key at civitai.com -> Account Settings -> API Keys, then:")
        print("  set CIVITAI_API_KEY=<your key>")
        print("  .venv\\Scripts\\python.exe probe_auth.py")
        sys.exit(1)
    print("API key present.")

    collection_id = get_config_collection_id()
    print(f"Using collection id {collection_id} for collection probes.\n")

    # --- A/B: REST key validity -------------------------------------------
    print("A. REST /v1/users/me on civitai.com (Bearer only)")
    username = None
    for label, base, rkey in (("A", COM, "BEARER_REST_COM"), ("B", RED, "BEARER_REST_RED")):
        if label == "B":
            print("B. REST /v1/users/me on civitai.red (Bearer only)")
        s = fresh_session(base, bearer=key)
        try:
            r = s.get(f"{base}/api/v1/users/me", timeout=20)
            body = r.json() if r.status_code == 200 else {}
            user = body.get("username") or body.get("user", {}).get("username")
            if label == "A" and user:
                username = user
            report(f"users/me on {base}", rkey, r.status_code == 200,
                   r.status_code, f"username={user!r}")
        except Exception as e:
            print(f"  [ERROR] {e}")
            results[rkey] = "error"

    # --- C/D: tRPC read with Bearer only -----------------------------------
    for label, base, rkey in (("C", COM, "BEARER_TRPC_COM"), ("D", RED, "BEARER_TRPC_RED")):
        print(f"{label}. tRPC collection.getAllUser on {base} (Bearer only)")
        s = fresh_session(base, bearer=key)
        try:
            r = trpc_get(s, base, "collection.getAllUser", {"limit": 1, "sort": "Newest"})
            data = trpc_json(r)
            n = len(data) if isinstance(data, list) else len(data.get("items", [])) if isinstance(data, dict) else 0
            report(f"collection.getAllUser on {base}", rkey,
                   r.status_code == 200, r.status_code, f"collections returned={n}")
        except Exception as e:
            print(f"  [ERROR] {e}")
            results[rkey] = "error"

    # --- E: NSFW collection paging, Bearer only on .red ---------------------
    print("E. tRPC image.getInfinite (browsingLevel 31) on civitai.red (Bearer only)")
    bearer_image_ids = set()
    s = fresh_session(RED, bearer=key)
    try:
        r = trpc_get(s, RED, "image.getInfinite", {
            "collectionId": collection_id, "period": "AllTime", "sort": "Newest",
            "browsingLevel": 31, "include": ["cosmetics"], "disablePoi": True,
            "disableMinor": False, "authed": True,
        }, timeout=30)
        data = trpc_json(r) or {}
        items = data.get("items", []) if isinstance(data, dict) else []
        bearer_image_ids = {i.get("id") for i in items if isinstance(i, dict)}
        max_lvl = max((i.get("nsfwLevel", 0) for i in items if isinstance(i, dict)), default=0)
        report("image.getInfinite Bearer-only", "BEARER_IMAGES_RED",
               r.status_code == 200 and len(items) > 0, r.status_code,
               f"items={len(items)}, max nsfwLevel={max_lvl}")
    except Exception as e:
        print(f"  [ERROR] {e}")
        results["BEARER_IMAGES_RED"] = "error"

    # Cookie comparison for E (item count parity)
    print("E2. Same call, cookies only (comparison)")
    s = fresh_session(RED)
    n_cookies = load_cookies_into(s, RED)
    if n_cookies == 0:
        print("  [SKIP] no cookies.txt found")
        results["COOKIE_IMAGES_RED"] = "skip"
    else:
        try:
            r = trpc_get(s, RED, "image.getInfinite", {
                "collectionId": collection_id, "period": "AllTime", "sort": "Newest",
                "browsingLevel": 31, "include": ["cosmetics"], "disablePoi": True,
                "disableMinor": False, "authed": True,
            }, timeout=30)
            data = trpc_json(r) or {}
            items = data.get("items", []) if isinstance(data, dict) else []
            max_lvl = max((i.get("nsfwLevel", 0) for i in items if isinstance(i, dict)), default=0)
            report("image.getInfinite cookies-only", "COOKIE_IMAGES_RED",
                   r.status_code == 200 and len(items) > 0, r.status_code,
                   f"items={len(items)}, max nsfwLevel={max_lvl}")
        except Exception as e:
            print(f"  [ERROR] {e}")
            results["COOKIE_IMAGES_RED"] = "error"

    # --- F: REST /v1/images collectionId (semantic) -------------------------
    print("F. REST /v1/images?collectionId (semantic check, Bearer)")
    for base, rkey in ((COM, "REST_COLLECTIONID_COM"), (RED, "REST_COLLECTIONID_RED")):
        s = fresh_session(base, bearer=key)
        try:
            r_with = s.get(f"{base}/api/v1/images",
                           params={"limit": 5, "collectionId": collection_id}, timeout=20)
            r_without = s.get(f"{base}/api/v1/images", params={"limit": 5}, timeout=20)
            ids_with = {i["id"] for i in r_with.json().get("items", [])} if r_with.status_code == 200 else set()
            ids_without = {i["id"] for i in r_without.json().get("items", [])} if r_without.status_code == 200 else set()
            # Param is honored only if the result set differs from the global feed
            # AND (when probe E worked) overlaps the collection's actual images.
            differs = bool(ids_with) and ids_with != ids_without
            overlaps = bool(bearer_image_ids & ids_with) if bearer_image_ids else None
            honored = differs and (overlaps is not False)
            report(f"collectionId on {base}", rkey, honored, r_with.status_code,
                   f"differs_from_global={differs}, overlaps_probe_E={overlaps}")
        except Exception as e:
            print(f"  [ERROR] {e}")
            results[rkey] = "error"

    # --- G: generation data (C# path) ---------------------------------------
    print("G. tRPC image.getGenerationData on civitai.com")
    gen_ids = [("public", PUBLIC_IMAGE_ID)]
    nsfw_id = next(iter(bearer_image_ids), None)
    if nsfw_id:
        gen_ids.append(("collection/NSFW", nsfw_id))
    for label, img_id in gen_ids:
        for mode, bearer in (("anonymous", None), ("bearer", key)):
            s = fresh_session(COM, bearer=bearer)
            try:
                r = trpc_get(s, COM, "image.getGenerationData", {"id": img_id})
                data = trpc_json(r)
                has_meta = bool(data and (data.get("meta") or data.get("resources")))
                rkey = f"GENDATA_{mode.upper()}_{'NSFW' if label != 'public' else 'PUBLIC'}"
                report(f"{label} image {img_id}, {mode}", rkey,
                       r.status_code == 200, r.status_code, f"has_meta_or_resources={has_meta}")
            except Exception as e:
                print(f"  [ERROR] {e}")

    # --- H: CDN needs auth? --------------------------------------------------
    print("H. CDN download with no auth at all")
    try:
        s = requests.Session()  # deliberately bare: no cookies, no bearer, no spoofed headers
        # Grab a URL fragment from probe E if available; otherwise skip.
        if bearer_image_ids:
            r_probe = trpc_get(fresh_session(RED, bearer=key), RED, "image.getInfinite", {
                "collectionId": collection_id, "period": "AllTime", "sort": "Newest",
                "browsingLevel": 31, "include": ["cosmetics"], "disablePoi": True,
                "disableMinor": False, "authed": True,
            }, timeout=30)
            items = (trpc_json(r_probe) or {}).get("items", [])
            first = next((i for i in items if isinstance(i, dict) and i.get("url")), None)
            if first:
                url_part = first["url"].split("?")[0].strip("/")
                cdn = f"https://image.civitai.com/xG1nkqKTMzGDvpLrqFT7WA/{url_part}/original=true,quality=90//probe"
                r = s.get(cdn, stream=True, timeout=30)
                chunk = next(r.iter_content(1024), b"")
                report("CDN anonymous fetch", "CDN_NO_AUTH",
                       r.status_code == 200 and len(chunk) > 0, r.status_code,
                       f"first chunk bytes={len(chunk)}")
                r.close()
            else:
                print("  [SKIP] no image URL available from probe E")
                results["CDN_NO_AUTH"] = "skip"
        else:
            print("  [SKIP] probe E returned no images")
            results["CDN_NO_AUTH"] = "skip"
    except Exception as e:
        print(f"  [ERROR] {e}")
        results["CDN_NO_AUTH"] = "error"

    # --- I/J: posting probes (opt-in) ----------------------------------------
    if args.posting:
        print("I. tRPC post.create draft with cookies only on civitai.red (then post.delete)")
        s = fresh_session(RED)
        n_cookies = load_cookies_into(s, RED)
        if n_cookies == 0:
            print("  [SKIP] no cookies.txt found")
            results["POST_COOKIES_RED"] = "skip"
        else:
            _probe_posting(s, RED, "POST_COOKIES_RED")

        print("J. tRPC post.create draft with Bearer only on civitai.red")
        s = fresh_session(RED, bearer=key)
        _probe_posting(s, RED, "POST_BEARER_RED")
    else:
        print("I/J. Posting probes skipped (run with --posting to include them).")
        results["POST_COOKIES_RED"] = "skip"
        results["POST_BEARER_RED"] = "skip"

    # --- Summary --------------------------------------------------------------
    print("\n" + "=" * 60)
    print("SUMMARY (paste this back)")
    print("=" * 60)
    if username:
        print(f"# authenticated as: {username}")
    for k in sorted(results):
        print(f"{k}={results[k]}")


def _probe_posting(session, base, rkey):
    """Create a draft post, report, then delete it. Draft posts are private."""
    try:
        r = trpc_post(session, base, "post.create", {"authed": True})
        data = trpc_json(r)
        post_id = data.get("id") if isinstance(data, dict) else None
        report("post.create", rkey, r.status_code == 200 and post_id is not None,
               r.status_code, f"draft post id={post_id}")
        if post_id is not None:
            rd = trpc_post(session, base, "post.delete", {"id": post_id})
            deleted = rd.status_code == 200
            print(f"  cleanup: post.delete -> HTTP {rd.status_code} "
                  f"({'deleted' if deleted else 'DELETE FAILED - remove draft manually at '+base+'/posts/'+str(post_id)})")
            results[rkey + "_CLEANUP"] = "yes" if deleted else "no"
    except Exception as e:
        print(f"  [ERROR] {e}")
        results[rkey] = "error"


if __name__ == "__main__":
    main()
