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

        # If ALL collections returned 0 images from the API, cookies are likely expired.
        # (All-already-downloaded would show total_images > 0 since those are counted as skipped.)
        if total_stats['total_images'] == 0 and len(collections) > 0:
            cookie_file = Path(__file__).parent / 'civitai.red_cookies.txt'
            print("\n" + "=" * 60)
            print("WARNING: No images were found in any collection!")
            print("=" * 60)
            print("\nThis usually means authentication failed.")
            print("\nPreferred fix: set a CivitAI API key (generate at")
            print("civitai.com -> Account Settings -> API Keys, then enter it")
            print("in Diffusion Toolkit under Settings > CivitAI, or set the")
            print("CIVITAI_API_KEY environment variable).")
            print("\nAlternatively, re-export your session cookies and replace:")
            print(f"  {cookie_file}")
            print("\n(If your collections are genuinely empty, ignore this.)")
            print("=" * 60 + "\n")
            sys.exit(2)  # Exit code 2 = likely auth failure


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

    client = orchestrator.api_client
    if client.access_token:
        print("[i] Signed in to CivitAI (OAuth access token supplied by Diffusion Toolkit).")
    if client.api_key:
        username = client.test_api_key()
        if username:
            print(f"[OK] API key valid (user: {username})")
        else:
            print("[FAIL] API key rejected - regenerate it at")
            print("  civitai.com -> Account Settings -> API Keys")
    elif not client.access_token:
        print("[i] No API key configured (using cookies).")
        print("  Tip: sign in to CivitAI in Diffusion Toolkit under")
        print("  Settings > CivitAI - more robust than cookies or a pasted key.")

    if orchestrator.test_authentication():
        print(f"[OK] Authentication successful (tRPC accepted: {client.auth_mechanism('trpc')})!")
        print("  You can access NSFW content.")
    else:
        print("[FAIL] Authentication failed")
        if client.api_key:
            print("\nYour API key did not work for the collection API and no")
            print("valid cookies were found. To fix this:")
        else:
            print("\nTo fix this:")
        print("  1. Open Chrome")
        print("  2. Log into CivitAI (via Discord)")
        print("  3. Make sure NSFW content is enabled in settings")
        print("  4. Re-export cookies, or set a CivitAI API key")
        print("  5. Run this script again")


def cmd_verify(args, orchestrator: DownloadOrchestrator, config: dict):
    """Verify completed downloads still exist on disk."""
    collection_id = args.collection if args.collection else None
    results = orchestrator.verify_downloads(collection_id, fix=args.fix)

    print("\n" + "="*60)
    print("DOWNLOAD VERIFICATION")
    print("="*60)
    print(f"\n  Completed records checked: {results['total']}")
    print(f"  Files OK:                  {results['ok']}")
    print(f"  Files MISSING:             {results['missing']}")
    if results['no_path']:
        print(f"  No path recorded:          {results['no_path']}")

    if results['missing'] > 0:
        print(f"\n  Missing files:")
        for record in results['missing_items'][:20]:
            print(f"    - [{record.collection_name}] ID {record.civitai_id}: {record.local_path}")
        if results['missing'] > 20:
            print(f"    ... and {results['missing'] - 20} more")

        if results.get('fixed'):
            print(f"\n  {results['missing']} records reset to pending.")
            print("  Run 'sync' to re-download the missing files.")
        elif not args.fix:
            print(f"\n  To reset these so 'sync' re-downloads them, run:")
            print(f"    python main.py verify --fix")
    else:
        print("\n  All files accounted for!")

    print()


def cmd_cleanup(args, orchestrator: DownloadOrchestrator, config: dict):
    """Cleanup old failed downloads from database."""
    days = args.days or 30
    orchestrator.state_db.cleanup_old_failed(days)
    print(f"Cleaned up failed downloads older than {days} days")


