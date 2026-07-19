"""
Standalone probe: can we read (and create) the user's own CivitAI posts,
including SCHEDULED ones (publishedAt in the future)?

Feeds the "CivitAI Posts Calendar" feature. Answers, empirically:
  P1. post.getInfinite: does it return own posts with id/publishedAt/images[]?
  P2. Which filter (section/draftOnly/pending) exposes SCHEDULED posts?
  P3. Per-post images: does image.getInfinite {postId, pending} expose
      name (original filename), width, height? What does post.getEdit return?
  P4. publishedAt precision (hour/minute, UTC marker) - raw strings printed.
  P5. Username via REST /v1/users/me (Bearer).
With --posting (opt-in, uses your account, cleans up after itself):
  P6. Upload handshake: POST /api/v1/image-upload -> presigned URL -> PUT bytes.
  P7. post.create draft -> post.addImage -> post.update {publishedAt: future}.
  P8. post.delete cleanup.

Usage:
    set CIVITAI_API_KEY=xxxx        (optional but recommended)
    .venv\Scripts\python.exe probe_posts.py [--posting] [--post-id N] [--username NAME]

Cookies (civitai*_cookies.txt in this folder) are used for tRPC calls; the
key is used for REST and tried on tRPC. Nothing secret is ever printed.
"""

import argparse
import base64
import datetime
import json
import os
import sys

from probe_auth import fresh_session, load_cookies_into, trpc_get, trpc_post, trpc_json, COM, RED

results = {}

# 1x1 transparent PNG for the upload probe.
TINY_PNG = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGBgAAAABQAB"
    "h6FO1AAAAABJRU5ErkJggg=="
)


def report(label, key, ok, status, evidence):
    verdict = "PASS" if ok else "FAIL"
    print(f"  [{verdict}] {label} (HTTP {status}) - {evidence}")
    results[key] = "yes" if ok else "no"
    return ok


def snippet(resp, n=400):
    try:
        return resp.text[:n].replace("\n", " ")
    except Exception:
        return "<unreadable>"


def summarize_post(p):
    """Compact one-line description of a post item."""
    if not isinstance(p, dict):
        return str(type(p))
    imgs = p.get("images")
    return (f"id={p.get('id')} publishedAt={p.get('publishedAt')!r} "
            f"title={str(p.get('title'))[:20]!r} "
            f"images={'list[%d]' % len(imgs) if isinstance(imgs, list) else ('count:' + str(imgs) if imgs is not None else 'absent')} "
            f"keys={sorted(p.get('__probed_keys__', p.keys()))[:14]}")


def cookie_session(base):
    s = fresh_session(base)
    n = load_cookies_into(s, base)
    return s, n


