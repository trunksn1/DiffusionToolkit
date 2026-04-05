"""
One-time script to backfill usernames for existing records in the CivitAI state database.
Fetches image metadata from the CivitAI API and updates the username column.

Usage:
    python backfill_usernames.py --db-path "C:/Users/trunk/AppData/Roaming/DiffusionToolkit/Civitai/civitai_state.db"
"""

import argparse
import sqlite3
import time
import sys
import logging
from pathlib import Path

import yaml
import requests

# Reuse the existing cookie loading from api_client
sys.path.insert(0, str(Path(__file__).parent))
from api_client import CivitAIClient

logging.basicConfig(level=logging.INFO, format='%(message)s')
logger = logging.getLogger(__name__)


def load_config():
    config_path = Path(__file__).parent / 'config.yaml'
    with open(config_path, 'r', encoding='utf-8') as f:
        return yaml.safe_load(f)


def ensure_username_column(db_path: str):
    """Add username column if it doesn't exist yet."""
    conn = sqlite3.connect(db_path)
    try:
        conn.execute("ALTER TABLE downloads ADD COLUMN username TEXT")
        conn.commit()
        logger.info("Added 'username' column to downloads table")
    except sqlite3.OperationalError:
        pass  # Column already exists
    conn.close()


def get_missing_username_ids(db_path: str) -> list:
    """Get all civitai_ids that have no username."""
    conn = sqlite3.connect(db_path)
    cursor = conn.execute(
        "SELECT civitai_id FROM downloads WHERE username IS NULL AND status = 'completed'"
    )
    ids = [row[0] for row in cursor.fetchall()]
    conn.close()
    return ids


def update_username(db_path: str, civitai_id: int, username: str):
    """Update the username for a single record."""
    conn = sqlite3.connect(db_path)
    conn.execute(
        "UPDATE downloads SET username = ? WHERE civitai_id = ?",
        (username, civitai_id)
    )
    conn.commit()
    conn.close()


def fetch_image_username(session: requests.Session, trpc_url: str, image_id: int) -> str | None:
    """Fetch username for a single image from the CivitAI tRPC API."""
    import json
    try:
        trpc_input = json.dumps({'json': {'id': image_id}}, separators=(',', ':'))
        url = f"{trpc_url}/image.get"
        response = session.get(url, params={'input': trpc_input}, timeout=15)

        if response.status_code == 404:
            return None
        response.raise_for_status()

        data = response.json()
        result = data.get('result', {}).get('data', {}).get('json', {})
        user = result.get('user')
        if isinstance(user, dict):
            return user.get('username')
        return None

    except requests.RequestException as e:
        logger.warning(f"  API error for image {image_id}: {e}")
        return None


def main():
    parser = argparse.ArgumentParser(description='Backfill usernames in CivitAI state database')
    parser.add_argument('--db-path', required=True, help='Path to civitai_state.db')
    parser.add_argument('--delay', type=float, default=1.0, help='Delay between API calls (seconds)')
    parser.add_argument('--dry-run', action='store_true', help='Show what would be done without updating')
    args = parser.parse_args()

    db_path = args.db_path
    if not Path(db_path).exists():
        logger.error(f"Database not found: {db_path}")
        sys.exit(1)

    # Ensure column exists, then get records missing usernames
    ensure_username_column(db_path)
    ids = get_missing_username_ids(db_path)
    logger.info(f"Found {len(ids)} records with missing usernames")

    if not ids:
        logger.info("Nothing to do!")
        return

    if args.dry_run:
        logger.info("Dry run - would fetch usernames for these IDs:")
        for cid in ids[:20]:
            logger.info(f"  {cid}")
        if len(ids) > 20:
            logger.info(f"  ... and {len(ids) - 20} more")
        return

    # Initialize API client (reuses cookie loading)
    config = load_config()
    client = CivitAIClient(config)

    updated = 0
    failed = 0

    for i, cid in enumerate(ids):
        username = fetch_image_username(client.session, config['api']['trpc_url'], cid)

        if username:
            update_username(db_path, cid, username)
            updated += 1
            logger.info(f"  [{i+1}/{len(ids)}] Image {cid} -> {username}")
        else:
            failed += 1
            logger.info(f"  [{i+1}/{len(ids)}] Image {cid} -> (not found)")

        # Rate limiting
        if i < len(ids) - 1:
            time.sleep(args.delay)

    logger.info(f"\nDone! Updated: {updated}, Not found: {failed}")


if __name__ == '__main__':
    main()
