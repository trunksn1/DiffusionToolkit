"""
CivitAI Collection Downloader - v04 (Proper Architecture)

A robust, efficient downloader for CivitAI collections with:
- Cookie-based authentication for NSFW content (no membership required)
- State management for resumable downloads
- Multi-extension deduplication
- Integration with DiffusionToolkit
- Concurrent downloads with proper error handling

Usage:
    python main.py sync                    # Sync all collections from config
    python main.py sync --collection 3157498  # Sync specific collection
    python main.py retry                   # Retry failed downloads
    python main.py stats                   # Show download statistics
    python main.py test-auth               # Test authentication
"""

import sys
import argparse
import logging
from pathlib import Path
from typing import Optional
import yaml

from downloader import DownloadOrchestrator
from models import Collection


def setup_logging(config: dict):
    """Configure logging based on config."""
    log_config = config.get('logging', {})
    level = getattr(logging, log_config.get('level', 'INFO'))

    # Create formatters
    formatter = logging.Formatter(
        '%(asctime)s - %(name)s - %(levelname)s - %(message)s',
        datefmt='%Y-%m-%d %H:%M:%S'
    )

    # Root logger
    root_logger = logging.getLogger()
    root_logger.setLevel(level)

    # Console handler with UTF-8 encoding
    if log_config.get('console', True):
        # Force UTF-8 for console output to handle accented characters
        console_handler = logging.StreamHandler(sys.stdout)
        console_handler.setFormatter(formatter)
        root_logger.addHandler(console_handler)

    # File handler with UTF-8 encoding
    if log_file := log_config.get('file'):
        file_handler = logging.FileHandler(log_file, encoding='utf-8')
        file_handler.setFormatter(formatter)
        root_logger.addHandler(file_handler)


def load_config(config_path: str = 'config.yaml') -> dict:
    """Load configuration from YAML file."""
    config_file = Path(config_path)

    if not config_file.exists():
        print(f"Error: Config file not found: {config_path}")
        print("Please create config.yaml from the template")
        sys.exit(1)

    # CRITICAL: Specify UTF-8 encoding to prevent "Josè" → "JosÃ¨" mangling on Windows
    with open(config_file, 'r', encoding='utf-8') as f:
        config = yaml.safe_load(f)

    return config


def cmd_sync(args, orchestrator: DownloadOrchestrator, config: dict):
    """Sync collections (download new images)."""

    # Dry run mode - just show statistics
    if args.dry_run:
        cmd_dry_run(args, orchestrator, config)
        return

    if args.collection:
        # Sync specific collection by ID
        collection_id = args.collection
        collection_name = args.name or f"Collection_{collection_id}"

        orchestrator.sync_collection(collection_id, collection_name)

    else:
        # Sync all collections from config
        collections = config.get('collections', [])

        if not collections:
            print("No collections configured in config.yaml")
            print("Add collections or use --collection flag")
            sys.exit(1)

        print(f"\nSyncing {len(collections)} collections from config...\n")

        # Track statistics
        collection_results = []
        total_stats = {
            'total_images': 0,
            'already_downloaded': 0,
            'downloaded': 0,
            'failed': 0
        }

        for coll_config in collections:
            collection_id = coll_config['id']
            collection_name = coll_config['name']

            try:
                stats = orchestrator.sync_collection(collection_id, collection_name)

                # Store collection results
                collection_results.append({
                    'name': collection_name,
                    'id': collection_id,
                    'total': stats.total,
                    'already_have': stats.skipped,
                    'downloaded': stats.completed,
                    'failed': stats.failed
                })

                # Update totals
                total_stats['total_images'] += stats.total
                total_stats['already_downloaded'] += stats.skipped
                total_stats['downloaded'] += stats.completed
                total_stats['failed'] += stats.failed

            except KeyboardInterrupt:
                print("\n\nInterrupted by user")
                print("Progress has been saved. Run again to continue.")
                sys.exit(0)
            except Exception as e:
                logging.error(f"Failed to sync {collection_name}: {e}")
                if args.stop_on_error:
                    raise

        # Display statistics
        print("\n" + "="*80)
        print("SYNC COMPLETE - STATISTICS")
        print("="*80)

        # Overall summary
        print(f"\nOVERALL TOTALS:")
        print(f"  Collections synced: {len(collection_results)}")
        print(f"  Total images found: {total_stats['total_images']}")
        print(f"  Already downloaded: {total_stats['already_downloaded']}")
        print(f"  Downloaded this run: {total_stats['downloaded']}")
        if total_stats['failed'] > 0:
            print(f"  Failed: {total_stats['failed']}")

        # Per-collection breakdown
        print(f"\n{'='*80}")
        print("PER-COLLECTION BREAKDOWN")
        print(f"{'='*80}")
        print(f"{'Collection':<30} {'Found':<10} {'Had':<10} {'Got':<10} {'Failed':<10}")
        print(f"{'-'*80}")
        for result in collection_results:
            print(f"{result['name']:<30} {result['total']:<10} {result['already_have']:<10} {result['downloaded']:<10} {result['failed']:<10}")

        print(f"\n{'='*80}")