def cmd_list_collections(args, orchestrator: DownloadOrchestrator, config: dict):
    """List the authenticated user's CivitAI collections as JSON to stdout."""
    import json as json_module

    # sys.stdout was redirected to stderr in main() for this command.
    # Use the real stdout (saved as __stdout__) for JSON output.
    real_stdout = sys.__stdout__

    try:
        collections = orchestrator.api_client.get_user_collections()
    except PermissionError as e:
        # Auth failure - output error JSON and exit with code 2
        print(json_module.dumps({"error": "auth", "message": str(e)}), file=real_stdout)
        sys.exit(2)
    except Exception as e:
        print(json_module.dumps({"error": "unknown", "message": str(e)}), file=real_stdout)
        sys.exit(1)

    result = []
    for c in collections:
        entry = {"id": c.id, "name": c.name}
        if c.image_count is not None:
            entry["image_count"] = c.image_count
        result.append(entry)

    # Output clean JSON to real stdout (C# will parse this)
    print(json_module.dumps(result, ensure_ascii=False, indent=2), file=real_stdout)


def cmd_posts(args, orchestrator: DownloadOrchestrator, config: dict):
    """Fetch own CivitAI posts (incl. scheduled) into the calendar cache JSON."""
    import json as json_module
    import datetime
    import posts_fetcher

    real_stdout = sys.__stdout__

    today = datetime.date.today()
    range_from = (datetime.date.fromisoformat(args.from_date) if args.from_date
                  else today - datetime.timedelta(days=4 * 365))
    # CivitAI refuses a publish date more than 3 CALENDAR months out (its
    # SchedulePostModal: dayjs(now).add(3, 'month')). The longest such span is
    # 92 days (Jun 1 -> Sep 1), so this covers the whole queue whatever the
    # month, without needing calendar arithmetic here.
    range_to = (datetime.date.fromisoformat(args.to_date) if args.to_date
                else today + datetime.timedelta(days=92))
    cache_path = Path(args.output) if args.output else posts_fetcher.default_cache_path()

    existing = None if args.full else posts_fetcher.load_cache(cache_path)
    # A cache without a username came from a fetch that paged the sitewide feed
    # (pre-fix bug): its posts are not ours. Never build increments on top of it.
    if existing and not existing.get("username"):
        existing = None

    def emit_progress(message):
        # One JSON line per progress update on the REAL stdout; C# reads these
        # live and shows them. Flush so they arrive as they happen, not buffered.
        try:
            print(json_module.dumps({"progress": message}), file=real_stdout, flush=True)
        except Exception:
            pass

    # Username: CLI arg > config.yaml 'posts: username:' > resolved via API key.
    username = args.username or (config.get('posts') or {}).get('username')

    try:
        cache = posts_fetcher.fetch_posts(
            orchestrator.api_client, config,
            username=username,
            range_from=range_from, range_to=range_to,
            existing_cache=existing, progress=emit_progress,
            want_image_stats=args.image_stats)
    except PermissionError as e:
        print(json_module.dumps({"error": "auth", "message": str(e)}), file=real_stdout)
        sys.exit(2)
    except Exception as e:
        print(json_module.dumps({"error": "fetch", "message": str(e)}), file=real_stdout)
        sys.exit(1)

    emit_progress("Saving…")
    posts_fetcher.write_cache_atomic(cache, cache_path)

    scheduled = sum(1 for p in cache["posts"] if p.get("scheduled"))
    images = sum(len(p.get("images") or []) for p in cache["posts"])
    print(json_module.dumps({
        "status": "ok",
        "posts": len(cache["posts"]),
        "images": images,
        "scheduled": scheduled,
        "warnings": len(cache["warnings"]),
        "cachePath": str(cache_path),
    }), file=real_stdout)


