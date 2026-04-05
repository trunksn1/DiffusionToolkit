"""
CivitAI API client with cookie-based authentication.
Extracts cookies from Chrome to access NSFW content without paid membership.
"""

import json
import logging
from typing import List, Optional, Dict, Any
from pathlib import Path
import time

import requests
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry
import browser_cookie3

from models import ImageItem, Collection

logger = logging.getLogger(__name__)


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

        # Setup session with retries
        self.session = self._create_session()

        # Load cookies from browser
        self._load_cookies()

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

        # Set headers
        session.headers.update({
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
            'Accept': 'application/json',
            'Accept-Language': 'en-US,en;q=0.9',
        })

        return session

    def _load_cookies(self):
        """Load cookies from file or Chrome browser."""
        from pathlib import Path
        from http.cookiejar import Cookie

        # First, try to load from cookies.txt file
        cookie_file = Path(__file__).parent / 'civitai.com_cookies.txt'

        if cookie_file.exists():
            try:
                logger.info(f"Loading cookies from file: {cookie_file.name}")
                cookie_count = 0

                with open(cookie_file, 'r', encoding='utf-8') as f:
                    for line in f:
                        line = line.strip()
                        # Skip comments and empty lines
                        if not line or line.startswith('#'):
                            continue

                        # Parse Netscape cookie format
                        # Format: domain, flag, path, secure, expiration, name, value
                        parts = line.split('\t')
                        if len(parts) != 7:
                            continue

                        domain, flag, path, secure, expiration, name, value = parts

                        # Create cookie
                        cookie = Cookie(
                            version=0,
                            name=name,
                            value=value,
                            port=None,
                            port_specified=False,
                            domain=domain,
                            domain_specified=True,
                            domain_initial_dot=domain.startswith('.'),
                            path=path,
                            path_specified=True,
                            secure=secure == 'TRUE',
                            expires=int(expiration) if expiration != '0' else None,
                            discard=False,
                            comment=None,
                            comment_url=None,
                            rest={},
                            rfc2109=False
                        )

                        self.session.cookies.set_cookie(cookie)
                        cookie_count += 1

                if cookie_count > 0:
                    logger.info(f"Loaded {cookie_count} cookies from file")
                    return
                else:
                    logger.warning("Cookie file exists but no valid cookies found")

            except Exception as e:
                logger.error(f"Failed to load cookies from file: {e}")
                logger.info("Falling back to browser cookie extraction...")

        # Fallback: try to load from Chrome browser
        try:
            logger.info("Loading cookies from Chrome...")
            cookies = browser_cookie3.chrome(domain_name='civitai.com')

            cookie_count = 0
            for cookie in cookies:
                self.session.cookies.set_cookie(cookie)
                cookie_count += 1

            if cookie_count > 0:
                logger.info(f"Loaded {cookie_count} cookies from Chrome")
            else:
                logger.warning("No cookies found! You may not be able to access NSFW content.")
                logger.warning("Make sure you're logged into CivitAI in Chrome.")

        except Exception as e:
            logger.error(f"Failed to load cookies: {e}")
            logger.warning("Continuing without authentication - NSFW content may not be accessible")

    def test_authentication(self) -> bool:
        """Test if we're authenticated by checking user session."""
        try:
            response = self.session.get(f"{self.api_base}/v1/users/me", timeout=10)
            if response.status_code == 200:
                user_data = response.json()
                logger.info(f"Authenticated as: {user_data.get('username', 'Unknown')}")
                return True
            elif response.status_code == 401:
                logger.warning("Not authenticated - some content may be inaccessible")
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

            response = self.session.get(url, params=params, timeout=30)
            response.raise_for_status()

            data = response.json()

            # Parse the tRPC response structure
            items_data = data.get('result', {}).get('data', {}).get('json', {}).get('items', [])
            next_cursor = data.get('result', {}).get('data', {}).get('json', {}).get('nextCursor')

            # DEBUG: Log what we got back
            logger.debug(f"API returned {len(items_data)} items for collection {collection_id}")
            if items_data:
                first_item = items_data[0]
                logger.debug(f"First item type: {first_item.get('type')}, has {len(first_item.get('images', []))} images, {len(first_item.get('srcs', []))} srcs")

            images = []
            # With image.getInfinite, items_data is directly an array of images!
            for i, img in enumerate(items_data):
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
        except (KeyError, json.JSONDecodeError) as e:
            logger.error(f"Failed to parse API response: {e}")
            return [], None

    def get_collection_info(self, collection_id: int) -> Optional[Collection]:
        """Get basic information about a collection."""
        try:
            # First, try to get collection metadata
            url = f"{self.api_base}/v1/collections/{collection_id}"
            response = self.session.get(url, timeout=10)

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

            # Stream the download
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