def cmd_dry_run(args, orchestrator: DownloadOrchestrator, config: dict):
    """Show what would be downloaded without actually downloading."""
    print("\n" + "="*60)
    print("DRY RUN MODE - No files will be downloaded")
    print("="*60 + "\n")

    if args.collection:
        # Preview specific collection
        collections = [{'id': args.collection, 'name': args.name or f"Collection_{args.collection}"}]
    else:
        # Preview all collections from config
        collections = config.get('collections', [])
        if not collections:
            print("No collections configured in config.yaml")
            sys.exit(1)

    # Store stats for each collection
    collection_results = []

    total_stats = {
        'total_images': 0,
        'already_downloaded': 0,
        'to_download': 0,
    }

    for coll in collections:
        print(f"Fetching: {coll['name']} (ID: {coll['id']})...", end=' ', flush=True)

        try:
            # DEBUG: Check if collection directory exists and has files
            collection_dir = Path(config['paths']['downloads']) / coll['name']
            if collection_dir.exists():
                existing_files = list(collection_dir.glob('*'))
                print(f"\n  DEBUG: Collection directory exists with {len(existing_files)} files")
                if existing_files:
                    print(f"  Sample file: {existing_files[0].name}")
            else:
                print(f"\n  DEBUG: Collection directory does NOT exist: {collection_dir}")

            # Fetch images from API
            images = orchestrator.api_client.get_collection_images(coll['id'], coll['name'])

            if not images:
                print("No images found")
                collection_results.append({
                    'name': coll['name'],
                    'id': coll['id'],
                    'total': 0,
                    'already_have': 0,
                    'to_download': 0,
                    'reasons': {}
                })
                continue

            # Check each image
            to_download = []
            already_have = []
            reasons = {}

            for image in images:
                should_download, reason = orchestrator.storage.should_download(image)

                if should_download:
                    to_download.append(image)
                else:
                    already_have.append(image)
                    reason_key = reason.split('(')[0].strip() if reason else 'unknown'
                    reasons[reason_key] = reasons.get(reason_key, 0) + 1

            print(f"Done ({len(images)} images found)")

            # Store collection results
            collection_results.append({
                'name': coll['name'],
                'id': coll['id'],
                'total': len(images),
                'already_have': len(already_have),
                'to_download': len(to_download),
                'reasons': reasons,
                'sample': to_download[:5]  # Store sample for later display
            })

            # Update totals
            total_stats['total_images'] += len(images)
            total_stats['already_downloaded'] += len(already_have)
            total_stats['to_download'] += len(to_download)

        except Exception as e:
            print(f"ERROR: {e}")
            logging.error(f"Failed to preview {coll['name']}: {e}", exc_info=True)
            continue

    # Now display all results in a nice summary table
    print(f"\n{'='*80}")
    print("COLLECTION SUMMARY")
    print(f"{'='*80}")
    print(f"{'Collection Name':<30} {'ID':<10} {'Total':<8} {'Have':<8} {'Download':<10}")
    print(f"{'-'*80}")

    for result in collection_results:
        print(f"{result['name']:<30} {result['id']:<10} {result['total']:<8} {result['already_have']:<8} {result['to_download']:<10}")

    # Detailed breakdown for each collection
    print(f"\n{'='*80}")
    print("DETAILED BREAKDOWN")
    print(f"{'='*80}")

    for result in collection_results:
        print(f"\n{result['name']} (ID: {result['id']})")
        print(f"  Total images: {result['total']}")
        print(f"  Already have: {result['already_have']}")

        if result['reasons']:
            print(f"    Why skipped:")
            for reason, count in sorted(result['reasons'].items()):
                print(f"      - {reason}: {count}")

        print(f"  Would download: {result['to_download']}")

        if result['sample']:
            print(f"  Sample images to download:")
            for img in result['sample']:
                print(f"    - {img.name} (ID: {img.id})")

    # Overall summary
    print(f"\n{'='*80}")
    print("OVERALL TOTALS")
    print(f"{'='*80}")
    print(f"  Collections checked: {len(collection_results)}")
    print(f"  Total images found: {total_stats['total_images']}")
    print(f"  Already downloaded: {total_stats['already_downloaded']}")
    print(f"  Would download: {total_stats['to_download']}")

    if total_stats['to_download'] > 0:
        estimated_size_mb = total_stats['to_download'] * 2  # Rough estimate: 2MB per image
        print(f"\n  Estimated download size: ~{estimated_size_mb} MB (~{estimated_size_mb/1024:.1f} GB)")
        estimated_time_min = total_stats['to_download'] / 2  # Rough estimate: 2 images per minute
        print(f"  Estimated download time: ~{estimated_time_min:.0f} minutes")

    # Per-collection breakdown
    print(f"\n{'='*80}")
    print("PER-COLLECTION BREAKDOWN")
    print(f"{'='*80}")
    print(f"{'Collection':<30} {'Found':<10} {'Downloaded':<12} {'To Download':<12}")
    print(f"{'-'*80}")
    for result in collection_results:
        print(f"{result['name']:<30} {result['total']:<10} {result['already_have']:<12} {result['to_download']:<12}")

    print(f"\n{'='*80}")
    print("To actually download, run: python main.py sync")
    print(f"{'='*80}\n")