def cmd_schedule_post(args, orchestrator: DownloadOrchestrator, config: dict):
    """Create a scheduled CivitAI post from one or more local image files."""
    import json as json_module
    import posts_fetcher

    real_stdout = sys.__stdout__

    def report_progress(message):
        print(json_module.dumps({"progress": message}), file=real_stdout, flush=True)

    # --publish-now is "schedule for right now": the same create/upload/attach
    # pipeline, with publishedAt set to the current instant.
    publish_at = args.publish_at
    if getattr(args, 'publish_now', False):
        import datetime as _datetime
        publish_at = _datetime.datetime.now(_datetime.timezone.utc).isoformat()

    try:
        result = posts_fetcher.schedule_post(
            orchestrator.api_client, args.file, publish_at, args.title,
            progress=report_progress)
    except RuntimeError as e:
        message = str(e)
        category, _, detail = message.partition(":")
        if category not in ("auth", "upload", "schedule"):
            category, detail = "schedule", message
        print(json_module.dumps({"error": category, "message": detail.strip()}), file=real_stdout)
        sys.exit(2 if category == "auth" else 1)
    except Exception as e:
        print(json_module.dumps({"error": "schedule", "message": str(e)}), file=real_stdout)
        sys.exit(1)

    # Record the new post straight into the calendar cache so it shows up
    # immediately - best-effort: a cache hiccup must not fail the scheduling.
    try:
        posts_fetcher.record_scheduled_post(
            orchestrator.api_client, posts_fetcher.default_cache_path(),
            result.get("postId"), result.get("publishedAt"),
            args.title, result.get("postImages") or [],
            scheduled=not getattr(args, 'publish_now', False))
    except Exception as e:
        import logging
        logging.getLogger(__name__).warning(
            f"Could not record scheduled post in calendar cache: {e}")

    print(json_module.dumps(result), file=real_stdout)


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
    parser.add_argument('--collections-file', type=str,
                        help='JSON file with collections to sync (overrides config.yaml collections)')

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

    # Verify command
    verify_parser = subparsers.add_parser('verify',
                                          help='Check that completed downloads still exist on disk')
    verify_parser.add_argument('-c', '--collection', type=int,
                               help='Only verify downloads from this collection')
    verify_parser.add_argument('--fix', action='store_true',
                               help='Reset missing files to pending so sync re-downloads them')

    # List collections command
    list_collections_parser = subparsers.add_parser('list-collections',
                                                      help='List user collections as JSON')

    # Posts calendar commands
    posts_parser = subparsers.add_parser('posts',
                                         help='Fetch own posts (incl. scheduled) into the calendar cache')
    posts_parser.add_argument('--from', dest='from_date', type=str, default=None,
                              help='Range start YYYY-MM-DD (default: today - 4 years)')
    posts_parser.add_argument('--to', dest='to_date', type=str, default=None,
                              help='Range end YYYY-MM-DD (default: today + 3 months)')
    posts_parser.add_argument('--full', action='store_true',
                              help='Ignore existing cache and refetch the whole range')
    posts_parser.add_argument('--output', type=str, default=None,
                              help='Cache file path (default: AppData DiffusionToolkit/Civitai/posts_cache.json)')
    posts_parser.add_argument('--username', type=str, default=None,
                              help='CivitAI username (default: resolved via API key)')
    posts_parser.add_argument('--image-stats', action='store_true',
                              help='Also fetch per-image stats (adds tips and views, '
                                   'at one extra request per post)')

    schedule_parser = subparsers.add_parser('schedule-post',
                                            help='Create a scheduled CivitAI post from local images')
    schedule_parser.add_argument('--file', required=True, action='append',
                                 help='Path to an image file (repeat to post several images together)')
    # Exactly one of the two: a future date, or publish immediately.
    when_group = schedule_parser.add_mutually_exclusive_group(required=True)
    when_group.add_argument('--publish-at',
                            help='Publish date-time, ISO 8601 (e.g. 2026-08-01T17:00:00+02:00)')
    when_group.add_argument('--publish-now', action='store_true',
                            help='Publish immediately instead of scheduling')
    schedule_parser.add_argument('--title', default=None, help='Optional post title')

    args = parser.parse_args()

    # For machine-readable commands, we need clean stdout (JSON only).
    # Redirect all console output to stderr before anything else logs to stdout.
    if args.command in ('list-collections', 'posts', 'schedule-post'):
        import io
        # Redirect print() and logging to stderr so stdout stays clean for JSON
        sys.stdout = sys.stderr
        # We'll restore stdout only when we need to output JSON

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

    # Override collections if file provided (for Diffusion Toolkit integration)
    if args.collections_file:
        import json as json_module
        try:
            with open(args.collections_file, 'r', encoding='utf-8') as f:
                collections_override = json_module.load(f)
            config['collections'] = collections_override
            print(f"Using collections override: {len(collections_override)} collections from {args.collections_file}")
        except Exception as e:
            print(f"Error reading collections file: {e}")
            sys.exit(1)

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
        'verify': cmd_verify,
        'cleanup': cmd_cleanup,
        'list-collections': cmd_list_collections,
        'posts': cmd_posts,
        'schedule-post': cmd_schedule_post,
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
