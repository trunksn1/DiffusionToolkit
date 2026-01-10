"""
Storage management with deduplication support.
Integrates with DiffusionToolkit database and state tracking.
"""

import os
import re
import sqlite3
import hashlib
import logging
import unicodedata
from pathlib import Path
from typing import Optional, Tuple, Dict, Any

from models import ImageItem

logger = logging.getLogger(__name__)


class StorageManager:
    """Manages file storage with deduplication."""

    def __init__(self, config: Dict[str, Any], state_db):
        self.config = config
        self.state_db = state_db
        self.base_path = Path(config['paths']['downloads'])

        # Support both old 'additional_check' and new 'check_paths' config
        if 'check_paths' in config['paths']:
            # New format: list of paths to check
            self.check_paths = [Path(p) for p in config['paths']['check_paths']]
        else:
            # Legacy format: fallback to additional_check
            self.check_paths = [Path(config['paths'].get('additional_check', config['paths']['downloads']))]

        self.diffusion_toolkit_db = config['paths'].get('diffusion_toolkit_db')
        self.supported_extensions = config['storage']['supported_extensions']
        self.organize_by_collection = config['storage']['organize_by_collection']

        # OPTIMIZATION: Cache all downloaded IDs in memory for instant lookups
        # Instead of 7,580 separate DB queries, do 1 query + 7,580 in-memory checks
        logger.info("Loading downloaded IDs into memory...")
        self.downloaded_ids = state_db.get_downloaded_ids()
        logger.info(f"Cached {len(self.downloaded_ids)} downloaded IDs")

        # OPTIMIZATION: Cache existing folder mappings (for Unicode encoding compatibility)
        # Maps normalized folder names to actual folder paths (e.g., "josè" → "JosÃ¨" on disk)
        logger.info("Scanning existing collection folders...")
        self.folder_cache = {}
        for check_path in self.check_paths:
            if check_path.exists():
                for existing_folder in check_path.iterdir():
                    if existing_folder.is_dir():
                        normalized_name = unicodedata.normalize('NFC', existing_folder.name.lower())
                        # Store mapping: normalized name → actual path
                        self.folder_cache[normalized_name] = existing_folder
        logger.info(f"Cached {len(self.folder_cache)} existing folders")

        # OPTIMIZATION: Pre-scan filesystem for existing files
        # Build a dict mapping (check_path, collection_name, base_filename) -> extension
        logger.info("Pre-scanning filesystem for existing files...")
        self.filesystem_cache = {}
        for check_path in self.check_paths:
            if check_path.exists():
                if self.organize_by_collection:
                    # Scan all collection subdirectories
                    for collection_dir in check_path.iterdir():
                        if collection_dir.is_dir():
                            collection_name = collection_dir.name.lower()
                            for file_path in collection_dir.iterdir():
                                if file_path.is_file():
                                    # Store base filename -> extension mapping
                                    base_name = file_path.stem
                                    extension = file_path.suffix
                                    # Store the first extension found (if multiple exist, any is fine)
                                    key = (str(check_path), collection_name, base_name)
                                    if key not in self.filesystem_cache:
                                        self.filesystem_cache[key] = extension
                else:
                    # Files directly in check_path (no collection subdirectories)
                    for file_path in check_path.iterdir():
                        if file_path.is_file():
                            base_name = file_path.stem
                            extension = file_path.suffix
                            # Use empty string for collection_name when not organized by collection
                            key = (str(check_path), "", base_name)
                            if key not in self.filesystem_cache:
                                self.filesystem_cache[key] = extension
        logger.info(f"Cached {len(self.filesystem_cache)} existing files from filesystem")

        # OPTIMIZATION: Pre-load DiffusionToolkit DB paths into memory
        self.diffusion_db_cache = set()
        if self.diffusion_toolkit_db and os.path.exists(self.diffusion_toolkit_db):
            logger.info("Loading DiffusionToolkit database into memory...")
            try:
                conn = sqlite3.connect(self.diffusion_toolkit_db)
                cursor = conn.cursor()
                cursor.execute("SELECT path FROM image")

                for row in cursor.fetchall():
                    # Normalize path for comparison
                    path = row[0]
                    unicode_normalized = unicodedata.normalize('NFC', path)
                    normalized_path = unicode_normalized.replace('\\', '/').lower()
                    self.diffusion_db_cache.add(normalized_path)

                conn.close()
                logger.info(f"Cached {len(self.diffusion_db_cache)} paths from DiffusionToolkit DB")
            except Exception as e:
                logger.warning(f"Failed to load DiffusionToolkit DB into memory: {e}")
        else:
            logger.info("DiffusionToolkit DB not configured or not found, skipping DB cache")

        # Create base directory
        self.base_path.mkdir(parents=True, exist_ok=True)

    def should_download(self, image: ImageItem) -> Tuple[bool, Optional[str]]:
        """
        Check if an image should be downloaded.

        Returns:
            Tuple of (should_download: bool, reason: str or None)
            reason is only set if should_download is False
        """
        # Check cached downloaded IDs first (instant O(1) lookup in Set)
        if image.id in self.downloaded_ids:
            logger.debug(f"Image {image.id} found in cached downloaded IDs")
            return False, "already downloaded (state DB)"

        # Get the unique filename (without path)
        unique_filename = self._get_unique_filename(image)

        # DEBUG: Show what we're checking
        logger.debug(f"Checking image {image.id}: {image.name}")

        # Check all configured paths for this file
        for check_path in self.check_paths:
            # Build the full path in this check location
            if self.organize_by_collection:
                # Sanitize will normalize Unicode (Josè → Josè, not JosÃ¨)
                sanitized_collection = self._sanitize_filename(image.collection_name)

                # Check the correct normalized path first
                full_base_path = check_path / sanitized_collection / unique_filename
                logger.debug(f"  Checking in: {full_base_path}")
                exists, found_ext, location = self._check_file_exists_any_extension(full_base_path)
                if exists:
                    logger.debug(f"  Found as: {full_base_path.name}{found_ext} in {location}")
                    return False, f"found in {check_path} as {full_base_path.name}{found_ext}"

                # COMPATIBILITY: Check cached folder mappings (handles "JosÃ¨" vs "Josè" encoding issues)
                target_normalized = unicodedata.normalize('NFC', sanitized_collection.lower())
                if target_normalized in self.folder_cache:
                    actual_folder = self.folder_cache[target_normalized]
                    alt_path = actual_folder / unique_filename
                    logger.debug(f"  Checking in cached folder match: {alt_path}")
                    exists, found_ext, location = self._check_file_exists_any_extension(alt_path)
                    if exists:
                        logger.debug(f"  Found in cached folder: {alt_path.name}{found_ext}")
                        return False, f"found in {actual_folder} as {alt_path.name}{found_ext}"
            else:
                full_base_path = check_path / unique_filename
                logger.debug(f"  Checking in: {full_base_path}")

                # Check if file exists with any extension
                exists, found_ext, location = self._check_file_exists_any_extension(full_base_path)
                if exists:
                    logger.debug(f"  Found as: {full_base_path.name}{found_ext} in {location}")
                    return False, f"found in {check_path} as {full_base_path.name}{found_ext}"

        # All checks passed - should download
        logger.debug(f"  NOT FOUND - would download")
        return True, None

    def get_save_path(self, image: ImageItem, extension: str = '') -> Path:
        """
        Get the path where an image should be saved.

        Args:
            image: ImageItem to save
            extension: File extension (with dot), if known

        Returns:
            Path object for saving the file
        """
        base_path = self._get_base_path_for_image(image)

        if extension:
            # Don't use .with_suffix() - it breaks filenames with dots!
            return Path(str(base_path) + extension)
        else:
            return base_path

    def _get_base_path_for_image(self, image: ImageItem) -> Path:
        """Get base path (without extension) for an image."""
        filename = self._get_unique_filename(image)

        if self.organize_by_collection:
            collection_dir = self.base_path / self._sanitize_filename(image.collection_name)
            collection_dir.mkdir(parents=True, exist_ok=True)
            return collection_dir / filename
        else:
            return self.base_path / filename

    def _get_unique_filename(self, image: ImageItem) -> str:
        """
        Generate a unique filename for an image (without extension).
        Returns: "original_name__CIV_ID__id" (extension added later)

        IMPORTANT: Matches v02 behavior EXACTLY for filename compatibility.
        """
        # CRITICAL: Match v02 exactly - try 'name' first, then 'url' as fallback
        # v02: raw_url = item.get('name') or item.get('url', '')
        raw_url = image.name or image.url or ''

        # Extract filename from raw_url (remove path and query params)
        if raw_url:
            clean_filename = raw_url.split('/')[-1].split('?')[0]
        else:
            clean_filename = f'image_{image.id}'

        # Sanitize the filename
        clean_name = self._sanitize_filename(clean_filename)

        # Extract extension from original filename (v02 compatibility)
        # We'll use this extension, not detect from content!
        base_name, original_extension = os.path.splitext(clean_name)

        # Truncate ID if too long (v02 compatibility)
        id_str = str(image.id)
        if len(id_str) > 40:
            id_str = id_str[:40]

        # Build unique name (without extension) - matches v02 format
        unique_name = f"{base_name}__CIV_ID__{id_str}"

        # Truncate if too long (v02 compatibility)
        if len(unique_name) > 200:
            unique_name = unique_name[:200]

        return unique_name

    def get_original_extension(self, image: ImageItem) -> str:
        """
        Get the original file extension from the image name.
        Matches v02 behavior - use the extension from CivitAI, not detected from content.

        Returns:
            Extension with dot (e.g., '.jpg') or empty string if no extension
        """
        # Same logic as _get_unique_filename to extract the original name
        raw_url = image.name or image.url or ''

        if raw_url:
            clean_filename = raw_url.split('/')[-1].split('?')[0]
        else:
            return ''  # No extension available

        # Sanitize the filename
        clean_name = self._sanitize_filename(clean_filename)

        # Extract extension
        _, original_extension = os.path.splitext(clean_name)

        return original_extension

    def _sanitize_filename(self, filename: str) -> str:
        """
        Remove invalid characters from filename and normalize Unicode.
        Fixes issues with accented characters like è being converted to Ã¨
        """
        # Normalize Unicode to NFC form (combines decomposed characters)
        # This fixes "Josè" vs "JosÃ¨" issues
        normalized = unicodedata.normalize('NFC', filename)

        # Remove invalid Windows filename characters
        sanitized = re.sub(r'[\\/:*?"<>|]', '_', normalized)

        return sanitized

    def _check_file_exists_any_extension(self, base_path: Path) -> Tuple[bool, Optional[str], str]:
        """
        Check if a file exists with any common extension.

        Returns:
            Tuple of (exists: bool, extension: str or None, location: str)
        """
        # OPTIMIZATION: Check filesystem cache first (instant O(1) lookup)
        if self.organize_by_collection:
            # Path structure: check_path / collection_name / filename
            check_path_str = str(base_path.parent.parent)
            collection_name = base_path.parent.name.lower()
        else:
            # Path structure: check_path / filename
            check_path_str = str(base_path.parent)
            collection_name = ""

        base_filename = base_path.name
        cache_key = (check_path_str, collection_name, base_filename)

        # Check if file exists in filesystem cache
        if cache_key in self.filesystem_cache:
            extension = self.filesystem_cache[cache_key]
            return True, extension, "filesystem (cached)"

        # OPTIMIZATION: Check DiffusionToolkit cache (instant O(1) lookup)
        if self.diffusion_db_cache:
            for ext in self.supported_extensions:
                full_path = Path(str(base_path) + ext)
                # Normalize path for comparison
                unicode_normalized = unicodedata.normalize('NFC', str(full_path))
                normalized_path = unicode_normalized.replace('\\', '/').lower()

                if normalized_path in self.diffusion_db_cache:
                    return True, ext, "DiffusionToolkit DB (cached)"

        return False, None, ""

    def _check_path_in_diffusion_db(self, file_path: str) -> bool:
        """Check if a path exists in DiffusionToolkit database."""
        if not self.diffusion_toolkit_db or not os.path.exists(self.diffusion_toolkit_db):
            return False

        try:
            # Normalize Unicode first (fixes "Josè" vs "JosÃ¨")
            unicode_normalized = unicodedata.normalize('NFC', file_path)

            # Normalize path for comparison
            normalized_path = unicode_normalized.replace('\\', '/').lower()

            conn = sqlite3.connect(self.diffusion_toolkit_db)
            cursor = conn.cursor()

            cursor.execute("""
                SELECT COUNT(*) FROM image
                WHERE LOWER(REPLACE(path, '\\', '/')) = ?
            """, (normalized_path,))

            result = cursor.fetchone()
            conn.close()

            return result[0] > 0

        except sqlite3.Error as e:
            logger.debug(f"DiffusionToolkit DB query error: {e}")
            return False
        except Exception as e:
            logger.debug(f"Unexpected error checking DiffusionToolkit DB: {e}")
            return False

    def calculate_file_hash(self, file_path: Path) -> Optional[str]:
        """Calculate SHA256 hash of a file."""
        try:
            sha256_hash = hashlib.sha256()
            with open(file_path, "rb") as f:
                for chunk in iter(lambda: f.read(8192), b""):
                    sha256_hash.update(chunk)
            return sha256_hash.hexdigest()
        except Exception as e:
            logger.error(f"Failed to calculate hash for {file_path}: {e}")
            return None

    def verify_file(self, file_path: Path) -> bool:
        """Verify that a file was downloaded correctly."""
        if not file_path.exists():
            return False

        # Check file size
        size = file_path.stat().st_size
        if size == 0:
            logger.error(f"Downloaded file is empty: {file_path}")
            return False

        if size < 100:  # Suspiciously small
            logger.warning(f"Downloaded file is very small ({size} bytes): {file_path}")

        return True

    def add_to_cache(self, civitai_id: int):
        """Add a newly downloaded image ID to the cache."""
        self.downloaded_ids.add(civitai_id)

    def cleanup_failed_download(self, file_path: Path):
        """Remove a failed/partial download."""
        try:
            if file_path.exists():
                file_path.unlink()
                logger.debug(f"Cleaned up failed download: {file_path}")
        except Exception as e:
            logger.error(f"Failed to cleanup {file_path}: {e}")

    def get_directory_stats(self, collection_name: Optional[str] = None) -> Dict[str, int]:
        """Get statistics about stored files."""
        if collection_name and self.organize_by_collection:
            search_path = self.base_path / self._sanitize_filename(collection_name)
        else:
            search_path = self.base_path

        if not search_path.exists():
            return {'total_files': 0, 'total_size': 0}

        total_files = 0
        total_size = 0

        for ext in self.supported_extensions:
            for file_path in search_path.rglob(f"*{ext}"):
                if file_path.is_file():
                    total_files += 1
                    total_size += file_path.stat().st_size

        return {
            'total_files': total_files,
            'total_size': total_size,
            'total_size_mb': round(total_size / (1024 * 1024), 2)
        }