def get_posts(session, base, input_json):
    r = trpc_get(session, base, "post.getInfinite", input_json, timeout=30)
    data = trpc_json(r) or {}
    items = data.get("items", []) if isinstance(data, dict) else (data if isinstance(data, list) else [])
    return r, items


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--posting", action="store_true",
                        help="Run write probes P6-P8 (creates then deletes a private draft post)")
    parser.add_argument("--post-id", type=int, default=None,
                        help="A known post id of yours (ideally a scheduled one) for post.getEdit inspection")
    parser.add_argument("--username", default=None, help="CivitAI username (else resolved via API key)")
    args = parser.parse_args()

    key = os.environ.get("CIVITAI_API_KEY", "").strip()
    print(f"API key: {'present' if key else 'ABSENT (tRPC probes will rely on cookies only)'}")

    # --- P5 first: username --------------------------------------------------
    username = args.username
    print("P5. Username resolution")
    if key:
        s = fresh_session(COM, bearer=key)
        try:
            r = s.get(f"{COM}/api/v1/users/me", timeout=20)
            if r.status_code == 200:
                body = r.json()
                username = username or body.get("username") or body.get("user", {}).get("username")
            report("REST /v1/users/me", "USERNAME_VIA_KEY", r.status_code == 200,
                   r.status_code, f"username={username!r}")
        except Exception as e:
            print(f"  [ERROR] {e}")
    if not username:
        print("  [WARN] no username available - pass --username; post feed probes may return nothing")

    for base, tag in ((RED, "RED"), (COM, "COM")):
        print(f"\n===== base {base} =====")
        s_cookie, n_cookies = cookie_session(base)
        sessions = [("cookies", s_cookie)] if n_cookies else []
        if key:
            sessions.append(("bearer", fresh_session(base, bearer=key)))
        if not sessions:
            print("  [SKIP] no cookies and no key")
            continue

        for auth_name, s in sessions:
            print(f"-- auth: {auth_name} --")

            # --- P1: own published posts ---------------------------------
            base_input = {"period": "AllTime", "sort": "Newest", "limit": 20, "authed": True}
            if username:
                base_input["username"] = username
            try:
                r, items = get_posts(s, base, dict(base_input))
                ev = f"items={len(items)}"
                if items:
                    ev += " | " + summarize_post(items[0])
                report(f"P1 post.getInfinite baseline", f"POSTS_{tag}_{auth_name.upper()}",
                       r.status_code == 200 and len(items) > 0, r.status_code, ev)
                if r.status_code != 200:
                    print(f"    body: {snippet(r)}")
            except Exception as e:
                print(f"  [ERROR] P1: {e}")
                continue

            # --- P2: filter variants hunting for SCHEDULED posts ----------
            now = datetime.datetime.now(datetime.timezone.utc)
            variants = [
                ("section=scheduled", {"section": "scheduled"}),
                ("section=draft", {"section": "draft"}),
                ("section=published", {"section": "published"}),
                ("draftOnly=true", {"draftOnly": True}),
                ("pending=true", {"pending": True}),
            ]
            for vname, extra in variants:
                inp = dict(base_input)
                inp.update(extra)
                try:
                    r, items = get_posts(s, base, inp)
                    future = []
                    for p in items:
                        pa = p.get("publishedAt") if isinstance(p, dict) else None
                        if isinstance(pa, str) and pa[:19] > now.strftime("%Y-%m-%dT%H:%M:%S"):
                            future.append(p)
                    ev = f"items={len(items)}, future-published={len(future)}"
                    if future:
                        ev += " | " + summarize_post(future[0])
                        results[f"SCHEDULED_VIA_{vname}_{tag}_{auth_name.upper()}"] = "yes"
                    ok = r.status_code == 200
                    print(f"  [{'PASS' if ok else 'FAIL'}] P2 {vname} (HTTP {r.status_code}) - {ev}")
                    if r.status_code not in (200, 401, 403) or (r.status_code == 200 and not items and vname == "section=scheduled"):
                        print(f"    body: {snippet(r, 250)}")
                except Exception as e:
                    print(f"  [ERROR] P2 {vname}: {e}")

            # --- P3: per-post images --------------------------------------
            probe_post_id = args.post_id
            if not probe_post_id:
                try:
                    _, items = get_posts(s, base, dict(base_input))
                    probe_post_id = items[0].get("id") if items else None
                except Exception:
                    probe_post_id = None
            if probe_post_id:
                try:
                    r = trpc_get(s, base, "image.getInfinite",
                                 {"postId": probe_post_id, "pending": True, "limit": 20, "authed": True},
                                 timeout=30)
                    data = trpc_json(r) or {}
                    imgs = data.get("items", []) if isinstance(data, dict) else []
                    ev = f"postId={probe_post_id}, images={len(imgs)}"
                    if imgs:
                        i0 = imgs[0]
                        ev += (f" | name={i0.get('name')!r} w={i0.get('width')} h={i0.get('height')}"
                               f" nsfwLevel={i0.get('nsfwLevel')} keys={sorted(i0.keys())[:14]}")
                    report("P3 image.getInfinite{postId,pending}", f"POST_IMAGES_{tag}_{auth_name.upper()}",
                           r.status_code == 200 and len(imgs) > 0, r.status_code, ev)
                except Exception as e:
                    print(f"  [ERROR] P3: {e}")
                try:
                    r = trpc_get(s, base, "post.getEdit", {"id": probe_post_id}, timeout=20)
                    data = trpc_json(r)
                    keys = sorted(data.keys())[:16] if isinstance(data, dict) else type(data).__name__
                    print(f"  [info] P3b post.getEdit HTTP {r.status_code} keys={keys}")
                    if isinstance(data, dict) and isinstance(data.get("images"), list) and data["images"]:
                        print(f"         first edit-image keys={sorted(data['images'][0].keys())[:16]}")
                except Exception as e:
                    print(f"  [ERROR] P3b: {e}")

            # --- P4: publishedAt raw strings ------------------------------
            try:
                _, items = get_posts(s, base, dict(base_input))
                raws = [p.get("publishedAt") for p in items[:5] if isinstance(p, dict)]
                print(f"  [info] P4 publishedAt raw samples: {raws}")
            except Exception as e:
                print(f"  [ERROR] P4: {e}")

    # --- P6-P8: write probes (opt-in) -----------------------------------------
    if args.posting:
        print("\n===== write probes (P6-P8) on", RED, "=====")
        s, n = cookie_session(RED)
        if n == 0:
            print("  [SKIP] no cookies.txt found - write probes need a session")
        else:
            _write_probes(s, RED)
    else:
        print("\nP6-P8 skipped (run with --posting to test upload/schedule; creates then deletes a draft).")

    print("\n" + "=" * 60)
    print("SUMMARY (paste this back)")
    print("=" * 60)
    for k in sorted(results):
        print(f"{k}={results[k]}")


