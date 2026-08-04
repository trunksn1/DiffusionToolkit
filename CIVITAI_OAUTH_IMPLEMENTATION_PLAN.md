# CivitAI OAuth Implementation Plan

Replacing the manual API key with a "Sign in with CivitAI" flow, so the
collections picker, the collections scraper, and the posts calendar all work
against the user's real account without hand-managed keys or exported cookies.

Status: **probe complete, implementation in progress.** Every claim in the
"Verified behavior" section below was measured against the live service on
2026-08-04 by `Diffusion.PyScripts/Civitai Collections Scraper/probe_oauth.py`.
Nothing here is inferred from documentation.

---

## Verified behavior

CivitAI runs a real OAuth 2.0 Authorization Code + PKCE (S256) server at
`https://auth.civitai.com/api/auth/oauth/{authorize,token,userinfo}`.

| Behavior | Result |
| --- | --- |
| Loopback redirect `http://127.0.0.1:8765/callback` | Accepted |
| Code exchange, public client, no secret | Works (`Bearer`, `expires_in=3600`) |
| `refresh_token` grant | Works, **rotates the refresh token** |
| `/userinfo` | Works — returns id and username |
| tRPC `collection.getAllUser` (.com and .red) | Works |
| tRPC `collection.getById` | Works |
| tRPC `image.getInfinite`, `browsingLevel=31` | Works (100 items, nsfwLevel 16 on .red) |
| tRPC `image.getGenerationData` | Works |
| tRPC `post.create` | Works with scope bit 64 |
| REST `/api/v1/users/me` | **401 — OAuth tokens are rejected** |

### Scope is a mandatory integer bitmask

Omitting the `scope` parameter fails with
`{"error":"invalid_scope","error_description":"Invalid scope value"}`. The
server does **not** fall back to the permissions the application was registered
with, so the client must always send an explicit mask.

Verified bit values:

| Bit | Permission | Needed for |
| ---: | --- | --- |
| 1 | Profile & Settings Read | Identity for the posts calendar |
| 32 | Media & Posts Read | Images and generation data |
| 64 | Media & Posts Write | Calendar scheduling (`post.create`) |
| 131072 | Collections Read | The collections picker |

Diffusion Toolkit requests `131169` (`1 | 32 | 64 | 131072`), which is granted
verbatim. Bits 4, 262144, 524288, 2097152 and 4194304 are documented elsewhere
but unused here. Delete permissions are deliberately never requested — one
consequence is that `post.delete` returns 403, so the probe's `--posting` mode
cannot clean up the draft post it creates.

### The constraint that shapes the design

**tRPC accepts OAuth; REST v1 does not.** This is not a replacement of the API
key but a second credential, selected per endpoint family:

| Family | Primary credential | Fallback |
| --- | --- | --- |
| tRPC | OAuth access token | API key, then cookies |
| REST v1 | API key | cookies |

`api_client.py::_auth_get` already memoizes Bearer acceptance per family, so it
needs a second credential rather than a redesign.

---

## Registration

CivitAI account settings -> OAuth Applications -> Register App:

- **App type:** Browser / Mobile App (`public`). Not Server App — a confidential
  client gets a secret, and a secret shipped inside a distributed `.exe` is not
  a secret. CivitAI lists desktop apps under this type.
- **Redirect URI:** `http://127.0.0.1:8765/callback`, exact match.
- **Permissions:** Profile & Settings Read; Media & Posts Read + Write;
  Collections Read + Write; Models Read. Leave every Delete column unticked.

The resulting client ID is an identifier, not a credential, and is compiled into
the application.

---

## Components

### 1. `Diffusion.Toolkit/Services/CivitaiOAuthService.cs` (new)

Ports the verified probe flow. Public surface:

- `SignInAsync(CancellationToken)` — PKCE verifier/challenge, random state,
  start the loopback listener, open the system browser, await the callback,
  validate state, exchange the code, fetch `/userinfo`, persist the session.
