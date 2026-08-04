"""
CivitAI API client with API-key (Bearer) authentication and cookie fallback.
An official API key is preferred wherever CivitAI accepts it; browser cookies
remain the fallback for endpoints that require a session (and when no key is
configured, behavior is identical to the original cookie-only client).
"""

import json
import logging
import os
from typing import List, Optional, Dict, Any, Tuple
from pathlib import Path
import time

import requests
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry
import browser_cookie3

from models import ImageItem, Collection

logger = logging.getLogger(__name__)


def _decode_reference_table(value: Any) -> Any:
    """Decode the reference-table JSON format returned by newer tRPC responses."""
    if not isinstance(value, list) or not value:
        return value

    decoded: Dict[int, Any] = {}

    def decode_index(index: int) -> Any:
        # The serializer uses negative indexes for JavaScript-only values.
        if index < 0:
            return None
        if index >= len(value):
            raise ValueError(f"Reference index {index} is outside the value table")
        if index in decoded:
            return decoded[index]

        item = value[index]
        if isinstance(item, dict):
            result: Dict[str, Any] = {}
            decoded[index] = result
            for key, reference in item.items():
                result[key] = (
                    decode_index(reference) if isinstance(reference, int) else reference
                )
            return result

        if isinstance(item, list):
            result_list: List[Any] = []
            decoded[index] = result_list
            result_list.extend(
                decode_index(reference) if isinstance(reference, int) else reference
                for reference in item
            )
            return result_list

        decoded[index] = item
        return item

    return decode_index(0)


def _extract_trpc_json(data: Any) -> Any:
    """Normalize legacy and current CivitAI tRPC response wrappers."""
    if not isinstance(data, dict):
        raise ValueError(f"Expected tRPC response object, got {type(data).__name__}")

    result = data.get('result')
    if not isinstance(result, dict):
        raise ValueError("tRPC response is missing a result object")

    payload = result.get('data')
    if isinstance(payload, str):
        payload = _decode_reference_table(json.loads(payload))
    elif isinstance(payload, dict) and 'json' in payload:
        payload = payload['json']

    return payload


