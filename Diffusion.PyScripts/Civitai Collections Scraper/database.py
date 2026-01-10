"""
State management using SQLite.
Tracks download progress, allows resuming interrupted downloads.
"""

import sqlite3
from pathlib import Path
from typing import List, Optional, Set
from datetime import datetime
from contextlib import contextmanager
import logging

from models import DownloadRecord, DownloadStatus, DownloadStats, ImageItem

logger = logging.getLogger(__name__)


class StateDatabase:
    """Manages download state in SQLite database."""

    def __init__(self, db_path: str):
        self.db_path = Path(db_path)
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        self._init_database()

    @contextmanager
    def _get_connection(self):
        """Context manager for database connections."""
        conn = sqlite3.connect(self.db_path)
        conn.row_factory = sqlite3.Row
        try:
            yield conn
            conn.commit()
        except Exception:
            conn.rollback()
            raise
        finally:
            conn.close()

    def _init_database(self):
        """Create tables if they don't exist."""
        with self._get_connection() as conn:
            conn.execute("""
                CREATE TABLE IF NOT EXISTS downloads (
                    civitai_id INTEGER PRIMARY KEY,
                    collection_id INTEGER NOT NULL,
                    collection_name TEXT NOT NULL,
                    status TEXT NOT NULL,
                    local_path TEXT,
                    file_hash TEXT,
                    error_message TEXT,
                    retry_count INTEGER DEFAULT 0,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                )
            """)

            # Create indexes for better query performance
            conn.execute("""
                CREATE INDEX IF NOT EXISTS idx_collection_id
                ON downloads(collection_id)
            """)
            conn.execute("""
                CREATE INDEX IF NOT EXISTS idx_status
                ON downloads(status)
            """)
            conn.execute("""
                CREATE INDEX IF NOT EXISTS idx_file_hash
                ON downloads(file_hash)
            """)

    def exists(self, civitai_id: int) -> bool:
        """Check if an image has been downloaded."""
        with self._get_connection() as conn:
            cursor = conn.execute(
                "SELECT 1 FROM downloads WHERE civitai_id = ? AND status = ?",
                (civitai_id, DownloadStatus.COMPLETED.value)
            )
            return cursor.fetchone() is not None

    def get_downloaded_ids(self, collection_id: Optional[int] = None) -> Set[int]:
        """Get set of all downloaded image IDs."""
        with self._get_connection() as conn:
            if collection_id:
                cursor = conn.execute(
                    "SELECT civitai_id FROM downloads WHERE status = ? AND collection_id = ?",
                    (DownloadStatus.COMPLETED.value, collection_id)
                )
            else:
                cursor = conn.execute(
                    "SELECT civitai_id FROM downloads WHERE status = ?",
                    (DownloadStatus.COMPLETED.value,)
                )
            return {row['civitai_id'] for row in cursor.fetchall()}

    def mark_pending(self, image: ImageItem):
        """Mark an image as pending download."""
        record = DownloadRecord(
            civitai_id=image.id,
            collection_id=image.collection_id,
            collection_name=image.collection_name,
            status=DownloadStatus.PENDING
        )
        self._upsert_record(record)

    def mark_pending_batch(self, images: List[ImageItem]):
        """
        Mark multiple images as pending in a single transaction.
        MUCH faster than calling mark_pending() in a loop (1 transaction vs N transactions).
        """
        if not images:
            return

        with self._get_connection() as conn:
            # Prepare all records
            records = [
                (
                    img.id,
                    img.collection_id,
                    img.collection_name,
                    DownloadStatus.PENDING.value,
                    None,  # local_path
                    None,  # file_hash
                    None,  # error_message
                    0      # retry_count
                )
                for img in images
            ]

            # Execute all inserts/updates in a single transaction
            conn.executemany("""
                INSERT INTO downloads (
                    civitai_id, collection_id, collection_name, status,
                    local_path, file_hash, error_message, retry_count
                )
                VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(civitai_id) DO UPDATE SET
                    collection_id = excluded.collection_id,
                    collection_name = excluded.collection_name,
                    status = excluded.status,
                    updated_at = CURRENT_TIMESTAMP
            """, records)

    def mark_downloading(self, civitai_id: int):
        """
        Mark an image as currently downloading.
        NOTE: This is deprecated and not used anymore (status is never read).
        Kept for backwards compatibility but does nothing.
        """
        pass  # OPTIMIZATION: Removed wasteful DB write - status never read anyway

    def mark_completed(self, civitai_id: int, local_path: str, file_hash: Optional[str] = None):
        """Mark an image as successfully downloaded."""
        with self._get_connection() as conn:
            conn.execute("""
                UPDATE downloads
                SET status = ?, local_path = ?, file_hash = ?, updated_at = ?
                WHERE civitai_id = ?
            """, (DownloadStatus.COMPLETED.value, local_path, file_hash, datetime.now(), civitai_id))

    def mark_completed_batch(self, completions: List[tuple]):
        """
        Mark multiple images as completed in a single transaction.

        Args:
            completions: List of (civitai_id, local_path, file_hash) tuples
        """
        if not completions:
            return

        with self._get_connection() as conn:
            # Prepare records with timestamp
            records = [
                (DownloadStatus.COMPLETED.value, local_path, file_hash, datetime.now(), civitai_id)
                for civitai_id, local_path, file_hash in completions
            ]

            conn.executemany("""
                UPDATE downloads
                SET status = ?, local_path = ?, file_hash = ?, updated_at = ?
                WHERE civitai_id = ?
            """, records)

    def mark_failed(self, civitai_id: int, error_message: str):
        """Mark an image download as failed."""
        with self._get_connection() as conn:
            conn.execute("""
                UPDATE downloads
                SET status = ?, error_message = ?, retry_count = retry_count + 1, updated_at = ?
                WHERE civitai_id = ?
            """, (DownloadStatus.FAILED.value, error_message, datetime.now(), civitai_id))

    def mark_failed_batch(self, failures: List[tuple]):
        """
        Mark multiple images as failed in a single transaction.

        Args:
            failures: List of (civitai_id, error_message) tuples
        """
        if not failures:
            return

        with self._get_connection() as conn:
            # Prepare records with timestamp
            records = [
                (DownloadStatus.FAILED.value, error_msg, datetime.now(), civitai_id)
                for civitai_id, error_msg in failures
            ]

            conn.executemany("""
                UPDATE downloads
                SET status = ?, error_message = ?, retry_count = retry_count + 1, updated_at = ?
                WHERE civitai_id = ?
            """, records)

    def mark_skipped(self, civitai_id: int, reason: str):
        """Mark an image as skipped (already exists elsewhere)."""
        with self._get_connection() as conn:
            # Check if record exists
            cursor = conn.execute("SELECT 1 FROM downloads WHERE civitai_id = ?", (civitai_id,))
            if cursor.fetchone():
                conn.execute("""
                    UPDATE downloads
                    SET status = ?, error_message = ?, updated_at = ?
                    WHERE civitai_id = ?
                """, (DownloadStatus.SKIPPED.value, reason, datetime.now(), civitai_id))
            else:
                # Insert new record
                conn.execute("""
                    INSERT INTO downloads (civitai_id, collection_id, collection_name, status, error_message)
                    VALUES (?, 0, 'unknown', ?, ?)
                """, (civitai_id, DownloadStatus.SKIPPED.value, reason))

    def get_failed_items(self, max_retries: int = 3) -> List[DownloadRecord]:
        """Get items that failed but haven't exceeded retry limit."""
        with self._get_connection() as conn:
            cursor = conn.execute("""
                SELECT * FROM downloads
                WHERE status = ? AND retry_count < ?
                ORDER BY updated_at ASC
            """, (DownloadStatus.FAILED.value, max_retries))

            return [self._row_to_record(row) for row in cursor.fetchall()]

    def get_stats(self, collection_id: Optional[int] = None) -> DownloadStats:
        """Get download statistics."""
        with self._get_connection() as conn:
            query = "SELECT status, COUNT(*) as count FROM downloads"
            params = []

            if collection_id:
                query += " WHERE collection_id = ?"
                params.append(collection_id)

            query += " GROUP BY status"
            cursor = conn.execute(query, params)

            stats = DownloadStats()
            for row in cursor.fetchall():
                status = row['status']
                count = row['count']
                stats.total += count

                if status == DownloadStatus.COMPLETED.value:
                    stats.completed = count
                elif status == DownloadStatus.FAILED.value:
                    stats.failed = count
                elif status == DownloadStatus.SKIPPED.value:
                    stats.skipped = count
                elif status == DownloadStatus.PENDING.value:
                    stats.pending = count

            return stats

    def get_path_by_id(self, civitai_id: int) -> Optional[str]:
        """Get the local path for a downloaded image."""
        with self._get_connection() as conn:
            cursor = conn.execute(
                "SELECT local_path FROM downloads WHERE civitai_id = ? AND status = ?",
                (civitai_id, DownloadStatus.COMPLETED.value)
            )
            row = cursor.fetchone()
            return row['local_path'] if row else None

    def _upsert_record(self, record: DownloadRecord):
        """Insert or update a download record."""
        with self._get_connection() as conn:
            conn.execute("""
                INSERT INTO downloads (
                    civitai_id, collection_id, collection_name, status,
                    local_path, file_hash, error_message, retry_count
                )
                VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(civitai_id) DO UPDATE SET
                    collection_id = excluded.collection_id,
                    collection_name = excluded.collection_name,
                    status = excluded.status,
                    local_path = excluded.local_path,
                    file_hash = excluded.file_hash,
                    error_message = excluded.error_message,
                    retry_count = excluded.retry_count,
                    updated_at = CURRENT_TIMESTAMP
            """, (
                record.civitai_id, record.collection_id, record.collection_name,
                record.status.value, record.local_path, record.file_hash,
                record.error_message, record.retry_count
            ))

    def _row_to_record(self, row) -> DownloadRecord:
        """Convert database row to DownloadRecord."""
        return DownloadRecord(
            civitai_id=row['civitai_id'],
            collection_id=row['collection_id'],
            collection_name=row['collection_name'],
            status=DownloadStatus(row['status']),
            local_path=row['local_path'],
            file_hash=row['file_hash'],
            error_message=row['error_message'],
            retry_count=row['retry_count'],
            created_at=datetime.fromisoformat(row['created_at']) if row['created_at'] else None,
            updated_at=datetime.fromisoformat(row['updated_at']) if row['updated_at'] else None
        )

    def reset_collection(self, collection_id: int):
        """Reset all downloads for a collection (set back to pending)."""
        with self._get_connection() as conn:
            conn.execute("""
                UPDATE downloads
                SET status = ?, error_message = NULL, retry_count = 0, updated_at = ?
                WHERE collection_id = ?
            """, (DownloadStatus.PENDING.value, datetime.now(), collection_id))
            logger.info(f"Reset collection {collection_id} to pending")

    def cleanup_old_failed(self, days: int = 30):
        """Remove old failed download records."""
        with self._get_connection() as conn:
            conn.execute("""
                DELETE FROM downloads
                WHERE status = ? AND updated_at < datetime('now', ? || ' days')
            """, (DownloadStatus.FAILED.value, f'-{days}'))
            logger.info(f"Cleaned up failed downloads older than {days} days")