def cmd_retry(args, orchestrator: DownloadOrchestrator, config: dict):
    """Retry failed downloads."""
    collection_id = args.collection if args.collection else None
    stats = orchestrator.retry_failed(collection_id)

    print("\nRetry Results:")
    print(f"  Completed: {stats.completed}")
    print(f"  Failed: {stats.failed}")
    print(f"  Success rate: {stats.success_rate:.1f}%")


def cmd_stats(args, orchestrator: DownloadOrchestrator, config: dict):
    """Show download statistics."""
    print("\n" + "="*60)
    print("Download Statistics")
    print("="*60 + "\n")

    if args.collection:
        # Stats for specific collection
        stats = orchestrator.get_stats(args.collection)
        print(f"Collection ID: {args.collection}")
        print(f"  {stats}")

    else:
        # Overall stats
        stats = orchestrator.get_stats()
        print(f"Overall: {stats}\n")

        # Per-collection stats
        collections = config.get('collections', [])
        if collections:
            print("Per-collection breakdown:")
            for coll in collections:
                coll_stats = orchestrator.get_stats(coll['id'])
                storage_stats = orchestrator.get_storage_stats(coll['name'])

                print(f"\n  {coll['name']} (ID: {coll['id']}):")
                print(f"    Database: {coll_stats}")
                print(f"    Filesystem: {storage_stats['total_files']} files, "
                      f"{storage_stats['total_size_mb']} MB")


def cmd_test_auth(args, orchestrator: DownloadOrchestrator, config: dict):
    """Test API authentication."""
    print("\nTesting CivitAI authentication...")
    print("-" * 40)

    if orchestrator.test_authentication():
        print("[OK] Authentication successful!")
        print("  You can access NSFW content.")
    else:
        print("[FAIL] Authentication failed or cookies not found")
        print("\nTo fix this:")
        print("  1. Open Chrome")
        print("  2. Log into CivitAI (via Discord)")
        print("  3. Make sure NSFW content is enabled in settings")
        print("  4. Run this script again")