class CivitAIClient:
    """
    Client for CivitAI API with cookie authentication.
    Uses browser cookies to access NSFW content.
    """

    def __init__(self, config: Dict[str, Any]):
        self.config = config
        self.api_base = config['api']['api_base_url']
        self.trpc_url = config['api']['trpc_url']
        self.page_size = config['api']['page_size']
        self.max_pages_per_collection = config['api'].get('max_pages_per_collection', 0)

        # API key: env var wins (set by Diffusion Toolkit at launch), then
        # config.yaml. Never log the value.
        self.api_key = (os.environ.get('CIVITAI_API_KEY')
                        or config['api'].get('api_key') or '').strip()
        # OAuth access token, supplied by Diffusion Toolkit which owns the sign-in
        # and refresh cycle. Short-lived (1h), so it is never persisted here.
        #
        # It is NOT interchangeable with the API key: measured 2026-08-04, OAuth
        # tokens authenticate every tRPC endpoint we use but are rejected with
        # 401 by the public REST v1 API, which accepts API keys only. Hence the
        # per-family preference in _auth_get rather than one shared credential.
        self.access_token = (os.environ.get('CIVITAI_ACCESS_TOKEN') or '').strip()
        # Memo of which credential works where: (family, credential) ->
        # True/False once Bearer has been observed to work/fail. Keyed by
        # credential too, since tRPC may accept the OAuth token while REST only
        # accepts the API key. Families: 'trpc', 'rest'.
        self._bearer_ok: Dict[Tuple[str, str], bool] = {}
        self._cookies_loaded = False

        # Setup session with retries
        self.session = self._create_session()

        if self.access_token:
            logger.info("OAuth access token configured for tRPC; "
                        f"REST uses {'the API key' if self.api_key else 'cookies'}")
        elif self.api_key:
            logger.info("API key configured; cookies will be used only as fallback")
        else:
            logger.info("No API key; using cookie authentication")
            # No credentials: load cookies eagerly, exactly like the original client.
            self._ensure_cookies()

    def auth_mechanism(self, family: str = 'trpc') -> str:
        """Which credential actually authenticated this family, for reporting."""
        for kind, _credential in self._credentials_for(family):
            if self._bearer_ok.get((family, kind)):
                return kind
        return "cookies"

    def _credentials_for(self, family: str) -> List[Tuple[str, str]]:
        """Credentials to try, in order, for an endpoint family.

        tRPC prefers the OAuth token and falls back to the API key; REST v1
        rejects OAuth tokens outright, so it only ever offers the API key.

        Families:
          'trpc'    - /api/trpc/*, reads and mutations. OAuth first.
          'rest'    - /api/v1/*. API key only; OAuth is rejected there.
          'session' - NextAuth /api/auth/session. Kept apart from 'rest' so
                      trying OAuth here costs nothing when /api/v1 has already
                      memoized a rejection.
          'upload'  - /api/v1/image-upload. Nominally REST, but it is the
                      website's own upload handshake rather than the public
                      API, so whether it honours a Bearer token is unmeasured;
                      it gets its own family to try OAuth once and memoize.
        """
        if family in ('trpc', 'session', 'upload') and self.access_token:
            return [('OAuth token', self.access_token), ('API key', self.api_key)]
        return [('API key', self.api_key)]

    def _create_session(self) -> requests.Session:
        """Create a session with retry logic."""
        session = requests.Session()

        # Configure retries
        retry_strategy = Retry(
            total=3,
            backoff_factor=1,
            status_forcelist=[429, 500, 502, 503, 504],
            allowed_methods=["HEAD", "GET", "OPTIONS"]
        )

        adapter = HTTPAdapter(max_retries=retry_strategy, pool_maxsize=20)
        session.mount("http://", adapter)
        session.mount("https://", adapter)

        # Set headers.
        # IMPORTANT: civitai.red enforces an origin check on authenticated tRPC
        # endpoints - requests without a matching Referer/Origin are rejected with
        # 401 even when the session cookies are valid. The browser always sends
        # these, so the site works there but the script previously did not. Send
        # them so authenticated collection/image queries succeed.
        host = self._api_host()
        session.headers.update({
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
            'Accept': 'application/json',
            'Accept-Language': 'en-US,en;q=0.9',
            'Referer': f'https://{host}/',
            'Origin': f'https://{host}',
        })

        return session

    def _api_host(self) -> str:
        """The host the API actually talks to (e.g. 'civitai.red'), derived from config."""
        from urllib.parse import urlparse
        host = urlparse(self.api_base).hostname or 'civitai.red'
        return host

    def _find_cookie_file(self):
        """Locate a cookies.txt file, preferring one that matches the API host.

        The Get cookies.txt extension names the export after the site's domain
        (e.g. civitai.red_cookies.txt or civitai.com_cookies.txt). Accept either,
        and any other civitai*_cookies.txt the user may have dropped in.
        """
        from pathlib import Path
        script_dir = Path(__file__).parent
        host = self._api_host()

        # Preference order: exact host match, then the other known site, then any match.
        preferred = [
            script_dir / f'{host}_cookies.txt',
            script_dir / 'civitai.red_cookies.txt',
            script_dir / 'civitai.com_cookies.txt',
        ]
        for candidate in preferred:
            if candidate.exists():
                return candidate

        matches = sorted(script_dir.glob('civitai*_cookies.txt'))
        return matches[0] if matches else None

    def _ensure_cookies(self):
        """Load cookies once, on first need.

        Lazy on purpose: when an API key is configured, the first Bearer
        attempt must go out cookie-free, otherwise a success could actually be
        cookie-authenticated and we would wrongly memoize "Bearer works".
        """
        if not self._cookies_loaded:
            self._cookies_loaded = True
            self._load_cookies()

    def _auth_request(self, method: str, url: str, family: str = 'trpc',
                      **kwargs) -> requests.Response:
        """Request with Bearer auth when available, falling back to cookies on 401/403.

        The single decision point for authentication. `family` groups endpoints
        (see _credentials_for) so a rejection is memoized per family and costs
        at most one wasted request per run.

        The body is re-sent on each attempt, so callers must pass a re-usable
        body (`json=`, `data=<bytes>`) and never a file object or generator -
        a consumed stream would silently upload nothing on the retry.
        """
        headers = dict(kwargs.pop('headers', {}) or {})
        for kind, credential in self._credentials_for(family):
            if not credential or self._bearer_ok.get((family, kind)) is False:
                continue
            attempt = dict(headers)
            attempt['Authorization'] = f'Bearer {credential}'
            response = self.session.request(method, url, headers=attempt, **kwargs)
            if response.status_code not in (401, 403):
                self._bearer_ok[(family, kind)] = True
                return response
            logger.info(f"{kind} not accepted for {family} endpoint "
                        f"(HTTP {response.status_code}); trying the next credential")
            self._bearer_ok[(family, kind)] = False
        self._ensure_cookies()
        return self.session.request(method, url, headers=headers, **kwargs)

    def _auth_get(self, url: str, family: str = 'trpc', **kwargs) -> requests.Response:
        """GET with Bearer auth when available, falling back to cookies."""
        return self._auth_request('GET', url, family, **kwargs)

    def _auth_post(self, url: str, family: str = 'trpc', **kwargs) -> requests.Response:
        """POST with Bearer auth when available, falling back to cookies.

        Write mutations were cookie-only until now. post.create over OAuth with
        the Media & Posts Write scope was verified working on 2026-08-04, so
        mutations go through the same credential ladder as reads and no longer
        need an exported cookie file.
        """
        return self._auth_request('POST', url, family, **kwargs)

    def _load_cookies(self):
        """Load cookies from file or Chrome browser."""
        from http.cookiejar import Cookie

        api_host = self._api_host()

        def add_cookie(domain, path, secure, expiration, name, value):
            """Register a cookie, and mirror it onto the API host if needed.

            requests only sends a cookie when its domain matches the request host.
            Cookies exported from civitai.com are scoped to .civitai.com and would
            never be sent to civitai.red, so we also register a copy on the API host.
            """
            try:
                expires = int(expiration) if expiration not in ('0', '') else None
            except ValueError:
                expires = None

            domains = [domain]
            # Mirror onto the API host when the source domain wouldn't cover it.
            bare = domain.lstrip('.')
            if bare != api_host and not api_host.endswith('.' + bare):
                domains.append('.' + api_host)

            for d in domains:
                cookie = Cookie(
                    version=0,
                    name=name,
                    value=value,
                    port=None,
                    port_specified=False,
                    domain=d,
                    domain_specified=True,
                    domain_initial_dot=d.startswith('.'),
                    path=path or '/',
                    path_specified=True,
                    secure=secure == 'TRUE',
                    expires=expires,
                    discard=False,
                    comment=None,
                    comment_url=None,
                    rest={},
                    rfc2109=False
                )
                self.session.cookies.set_cookie(cookie)

        # First, try to load from a cookies.txt file
        cookie_file = self._find_cookie_file()

        if cookie_file is not None:
            try:
                logger.info(f"Loading cookies from file: {cookie_file.name}")
                cookie_count = 0

                with open(cookie_file, 'r', encoding='utf-8') as f:
                    for line in f:
                        line = line.rstrip('\n').rstrip('\r')
                        if not line.strip():
                            continue

                        # The Netscape format marks HttpOnly cookies with a
                        # "#HttpOnly_" prefix on the domain. Keep those (the auth
                        # token is often HttpOnly); skip only real comment lines.
                        if line.startswith('#'):
                            if line.startswith('#HttpOnly_'):
                                line = line[len('#HttpOnly_'):]
                            else:
                                continue

                        # Format: domain, flag, path, secure, expiration, name, value
                        parts = line.split('\t')
                        if len(parts) != 7:
                            continue

                        domain, flag, path, secure, expiration, name, value = parts
                        add_cookie(domain, path, secure, expiration, name, value)
                        cookie_count += 1

                if cookie_count > 0:
                    logger.info(f"Loaded {cookie_count} cookies from file (host: {api_host})")
                    return
                else:
                    logger.warning("Cookie file exists but no valid cookies found")

            except Exception as e:
                logger.error(f"Failed to load cookies from file: {e}")
                logger.info("Falling back to browser cookie extraction...")

        # Fallback: try to load from Chrome browser
        try:
            logger.info(f"Loading cookies from Chrome (domain: {api_host})...")
            cookies = browser_cookie3.chrome(domain_name=api_host)

            cookie_count = 0
            for cookie in cookies:
                self.session.cookies.set_cookie(cookie)
                cookie_count += 1

            if cookie_count > 0:
                logger.info(f"Loaded {cookie_count} cookies from Chrome")
            else:
                logger.warning("No cookies found! You may not be able to access NSFW content.")
                logger.warning(f"Make sure you're logged into {api_host} in Chrome,")
                logger.warning(f"or export cookies from {api_host} into '{api_host}_cookies.txt'.")

        except Exception as e:
            logger.error(f"Failed to load cookies: {e}")
            logger.warning("Continuing without authentication - NSFW content may not be accessible")

    def test_api_key(self) -> Optional[str]:
        """Validate the configured API key against REST /v1/users/me.

        This endpoint expects a Bearer token (it 401s for cookie sessions), so
        it validates the key itself independently of tRPC acceptance.
        Returns the username on success, None on failure or when no key is set.
        """
        if not self.api_key:
            return None
        # Validate at most once per run (cmd_test_auth and test_authentication
        # may both call this).
        if hasattr(self, '_api_key_user'):
            return self._api_key_user
        self._api_key_user = None
        try:
            response = self.session.get(
                f"{self.api_base}/v1/users/me",
                headers={'Authorization': f'Bearer {self.api_key}'},
                timeout=10,
            )
            if response.status_code == 200:
                data = response.json()
                username = data.get('username') or data.get('user', {}).get('username')
                logger.info(f"API key valid (user: {username})")
                self._api_key_user = username or 'unknown'
                return self._api_key_user
            logger.warning(f"/v1/users/me rejected the API key (HTTP {response.status_code}) - "
                           "on granular-scope keys this endpoint needs the Profile Read "
                           "scope; the key may still work elsewhere")
            return None
        except Exception as e:
            logger.error(f"API key validation failed: {e}")
            return None

    def test_authentication(self) -> bool:
        """Test the authentication path the scraper actually uses.

        First validates the API key (if configured) against REST /v1/users/me,
        then exercises the tRPC endpoint the scraper relies on via _auth_get,
        which includes the Bearer-then-cookies fallback. Returns True if the
        tRPC path works by either mechanism.
        """
        self.test_api_key()
        try:
            trpc_input = {"json": {"limit": 1, "sort": "Newest"}}
            params = {"input": json.dumps(trpc_input, separators=(',', ':'))}
            response = self._auth_get(
                f"{self.trpc_url}/collection.getAllUser",
                family='trpc',
                params=params,
                timeout=10,
            )
            if response.status_code == 200:
                logger.info(f"Authenticated: tRPC API accepted {self.auth_mechanism('trpc')}")
                return True
            elif response.status_code == 401:
                logger.warning("Not authenticated (401) - cookies missing/expired, "
                               "or Referer/Origin not accepted")
                return False
            else:
                logger.warning(f"Authentication check returned status {response.status_code}")
                return False
        except Exception as e:
            logger.error(f"Authentication test failed: {e}")
            return False

    def get_collection_images(self, collection_id: int, collection_name: str) -> List[ImageItem]:
        """
        Fetch all images from a collection using the tRPC API.
        This is the same endpoint the website uses, so it supports NSFW content.
        """
        if self.max_pages_per_collection > 0:
            logger.info(f"Fetching images from collection: {collection_name} ({collection_id}) [limited to {self.max_pages_per_collection} pages]")
        else:
            logger.info(f"Fetching images from collection: {collection_name} ({collection_id})")

        all_images = []
        seen_ids = set()  # Track unique image IDs to avoid duplicates
        cursor = None
        page = 1

        while True:
            images, next_cursor = self._fetch_collection_page(collection_id, collection_name, cursor)

            if not images:
                break

            # Deduplicate: only add images we haven't seen before
            new_images = []
            for img in images:
                if img.id not in seen_ids:
                    seen_ids.add(img.id)
                    new_images.append(img)

            all_images.extend(new_images)
            logger.info(f"  Page {page}: Found {len(images)} images ({len(new_images)} unique, {len(images) - len(new_images)} duplicates) (total unique: {len(all_images)})")

            # Check if we've reached the page limit (if set)
            if self.max_pages_per_collection > 0 and page >= self.max_pages_per_collection:
                logger.info(f"  Reached page limit ({self.max_pages_per_collection} pages), stopping fetch")
                break

            if not next_cursor:
                break

            cursor = next_cursor
            page += 1

            # Small delay to be nice to the server
            time.sleep(0.5)

        logger.info(f"Completed fetching {len(all_images)} unique images from {collection_name}")
        return all_images

    def _fetch_collection_page(
        self,
        collection_id: int,
        collection_name: str,
        cursor: Optional[str] = None
    ) -> tuple[List[ImageItem], Optional[str]]:
        """Fetch a single page of collection images."""
        try:
            # Build tRPC query exactly as the browser does
            input_data = {
                "collectionId": collection_id,
                "period": "AllTime",
                "sort": "Newest",
                "browsingLevel": 31,  # 31 = All levels (1+2+4+8+16 = SFW+Soft+Mature+X+XXX)
                "include": ["cosmetics"],
                "excludedTagIds": [415792, 426772, 5188, 5249, 130818, 130820, 133182],
                "disablePoi": True,
                "disableMinor": False,
                "authed": True
            }

            # Add cursor if we have one (it's a number, not a string!)
            if cursor is not None:
                input_data["cursor"] = cursor

            # tRPC format - just wrap in json
            trpc_input = {
                "json": input_data
            }

            params = {
                "input": json.dumps(trpc_input, separators=(',', ':'))
            }

            # CORRECT ENDPOINT: image.getInfinite, not collection.getInfinite!
            url = f"{self.trpc_url}/image.getInfinite"

            # DEBUG: Log what we're actually requesting
            logger.debug(f"Requesting collection ID: {collection_id}")
            logger.debug(f"Full URL: {url}?input={params['input'][:200]}...")

            response = self._auth_get(url, family='trpc', params=params, timeout=30)
            response.raise_for_status()

            payload = _extract_trpc_json(response.json())
            if not isinstance(payload, dict):
                raise ValueError(
                    f"Expected image page object, got {type(payload).__name__}"
                )

            items_data = payload.get('items', [])
            next_cursor = payload.get('nextCursor')
            if not isinstance(items_data, list):
                raise ValueError(
                    f"Expected image items list, got {type(items_data).__name__}"
                )

            # DEBUG: Log what we got back
            logger.debug(f"API returned {len(items_data)} items for collection {collection_id}")
            if items_data:
                first_item = items_data[0]
                if isinstance(first_item, dict):
                    logger.debug(
                        f"First item type: {first_item.get('type')}, "
                        f"has {len(first_item.get('images', []))} images, "
                        f"{len(first_item.get('srcs', []))} srcs"
                    )

            images = []
            # With image.getInfinite, items_data is directly an array of images!
            for i, img in enumerate(items_data):
                if not isinstance(img, dict):
                    logger.error(
                        f"Error parsing image {i}: expected object, "
                        f"got {type(img).__name__}"
                    )
                    continue

                # Debug first image
                if i == 0:
                    logger.debug(f"First image has keys: {list(img.keys())}")

                try:
                    images.append(ImageItem(
                        id=img['id'],
                        name=img.get('name') or img.get('url', f'image_{img["id"]}'),
                        url=img['url'],
                        collection_id=collection_id,
                        collection_name=collection_name,
                        nsfw=img.get('nsfw') or img.get('nsfwLevel'),
                        username=img.get('user', {}).get('username') if isinstance(img.get('user'), dict) else None
                    ))
                except (KeyError, TypeError) as e:
                    logger.error(f"Error parsing image {i}: {e}")
                    logger.error(f"Available fields: {list(img.keys()) if isinstance(img, dict) else 'not a dict'}")
                    # Continue to next image instead of stopping
                    continue

            return images, next_cursor

        except requests.RequestException as e:
            logger.error(f"Failed to fetch collection page: {e}")
            return [], None
        except (KeyError, TypeError, ValueError, json.JSONDecodeError) as e:
            logger.error(f"Failed to parse API response: {e}")
            return [], None

    def get_collection_info(self, collection_id: int) -> Optional[Collection]:
        """Get basic information about a collection."""
        try:
            # First, try to get collection metadata
            url = f"{self.api_base}/v1/collections/{collection_id}"
            response = self._auth_get(url, family='rest', timeout=10)

            if response.status_code == 200:
                data = response.json()
                return Collection(
                    id=collection_id,
                    name=data.get('name', f'Collection {collection_id}'),
                    image_count=data.get('imageCount')
                )
            else:
                # If API call fails, just return basic info
                return Collection(id=collection_id, name=f'Collection {collection_id}')

        except Exception as e:
            logger.warning(f"Could not fetch collection info: {e}")
            return Collection(id=collection_id, name=f'Collection {collection_id}')

    def get_user_collections(self) -> List[Collection]:
        """
        Fetch the authenticated user's collections using the tRPC API.
        Requires valid cookie-based authentication.
        Returns a list of Collection objects, or raises an exception on auth failure.
        """
        # Use the tRPC endpoint (same as the website uses with cookies)
        collections = []
        cursor = None

        while True:
            input_data = {
                "limit": 100,
                "sort": "Newest",
            }
            if cursor is not None:
                input_data["cursor"] = cursor

            trpc_input = {"json": input_data}
            params = {
                "input": json.dumps(trpc_input, separators=(',', ':'))
            }

            url = f"{self.trpc_url}/collection.getAllUser"
            resp = self._auth_get(url, family='trpc', params=params, timeout=15)

            if resp.status_code == 401:
                raise PermissionError("Authentication failed (401). Set a CivitAI API key "
                                      "(Settings > CivitAI in Diffusion Toolkit) or re-export your cookies.")

            if resp.status_code != 200:
                raise ConnectionError(f"Failed to fetch collections (HTTP {resp.status_code})")

            data = resp.json()
            result_json = data.get('result', {}).get('data', {}).get('json', {})

            # Debug: log the response structure on first page
            if cursor is None:
                logger.debug(f"collection.getAllUser response type: {type(result_json)}")
                if isinstance(result_json, list) and result_json:
                    logger.debug(f"First item keys: {list(result_json[0].keys()) if isinstance(result_json[0], dict) else type(result_json[0])}")
                elif isinstance(result_json, dict):
                    logger.debug(f"Response keys: {list(result_json.keys())}")

            # tRPC response may be a list directly or a dict with 'items' key
            if isinstance(result_json, list):
                items = result_json
                next_cursor = None
            elif isinstance(result_json, dict):
                items = result_json.get('items', [])
                next_cursor = result_json.get('nextCursor')
            else:
                logger.warning(f"Unexpected response type: {type(result_json)}")
                break

            if not items:
                break

            for item in items:
                # item could be a dict directly or nested under a key
                if not isinstance(item, dict):
                    continue
                coll_id = item.get('id')
                if coll_id is None:
                    continue
                collections.append(Collection(
                    id=coll_id,
                    name=item.get('name', f'Collection {coll_id}'),
                    image_count=item.get('image', {}).get('count') if isinstance(item.get('image'), dict)
                        else item.get('metadata', {}).get('imageCount') if isinstance(item.get('metadata'), dict)
                        else item.get('imageCount')
                ))

            if not next_cursor:
                break
            cursor = next_cursor
            time.sleep(0.3)

        logger.info(f"Found {len(collections)} user collections")
        return collections

    def download_image(self, image: ImageItem, output_path: Path, original_extension: str = '') -> bool:
        """
        Download an image file.

        Args:
            image: ImageItem to download
            output_path: Where to save the file (without extension)
            original_extension: Original file extension from CivitAI (with dot, e.g., '.jpg')

        Returns:
            True if successful, False otherwise
        """
        try:
            # Build the image URL (same as original script)
            image_base = self.config['api']['image_base_url']

            # Clean the URL part
            url_part = image.url.split('?')[0].strip('/')

            # Use original name for the URL
            url_filename = image.name

            download_url = f"{image_base}{url_part}/original=true,quality=90//{url_filename}"

            logger.debug(f"Downloading from: {download_url}")

            # Stream the download. Deliberately NOT via _auth_get: never send
            # the Bearer token to the CDN host. Cookies keep NSFW originals
            # working exactly as before.
            self._ensure_cookies()
            response = self.session.get(download_url, stream=True, timeout=60)
            response.raise_for_status()

            # V02 BEHAVIOR: Use original extension if available, only detect if missing
            extension = original_extension

            # Only detect if no extension provided (v02 compatibility - line 339)
            if not extension:
                # No extension from original filename - need to detect from content
                first_chunk = next(response.iter_content(chunk_size=1024), None)
                if not first_chunk:
                    logger.error(f"Empty response for image {image.id}")
                    return False

                extension = self._detect_extension_from_content(first_chunk)
                logger.debug(f"No original extension, detected: {extension}")

                # Save with detected extension
                # CRITICAL: Don't use .with_suffix() - it breaks filenames with dots!
                final_path = Path(str(output_path) + extension)
                with open(final_path, 'wb') as f:
                    f.write(first_chunk)
                    # Write rest of content
                    for chunk in response.iter_content(chunk_size=8192):
                        f.write(chunk)
            else:
                # Use original extension from CivitAI (v02 behavior - line 322, 348-351)
                logger.debug(f"Using original extension: {extension}")
                # CRITICAL: Don't use .with_suffix() - use string concatenation!
                final_path = Path(str(output_path) + extension)
                with open(final_path, 'wb') as f:
                    for chunk in response.iter_content(chunk_size=8192):
                        f.write(chunk)

            return True

        except requests.RequestException as e:
            logger.error(f"Failed to download image {image.id}: {e}")
            return False
        except Exception as e:
            logger.error(f"Unexpected error downloading {image.id}: {e}")
            return False

    def _get_extension_from_content_type(self, content_type: str) -> Optional[str]:
        """Map content-type to file extension."""
        type_map = {
            'image/jpeg': '.jpg',
            'image/jpg': '.jpg',
            'image/png': '.png',
            'image/webp': '.webp',
            'image/gif': '.gif',
            'image/bmp': '.bmp',
            'image/tiff': '.tiff'
        }
        return type_map.get(content_type)

    def _detect_extension_from_content(self, content: bytes) -> str:
        """Detect file type from content magic bytes."""
        # Check magic bytes
        if content.startswith(b'\xFF\xD8\xFF'):
            return '.jpg'
        elif content.startswith(b'\x89PNG\r\n\x1a\n'):
            return '.png'
        elif content.startswith(b'RIFF') and b'WEBP' in content[:12]:
            return '.webp'
        elif content.startswith(b'GIF87a') or content.startswith(b'GIF89a'):
            return '.gif'
        elif content.startswith(b'BM'):
            return '.bmp'
        else:
            # Default to jpg
            return '.jpg'
