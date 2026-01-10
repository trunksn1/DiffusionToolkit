"""
Data models for CivitAI downloader.
Using dataclasses for type safety and clarity.
"""

from dataclasses import dataclass
from datetime import datetime
from typing import Optional
from enum import Enum


class DownloadStatus(Enum):
    """Status of a download item."""
    PENDING = "pending"
    DOWNLOADING = "downloading"
    COMPLETED = "completed"
    FAILED = "failed"
    SKIPPED = "skipped"


@dataclass
class ImageItem:
    """Represents an image from CivitAI."""
    id: int
    name: str
    url: str  # The hash part of the URL
    collection_id: int
    collection_name: str
    nsfw: Optional[bool] = None

    def __hash__(self):
        return hash(self.id)

    def __eq__(self, other):
        if isinstance(other, ImageItem):
            return self.id == other.id
        return False


@dataclass
class DownloadRecord:
    """Database record of a download."""
    civitai_id: int
    collection_id: int
    collection_name: str
    status: DownloadStatus
    local_path: Optional[str] = None
    file_hash: Optional[str] = None
    error_message: Optional[str] = None
    retry_count: int = 0
    created_at: Optional[datetime] = None
    updated_at: Optional[datetime] = None

    def to_dict(self):
        """Convert to dictionary for database storage."""
        return {
            'civitai_id': self.civitai_id,
            'collection_id': self.collection_id,
            'collection_name': self.collection_name,
            'status': self.status.value,
            'local_path': self.local_path,
            'file_hash': self.file_hash,
            'error_message': self.error_message,
            'retry_count': self.retry_count,
            'created_at': self.created_at or datetime.now(),
            'updated_at': datetime.now()
        }


@dataclass
class Collection:
    """Represents a CivitAI collection."""
    id: int
    name: str
    image_count: Optional[int] = None

    def __str__(self):
        if self.image_count:
            return f"{self.name} ({self.image_count} images)"
        return self.name


@dataclass
class DownloadStats:
    """Statistics about download progress."""
    total: int = 0
    completed: int = 0
    failed: int = 0
    skipped: int = 0
    pending: int = 0

    @property
    def remaining(self) -> int:
        return self.pending

    @property
    def success_rate(self) -> float:
        if self.total == 0:
            return 0.0
        return (self.completed / self.total) * 100

    def __str__(self):
        return (
            f"Total: {self.total}, "
            f"Completed: {self.completed}, "
            f"Failed: {self.failed}, "
            f"Skipped: {self.skipped}, "
            f"Pending: {self.pending}"
        )
