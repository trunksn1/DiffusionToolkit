"""
Main download orchestrator.
Coordinates API client, storage, and state management.
"""

import logging
import time
from pathlib import Path
from typing import List, Dict, Any, Optional
from concurrent.futures import ThreadPoolExecutor, as_completed
from threading import Lock

from models import ImageItem, DownloadStats
from api_client import CivitAIClient
from storage import StorageManager
from database import StateDatabase

logger = logging.getLogger(__name__)


class DownloadOrchestrator:
    """Coordinates the download process."""

    def __init__(self, config: Dict[str, Any]):
        self.config = config
        self.max_workers = config['download']['max_workers']
        self.delay = config['download']['delay_between_downloads']
        self.max_retries = config['download']['max_retries']

        # Initialize components
        self.state_db = StateDatabase(config['paths']['state_db'])
        self.storage = StorageManager(config, self.state_db)
        self.api_client = CivitAIClient(config)

        # Thread-safe counter for progress
        self._progress_lock = Lock()
        self._downloaded = 0
        self._skipped = 0
        self._failed = 0

    def test_authentication(self) -> bool:
        """Test if API authentication is working."""
        return self.api_client.test_authentication()

    def sync_collection(self, collection_id: int, collection_name: str) -> DownloadStats:
        """
        Synchronize a collection - download any new images.

        Args:
            collection_id: CivitAI collection ID
            collection_name: Name of the collection

        Returns:
            DownloadStats with results
        """
        logger.info(f"\n{'='*60}")
        logger.info(f"Syncing collection: {collection_name} (ID: {collection_id})")
        logger.info(f"{'='*60}\n")

        # Reset progress counters
        self._downloaded = 0
        self._skipped = 0
        self._failed = 0

        # Step 1: Fetch all images from API
        images = self.api_client.get_collection_images(collection_id, collection_name)

        if not images:
            logger.warning(f"No images found in collection {collection_name}")
            return DownloadStats()

        logger.info(f"Found {len(images)} images in collection")

        # Step 2: Filter out images we already have
        to_download = []
        for image in images:
            should_download, reason = self.storage.should_download(image)

            if should_download:
                to_download.append(image)
            else:
                self._skipped += 1
                # OPTIMIZATION: Don't waste time writing "skipped" to database
                # If we already know the file exists, no need to record that we're skipping it
                logger.debug(f"Skipping {image.name}: {reason}")

        logger.info(f"\nDownload plan:")
        logger.info(f"  Total images: {len(images)}")
        logger.info(f"  Already have: {self._skipped}")
        logger.info(f"  To download: {len(to_download)}")

        # OPTIMIZATION: Mark all pending images in a single batch transaction
        # Instead of 80 separate DB writes, do 1 batch write (10-100x faster!)
        if to_download:
            logger.debug(f"Marking {len(to_download)} images as pending in database...")
            self.state_db.mark_pending_batch(to_download)
            logger.debug("Batch database update complete")

        if not to_download:
            logger.info("\nNothing new to download!")
            return self._get_final_stats()

        # Step 3: Download images concurrently
        logger.info(f"\nStarting downloads with {self.max_workers} workers...\n")

        # OPTIMIZATION: Collect all results for batch database update
        completed_downloads = []  # List of (civitai_id, local_path, file_hash)
        failed_downloads = []     # List of (civitai_id, error_message)

        with ThreadPoolExecutor(max_workers=self.max_workers) as executor:
            # Submit all download tasks
            future_to_image = {
                executor.submit(self._download_single_image, image): image
                for image in to_download
            }

            # Process completed downloads and collect results
            for future in as_completed(future_to_image):
                image = future_to_image[future]
                try:
                    result = future.result()  # Get the result tuple
                    if result:
                        status = result[0]
                        if status == 'success':
                            _, civitai_id, local_path, file_hash = result
                            completed_downloads.append((civitai_id, local_path, file_hash))
                        elif status == 'failed':
                            _, civitai_id, error_msg = result
                            failed_downloads.append((civitai_id, error_msg))
                except Exception as e:
                    logger.error(f"Unexpected error downloading {image.name}: {e}")

        # OPTIMIZATION: Batch update database with all results
        if completed_downloads:
            logger.debug(f"Updating database: {len(completed_downloads)} completed downloads...")
            self.state_db.mark_completed_batch(completed_downloads)

        if failed_downloads:
            logger.debug(f"Updating database: {len(failed_downloads)} failed downloads...")
            self.state_db.mark_failed_batch(failed_downloads)

        # Return final statistics
        stats = self._get_final_stats()
        logger.info(f"\n{'='*60}")
        logger.info(f"Collection sync complete: {collection_name}")
        logger.info(f"{'='*60}")
        logger.info(f"{stats}\n")

        return stats

    def _download_single_image(self, image: ImageItem):
        """
        Download a single image (called by thread pool).
        Returns result tuple for batch database update.
        """
        save_path = None  # Initialize to avoid UnboundLocalError in exception handler
        try:
            # Get save path (without extension, we'll add it after detecting type)
            save_path = self.storage.get_save_path(image)

            # Get original extension from CivitAI (v02 compatibility)
            original_extension = self.storage.get_original_extension(image)

            # Download the image
            logger.info(f"Downloading: {image.name}")

            success = self.api_client.download_image(image, save_path, original_extension)

            if success:
                # Find the actual file (API client adds extension)
                actual_file = self._find_downloaded_file(save_path, original_extension)

                if actual_file and self.storage.verify_file(actual_file):
                    # Calculate hash
                    file_hash = self.storage.calculate_file_hash(actual_file)

                    # Add to cache so future checks are instant
                    self.storage.add_to_cache(image.id)

                    with self._progress_lock:
                        self._downloaded += 1

                    logger.info(f"[OK] Completed: {actual_file.name}")

                    # Delay between downloads to be nice to the server
                    if self.delay > 0:
                        time.sleep(self.delay)

                    # OPTIMIZATION: Return success info for batch DB update
                    return ('success', image.id, str(actual_file), file_hash)

                else:
                    raise Exception("File verification failed")

            else:
                raise Exception("Download failed")

        except Exception as e:
            error_msg = str(e)
            logger.error(f"[FAIL] Failed: {image.name} - {error_msg}")

            # Cleanup any partial download (only if save_path was determined)
            if save_path:
                self.storage.cleanup_failed_download(save_path)

            with self._progress_lock:
                self._failed += 1

            # OPTIMIZATION: Return failure info for batch DB update
            return ('failed', image.id, error_msg)

    def _find_downloaded_file(self, base_path: Path, original_extension: str = '') -> Path:
        """Find the downloaded file (API client adds extension)."""
        # Check original extension first (most likely)
        if original_extension:
            # Don't use .with_suffix() - it breaks filenames with dots!
            potential_path = Path(str(base_path) + original_extension)
            if potential_path.exists():
                return potential_path

        # Fallback: Check for common extensions
        for ext in ['.jpg', '.jpeg', '.png', '.webp', '.gif']:
            # Don't use .with_suffix() - it breaks filenames with dots!
            potential_path = Path(str(base_path) + ext)
            if potential_path.exists():
                return potential_path

        # If not found, return the base path
        return base_path

    def _get_final_stats(self) -> DownloadStats:
        """Get current download statistics."""
        stats = DownloadStats()
        stats.completed = self._downloaded
        stats.failed = self._failed
        stats.skipped = self._skipped
        stats.total = stats.completed + stats.failed + stats.skipped
        return stats

    def retry_failed(self, collection_id: Optional[int] = None) -> DownloadStats:
        """Retry failed downloads."""
        logger.info("Retrying failed downloads...")

        failed_items = self.state_db.get_failed_items(max_retries=self.max_retries)

        if collection_id:
            failed_items = [item for item in failed_items if item.collection_id == collection_id]

        if not failed_items:
            logger.info("No failed downloads to retry")
            return DownloadStats()

        logger.info(f"Found {len(failed_items)} failed downloads to retry")

        # Convert to ImageItems and download
        images_to_retry = []
        for record in failed_items:
            # We need to refetch the image metadata from API
            # For now, create a basic ImageItem
            image = ImageItem(
                id=record.civitai_id,
                name=f"image_{record.civitai_id}",
                url="",  # Will need to refetch if needed
                collection_id=record.collection_id,
                collection_name=record.collection_name
            )
            images_to_retry.append(image)

        # Reset counters
        self._downloaded = 0
        self._skipped = 0
        self._failed = 0

        # Download with thread pool
        with ThreadPoolExecutor(max_workers=self.max_workers) as executor:
            futures = [executor.submit(self._download_single_image, img) for img in images_to_retry]
            for future in as_completed(futures):
                try:
                    future.result()
                except Exception as e:
                    logger.error(f"Error during retry: {e}")

        return self._get_final_stats()

    def verify_downloads(self, collection_id: Optional[int] = None, fix: bool = False) -> Dict[str, Any]:
        """
        Verify that completed downloads still exist on disk.

        Args:
            collection_id: Optional collection to limit the check to.
            fix: If True, reset missing records to pending so sync re-downloads them.

        Returns:
            Dict with verification results.
        """
        logger.info("Verifying completed downloads...")

        completed = self.state_db.get_completed_items(collection_id)
        if not completed:
            logger.info("No completed downloads to verify")
            return {'total': 0, 'ok': 0, 'missing': 0, 'no_path': 0, 'missing_items': []}

        ok = 0
        missing = []
        no_path = 0

        for record in completed:
            if not record.local_path:
                no_path += 1
                continue

            if Path(record.local_path).exists():
                ok += 1
            else:
                missing.append(record)
                logger.warning(f"MISSING: {record.local_path} (civitai_id={record.civitai_id}, collection={record.collection_name})")

        if fix and missing:
            ids_to_reset = [r.civitai_id for r in missing]
            self.state_db.reset_by_ids(ids_to_reset)
            logger.info(f"Reset {len(ids_to_reset)} records to pending — run 'sync' to re-download them")

        return {
            'total': len(completed),
            'ok': ok,
            'missing': len(missing),
            'no_path': no_path,
            'missing_items': missing,
            'fixed': fix and len(missing) > 0,
        }

    def get_stats(self, collection_id: Optional[int] = None) -> DownloadStats:
        """Get download statistics from database."""
        return self.state_db.get_stats(collection_id)

    def get_storage_stats(self, collection_name: Optional[str] = None) -> Dict[str, Any]:
        """Get filesystem statistics."""
        return self.storage.get_directory_stats(collection_name)