def _write_probes(session, base):
    post_id = None
    try:
        # P7a: draft post
        r = trpc_post(session, base, "post.create", {"authed": True})
        data = trpc_json(r)
        post_id = data.get("id") if isinstance(data, dict) else None
        report("P7a post.create draft", "POSTCREATE", r.status_code == 200 and post_id is not None,
               r.status_code, f"post id={post_id}")
        if post_id is None:
            print(f"    body: {snippet(r)}")
            return

        # P6: upload handshake - the website's image upload endpoint.
        upload_url = None
        upload_key = None
        try:
            r = session.post(f"{base}/api/v1/image-upload",
                             json={"filename": "probe_calendar.png", "metadata": {}},
                             timeout=30)
            ok = r.status_code == 200
            body = r.json() if ok else {}
            upload_url = body.get("uploadURL") or body.get("uploadUrl") or body.get("url")
            upload_key = body.get("id") or body.get("key")
            report("P6a image-upload handshake", "UPLOAD_HANDSHAKE", ok and bool(upload_url),
                   r.status_code, f"keys={sorted(body.keys()) if ok else snippet(r, 200)}")
        except Exception as e:
            print(f"  [ERROR] P6a: {e}")

        if upload_url:
            try:
                r = session.put(upload_url, data=TINY_PNG,
                                headers={"Content-Type": "image/png"}, timeout=60)
                report("P6b PUT bytes to presigned URL", "UPLOAD_PUT",
                       r.status_code in (200, 201, 204), r.status_code, f"key={upload_key!r}")
            except Exception as e:
                print(f"  [ERROR] P6b: {e}")

        # P7b: attach image to post (field names are the big unknown - a 400
        # with a validation error is VALUABLE output: it names required fields).
        if upload_key:
            try:
                r = trpc_post(session, base, "post.addImage", {
                    "postId": post_id,
                    "url": upload_key,
                    "name": "probe_calendar.png",
                    "width": 1,
                    "height": 1,
                    "index": 0,
                    "mimeType": "image/png",
                })
                report("P7b post.addImage", "ADDIMAGE", r.status_code == 200,
                       r.status_code, snippet(r, 300) if r.status_code != 200 else "attached")
            except Exception as e:
                print(f"  [ERROR] P7b: {e}")

        # P7c: schedule - set future publishedAt on the draft.
        future = (datetime.datetime.now(datetime.timezone.utc)
                  + datetime.timedelta(days=7)).strftime("%Y-%m-%dT%H:00:00.000Z")
        try:
            r = trpc_post(session, base, "post.update", {"id": post_id, "publishedAt": future})
            report("P7c post.update publishedAt(future)", "SCHEDULE_UPDATE", r.status_code == 200,
                   r.status_code, f"publishedAt={future}" if r.status_code == 200 else snippet(r, 300))
        except Exception as e:
            print(f"  [ERROR] P7c: {e}")

    finally:
        # P8: cleanup, always attempted.
        if post_id is not None:
            try:
                r = trpc_post(session, base, "post.delete", {"id": post_id})
                deleted = r.status_code == 200
                report("P8 post.delete cleanup", "CLEANUP", deleted, r.status_code,
                       "deleted" if deleted else f"DELETE FAILED - remove manually: {base}/posts/{post_id}")
            except Exception as e:
                print(f"  [ERROR] P8: {e} - remove manually: {base}/posts/{post_id}")


if __name__ == "__main__":
    main()