def cmd_cleanup(args, orchestrator: DownloadOrchestrator, config: dict):
    """Cleanup old failed downloads from database."""
    days = args.days or 30
    orchestrator.state_db.cleanup_old_failed(days)
    print(f"Cleaned up failed downloads older than {days} days")


def main():
    """Main entry point."""
    parser = argparse.ArgumentParser(
        description='CivitAI Collection Downloader v04',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  python main.py sync                          # Sync all collections
  python main.py sync --dry-run                # Preview what would be downloaded
  python main.py sync -c 3157498 -n "My Collection"  # Sync one collection
  python main.py sync -c 3157498 --dry-run     # Preview one collection
  python main.py retry                         # Retry failed downloads
  python main.py stats                         # Show statistics
  python main.py test-auth                     # Test authentication
        """
    )

    # Global options
    parser.add_argument('--config', default='config.yaml',
                        help='Path to config file (default: config.yaml)')
    parser.add_argument('--db-path', type=str,
                        help='Override database path (used by Diffusion Toolkit integration)')
    parser.add_argument('--max-pages', type=int, default=None,
                        help='Max pages per collection (0=unlimited, used by Diffusion Toolkit integration)')

    # Subcommands
    subparsers = parser.add_subparsers(dest='command', help='Command to run')

    # Sync command
    sync_parser = subparsers.add_parser('sync', help='Sync collections (download new images)')
    sync_parser.add_argument('-c', '--collection', type=int,
                             help='Specific collection ID to sync')
    sync_parser.add_argument('-n', '--name', type=str,
                             help='Collection name (used with --collection)')
    sync_parser.add_argument('--dry-run', action='store_true',
                             help='Preview what would be downloaded without actually downloading')
    sync_parser.add_argument('--stop-on-error', action='store_true',
                             help='Stop if a collection fails')

    # Retry command
    retry_parser = subparsers.add_parser('retry', help='Retry failed downloads')
    retry_parser.add_argument('-c', '--collection', type=int,
                              help='Only retry failures from this collection')

    # Stats command
    stats_parser = subparsers.add_parser('stats', help='Show download statistics')
    stats_parser.add_argument('-c', '--collection', type=int,
                              help='Show stats for specific collection')

    # Test auth command
    test_auth_parser = subparsers.add_parser('test-auth', help='Test API authentication')

    # Cleanup command
    cleanup_parser = subparsers.add_parser('cleanup', help='Cleanup old failed downloads')
    cleanup_parser.add_argument('--days', type=int, default=30,
                                help='Remove failures older than N days')

    args = parser.parse_args()

    # Load configuration
    try:
        config = load_config(args.config)
    except Exception as e:
        print(f"Error loading config: {e}")
        sys.exit(1)

    # Override database path if provided (for Diffusion Toolkit integration)
    if args.db_path:
        print(f"Using database path override: {args.db_path}")
        config['paths']['state_db'] = args.db_path

    # Override max pages if provided (for Diffusion Toolkit integration)
    if args.max_pages is not None:
        print(f"Using max pages override: {args.max_pages}")
        config['api']['max_pages_per_collection'] = args.max_pages

    # Setup logging
    setup_logging(config)

    # Create orchestrator
    try:
        orchestrator = DownloadOrchestrator(config)
    except Exception as e:
        logging.error(f"Failed to initialize downloader: {e}")
        sys.exit(1)

    # Run command
    if not args.command:
        parser.print_help()
        sys.exit(0)

    commands = {
        'sync': cmd_sync,
        'retry': cmd_retry,
        'stats': cmd_stats,
        'test-auth': cmd_test_auth,
        'cleanup': cmd_cleanup,
    }

    try:
        commands[args.command](args, orchestrator, config)
    except KeyboardInterrupt:
        print("\n\nInterrupted by user")
        sys.exit(0)
    except Exception as e:
        logging.error(f"Error: {e}", exc_info=True)
        sys.exit(1)


if __name__ == '__main__':
    main()