- `GetValidAccessTokenAsync(CancellationToken)` — returns a live access token,
  refreshing when it is within 60s of expiry.
- `SignOut()` — clears the local session. For a public client this is local
  only; the server-side grant is revoked from CivitAI account settings.
- `GetState()` — connected/disconnected plus the connected username, for the UI.

### 2. Session storage

`Settings.CivitaiOAuthSessionProtected` alongside the existing
`CivitaiApiKeyProtected` (`Configuration/Settings.cs`). The envelope
(`accessToken`, `refreshToken`, `expiresAtUtc`, `scope`, `userId`, `username`)
is serialized to JSON and passed through the existing
`Common/ProtectedString.cs` (DPAPI, CurrentUser), so plaintext never reaches
`settings.json`. A session that fails to decrypt — a settings file copied
between Windows users — is treated as signed out, matching how the API key
already behaves.

### 3. Credential routing

- Three Python launch sites gain a `CIVITAI_ACCESS_TOKEN` environment variable
  beside the existing `CIVITAI_API_KEY`: `MainWindow.xaml.cs` (sync),
  `Pages/Settings.xaml.cs` (`list-collections`), and
  `Services/CivitaiPostsService.cs` (posts and scheduling). Credentials go via
  the environment only, never on the command line, which is written to
  `DiffusionToolkit.log`.
- `Diffusion.Civitai/CivitaiClient.cs` takes an optional access token and uses
  it for tRPC while keeping the API key for REST.
- `api_client.py` reads `CIVITAI_ACCESS_TOKEN` and prefers it for
  `family='trpc'`, keeping the API key for `family='rest'`.

### 4. Settings UI

A "Connect CivitAI account" button showing the connected username, and Sign out.
The API key box stays: REST v1 still requires it, and it remains the fallback
if OAuth is unavailable.

---

## Design decisions

**Use `TcpListener`, not `HttpListener`.** `HttpListener` requires a
`netsh http add urlacl` reservation for non-elevated processes on Windows, so
it would fail with `Access is denied` for ordinary users. A `TcpListener` bound
to `127.0.0.1:8765` speaking minimal HTTP has no such requirement. The Python
probe worked this way.

**The port is fixed.** CivitAI matches `redirect_uri` exactly, so 8765 cannot be
dynamic. If the port is occupied, sign-in fails with an explicit message rather
than silently falling back.

**Refresh rotates the refresh token.** The store must be rewritten on every
refresh, and concurrent refreshes serialized behind a `SemaphoreSlim` —
otherwise two parallel refreshes rotate each other into invalidity.

**Access tokens last one hour, and a full sync can exceed that.** The token is
refreshed immediately before each Python launch and the existing cookie/API-key
fallback absorbs any overflow. Letting Python refresh would require it to write
back into the DPAPI store, which is not worth the coupling.

---

## Sequencing

Each step leaves the API key path working, so nothing breaks if a later step is
deferred.

1. `CivitaiOAuthService` and session storage; verify sign-in, refresh, sign-out.
2. The `list-collections` picker — smallest surface, immediate payoff.
3. The collections scraper sync.
4. The posts calendar.

---

## Notes and gotchas

- The tRPC response format is mid-migration: `collection.getAllUser` still
  returns the legacy `{"result":{"data":{"json":...}}}` wrapper while
  `image.getInfinite` returns a reference table delivered as a JSON *string*.
  `api_client.py::_extract_trpc_json` handles both; anything parsing tRPC must
  go through it or it will silently read zero items.
- NSFW collections return images only on `civitai.red`; the same query on
  `civitai.com` returns zero items. This is the existing domain split, not an
  auth failure.
- `civitai.red` enforces an origin check on authenticated tRPC endpoints —
  requests need matching `Referer`/`Origin` headers even with a valid token.
- OAuth already outperforms the cookie path in practice: during the probe the
  exported cookies had expired and returned zero items where OAuth returned 100.
