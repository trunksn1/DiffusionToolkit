using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.Database;
using Diffusion.Database.Models;

namespace Diffusion.Toolkit.Services;

/// <summary>
/// Backs the CivitAI Posts Calendar: loads the posts cache produced by the
/// Python scraper (`main.py posts`), refreshes it on demand, schedules new
/// posts (`main.py schedule-post`), and matches CivitAI images to local
/// library files by original filename.
/// </summary>
public class CivitaiPostsService
{
    public const string AuthFailureMessage =
        "CivitAI authentication failed.\n\n" +
        "Preferred fix: enter a CivitAI API key in Settings > CivitAI\n" +
        "(generate one at civitai.com > Account Settings > API Keys).\n\n" +
        "Alternatively, refresh your cookies:\n" +
        "1. Open Chrome and log into civitai.com\n" +
        "2. Export cookies using 'Get cookies.txt LOCALLY' extension\n" +
        "3. Save the cookies file in the Civitai Collections Scraper folder\n" +
        "4. Try again.";

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DiffusionToolkit", "Civitai", "posts_cache.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CivitaiPostsCache? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var json = File.ReadAllText(CachePath);
            var cache = JsonSerializer.Deserialize<CivitaiPostsCache>(json, JsonOptions);
            // A cache without a username was written by a fetch that couldn't
            // resolve the account - its posts are the sitewide feed, not the
            // user's. Treat it as absent so garbage never reaches the calendar.
            if (cache != null && string.IsNullOrWhiteSpace(cache.Username))
            {
                Logger.Log("CivitaiPostsService: discarding cache with no username (bad fetch)");
                return null;
            }
            return cache;
        }
        catch (Exception e)
        {
            Logger.Log($"CivitaiPostsService: failed to load cache: {e.Message}");
            return null;
        }
    }

    /// <summary>The earliest date history can be recovered from: CivitAI's founding.</summary>
    public static readonly DateTime CivitaiFounding = new(2022, 11, 1);

    /// <summary>
    /// Quick refresh: fetches the current month through +3 months, looking for
    /// newly drafted/scheduled future posts. Incremental — keeps existing history
    /// in the cache and only re-scans recent + future posts.
    /// </summary>
    public Task<PythonResult> FetchUpcomingAsync(Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var firstOfMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        return RunPythonAsync($"main.py posts --from {firstOfMonth:yyyy-MM-dd}", onProgress, cancellationToken);
    }

    /// <summary>
    /// Full history recovery from <paramref name="fromDate"/> (clamped to CivitAI's
    /// founding) through +3 months. Replaces the cache wholesale.
    /// </summary>
    public Task<PythonResult> RecoverHistoryAsync(DateTime fromDate, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (fromDate < CivitaiFounding) fromDate = CivitaiFounding;
        return RunPythonAsync($"main.py posts --full --from {fromDate:yyyy-MM-dd}", onProgress, cancellationToken);
    }

    /// <summary>
    /// Creates a scheduled post on CivitAI for a local image file.
    /// publishAtLocal is converted to an ISO 8601 string with local offset.
    /// </summary>
    public Task<PythonResult> SchedulePostAsync(string filePath, DateTime publishAtLocal, string? title)
    {
        return SchedulePostAsync(new[] { filePath }, publishAtLocal, title);
    }

    /// <summary>
    /// Creates a single scheduled post on CivitAI containing every file in
    /// <paramref name="filePaths"/> (in order). publishAtLocal is converted to
    /// an ISO 8601 string with local offset.
    /// </summary>
    public Task<PythonResult> SchedulePostAsync(IEnumerable<string> filePaths, DateTime publishAtLocal, string? title,
        Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var iso = new DateTimeOffset(publishAtLocal).ToString("yyyy-MM-dd'T'HH:mm:sszzz");
        var arguments = new StringBuilder("main.py schedule-post");
        foreach (var filePath in filePaths)
        {
            arguments.Append($" --file \"{filePath}\"");
        }
        arguments.Append($" --publish-at \"{iso}\"");
        if (!string.IsNullOrWhiteSpace(title))
        {
            arguments.Append($" --title \"{title.Replace("\"", "'")}\"");
        }
        return RunPythonAsync(arguments.ToString(), onProgress, cancellationToken);
    }

    private async Task<PythonResult> RunPythonAsync(string arguments, Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var scriptsBasePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Diffusion.PyScripts", "Civitai Collections Scraper");
        var pythonPath = Path.Combine(scriptsBasePath, ".venv", "Scripts", "python.exe");
        var mainPyPath = Path.Combine(scriptsBasePath, "main.py");

        if (!File.Exists(pythonPath))
        {
            return PythonResult.Failure("error", $"Python executable not found at:\n{pythonPath}");
        }
        if (!File.Exists(mainPyPath))
        {
            return PythonResult.Failure("error", $"main.py not found at:\n{mainPyPath}");
        }

        return await Task.Run(() =>
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = arguments,
                WorkingDirectory = scriptsBasePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            // API key via environment only - never on the command line (logged).
            var civitaiApiKey = ServiceLocator.Settings?.GetCivitaiApiKey();
            if (!string.IsNullOrWhiteSpace(civitaiApiKey))
            {
                processInfo.EnvironmentVariables["CIVITAI_API_KEY"] = civitaiApiKey;
            }

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                return PythonResult.Failure("error", "Failed to start the CivitAI script.");
            }

            // Cancellation kills the process tree; the read loop then sees EOF
            // and unwinds naturally.
            using var cancelRegistration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { /* already exited */ }
            });

            // Drain stderr on a background thread so a full stderr buffer can't
            // deadlock the stdout read loop below.
            var stderrBuilder = new StringBuilder();
            var stderrTask = Task.Run(() =>
            {
                string? line;
                while ((line = process.StandardError.ReadLine()) != null)
                {
                    stderrBuilder.AppendLine(line);
                }
            });

            // stdout is a stream of JSON lines: {"progress":"…"} updates arrive
            // live; the last non-progress line is the result envelope.
            string? envelope = null;
            string? outLine;
            while ((outLine = process.StandardOutput.ReadLine()) != null)
            {
                var trimmed = outLine.Trim();
                if (trimmed.Length == 0) continue;
                if (TryReadProgress(trimmed, out var message))
                {
                    onProgress?.Invoke(message);
                }
                else
                {
                    envelope = trimmed;
                }
            }

            process.WaitForExit();
            stderrTask.Wait(2000);

            if (cancellationToken.IsCancellationRequested)
            {
                Logger.Log($"CivitaiPostsService: '{arguments}' canceled by user");
                return PythonResult.Failure("canceled", "Canceled.");
            }

            var stderr = stderrBuilder.ToString();
            Logger.Log($"CivitaiPostsService: '{arguments}' exited with code {process.ExitCode}");
            if (!string.IsNullOrEmpty(stderr))
            {
                Logger.Log($"CivitaiPostsService: stderr (last 500 chars): {stderr[Math.Max(0, stderr.Length - 500)..]}");
            }

            return PythonResult.FromStdout(envelope, process.ExitCode);
        });
    }

    public const string PostedAlbumName = "Posted";

    /// <summary>Subfolder of the first root folder that receives downloads.</summary>
    public const string PostedFolderName = "Posted";

    // CivitAI's public image CDN prefix (present in every civitai image URL).
    private const string CdnPrefix = "https://image.civitai.com/xG1nkqKTMzGDvpLrqFT7WA";

    /// <summary>Small CDN preview URL for a CivitAI image (its Url field is the CDN key).</summary>
    public static string CdnThumbnailUrl(string cdnKey, string? name, int width = 96) =>
        $"{CdnPrefix}/{cdnKey}/width={width}/{Uri.EscapeDataString(string.IsNullOrWhiteSpace(name) ? "image.jpeg" : name)}";

    private static readonly System.Net.Http.HttpClient DownloadClient = CreateDownloadClient();

    private static System.Net.Http.HttpClient CreateDownloadClient()
    {
        var client = new System.Net.Http.HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        client.Timeout = TimeSpan.FromMinutes(5);
        return client;
    }

    /// <summary>
    /// Downloads posted CivitAI images that have no local match into
    /// "&lt;first root folder&gt;\Posted". After the app rescans its folders,
    /// the next calendar refresh matches them and adds them to the Posted
    /// album automatically.
    /// </summary>
    public async Task<(int downloaded, int skipped, int failed, string? folder)> DownloadMissingAsync(
        List<ResolvedPostImage> resolved, Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var root = ServiceLocator.FolderService?.RootFolders?.FirstOrDefault()?.Path;
        if (root == null || !Directory.Exists(root))
        {
            return (0, 0, 0, null);
        }
        var folder = Path.Combine(root, PostedFolderName);
        Directory.CreateDirectory(folder);

        // One download per CivitAI image id; needs the CDN key (Url) and a name.
        var targets = resolved
            .Where(r => r.Status == MatchStatus.Unmatched
                        && !string.IsNullOrWhiteSpace(r.Url)
                        && !string.IsNullOrWhiteSpace(r.Name))
            .GroupBy(r => r.CivitaiImageId)
            .Select(g => g.First())
            .ToList();

        int downloaded = 0, skipped = 0, failed = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = targets[i];
            onProgress?.Invoke($"Downloading missing images: {i + 1} of {targets.Count}…");

            var fileName = SanitizeFileName(image.Name!);
            if (!Path.HasExtension(fileName)) fileName += ".jpeg";
            var target = Path.Combine(folder, fileName);
            if (File.Exists(target))
            {
                skipped++;
                continue;
            }

            try
            {
                // original=true returns the file as uploaded (original format).
                var url = $"{CdnPrefix}/{image.Url}/original=true/{Uri.EscapeDataString(fileName)}";
                var bytes = await DownloadClient.GetByteArrayAsync(url, cancellationToken);
                await File.WriteAllBytesAsync(target, bytes, cancellationToken);
                downloaded++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Logger.Log($"CivitaiPostsService: download failed for image {image.CivitaiImageId} ({image.Name}): {e.Message}");
                failed++;
            }
        }

        return (downloaded, skipped, failed, folder);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    /// <summary>
    /// Assigns every matched local image to the "Posted" album (created on
    /// demand). AddImagesToAlbum uses INSERT OR IGNORE, so images already in
    /// the album are untouched. Returns the number of matched images.
    /// </summary>
    public int MarkMatchedAsPosted(List<ResolvedPostImage> resolved)
    {
        return MarkImagesAsPosted(resolved
            .Where(r => r.Status == MatchStatus.Matched && r.LocalImageId.HasValue)
            .Select(r => r.LocalImageId!.Value));
    }

    /// <summary>
    /// Adds the given library images to the "Posted" album (created on demand).
    /// Used both for matched calendar images and for images the user just
    /// scheduled from the Schedule tab. Returns the number of images tagged.
    /// </summary>
    public int MarkImagesAsPosted(IEnumerable<int> imageIds)
    {
        try
        {
            var dataStore = ServiceLocator.DataStore;
            if (dataStore == null) return 0;

            var ids = imageIds.Distinct().ToList();
            if (ids.Count == 0) return 0;

            var album = dataStore.GetAlbumByName(PostedAlbumName)
                        ?? dataStore.CreateAlbum(new Album { Name = PostedAlbumName });
            dataStore.AddImagesToAlbum(album.Id, ids);
            return ids.Count;
        }
        catch (Exception e)
        {
            Logger.Log($"CivitaiPostsService: Posted album tagging failed: {e.Message}");
            return 0;
        }
    }

    /// <summary>
    /// True if <paramref name="jsonLine"/> is a {"progress":"…"} update, in which
    /// case <paramref name="message"/> holds the text. False for result envelopes
    /// and anything that doesn't parse.
    /// </summary>
    private static bool TryReadProgress(string jsonLine, out string message)
    {
        message = string.Empty;
        if (jsonLine.Length == 0 || jsonLine[0] != '{') return false;
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            if (doc.RootElement.TryGetProperty("progress", out var p))
            {
                message = p.GetString() ?? string.Empty;
                return true;
            }
        }
        catch (JsonException)
        {
            // Not JSON (a stray print) — ignore; the real envelope is valid JSON.
        }
        return false;
    }

    /// <summary>
    /// Matches every CivitAI image in the cache to local library files:
    /// exact filename (extension-tolerant) → width/height filter →
    /// UserMetadata SourceUrl containing /images/{id} → Ambiguous/Unmatched.
    /// </summary>
    public List<ResolvedPostImage> ResolveMatches(CivitaiPostsCache cache)
    {
        var resolved = new List<ResolvedPostImage>();
        var dataStore = ServiceLocator.DataStore;
        if (dataStore == null || cache.Posts == null) return resolved;

        var allNames = cache.Posts
            .SelectMany(p => p.Images ?? new List<CivitaiPostImage>())
            .Select(i => i.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var matchesByName = dataStore.GetImagesByFileNames(allNames!)
            .GroupBy(m => Path.GetFileNameWithoutExtension(m.FileName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var post in cache.Posts)
        {
            var publishedAtUtc = post.PublishedAtUtc;
            foreach (var image in post.Images ?? new List<CivitaiPostImage>())
            {
                var item = new ResolvedPostImage
                {
                    CivitaiImageId = image.Id,
                    Name = image.Name,
                    Url = image.Url,
                    Width = image.Width,
                    Height = image.Height,
                    PostId = post.PostId,
                    Title = post.Title,
                    PublishedAtUtc = publishedAtUtc,
                    Scheduled = post.Scheduled,
                };

                var candidates = FindCandidates(matchesByName, image);
                if (candidates.Count == 1)
                {
                    item.Status = MatchStatus.Matched;
                    item.LocalImageId = candidates[0].Id;
                    item.LocalPath = candidates[0].Path;
                }
                else if (candidates.Count > 1)
                {
                    var best = TieBreakByUserMetadata(dataStore, candidates, image.Id);
                    if (best != null)
                    {
                        item.Status = MatchStatus.Matched;
                        item.LocalImageId = best.Id;
                        item.LocalPath = best.Path;
                    }
                    else
                    {
                        item.Status = MatchStatus.Ambiguous;
                        item.Candidates = candidates;
                    }
                }
                else
                {
                    item.Status = MatchStatus.Unmatched;
                }

                resolved.Add(item);
            }
        }

        return resolved;
    }

    private static List<ImageFileMatch> FindCandidates(
        Dictionary<string, List<ImageFileMatch>> matchesByName, CivitaiPostImage image)
    {
        if (string.IsNullOrWhiteSpace(image.Name)) return new List<ImageFileMatch>();

        var stem = Path.GetFileNameWithoutExtension(image.Name);
        if (!matchesByName.TryGetValue(stem, out var candidates))
        {
            return new List<ImageFileMatch>();
        }

        // Prefer exact filename (with extension) over stem-only matches.
        var exact = candidates.Where(c => string.Equals(c.FileName, image.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = exact.Count > 0 ? exact : candidates;

        // Dimension filter when CivitAI reports dimensions.
        if (image.Width is > 0 && image.Height is > 0)
        {
            var sized = pool.Where(c => c.Width == image.Width && c.Height == image.Height).ToList();
            if (sized.Count > 0) return sized;
        }
        return pool.ToList();
    }

    private static ImageFileMatch? TieBreakByUserMetadata(
        DataStore dataStore, List<ImageFileMatch> candidates, long civitaiImageId)
    {
        var marker = $"/images/{civitaiImageId}";
        foreach (var candidate in candidates.Where(c => !string.IsNullOrEmpty(c.Hash)))
        {
            try
            {
                if (dataStore.GetUserMetadata(candidate.Hash!)
                    .Any(m => m.SourceUrl != null && m.SourceUrl.Contains(marker)))
                {
                    return candidate;
                }
            }
            catch
            {
                // Tie-break is best-effort only.
            }
        }
        return null;
    }
}

public class PythonResult
{
    public bool Success { get; init; }
    public string? ErrorCategory { get; init; }   // "auth" | "upload" | "schedule" | "fetch" | "error"
    public string? Message { get; init; }
    public JsonElement? Payload { get; init; }

    public bool IsAuthFailure => ErrorCategory == "auth";
    public bool IsCanceled => ErrorCategory == "canceled";

    public static PythonResult Failure(string category, string message) =>
        new() { Success = false, ErrorCategory = category, Message = message };

    public static PythonResult FromStdout(string? stdout, int exitCode)
    {
        var json = stdout?.Trim();
        if (string.IsNullOrEmpty(json))
        {
            return Failure("error", $"The CivitAI script produced no output (exit code {exitCode}).");
        }
        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone();
            if (root.TryGetProperty("error", out var error))
            {
                var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                return new PythonResult
                {
                    Success = false,
                    ErrorCategory = error.GetString(),
                    Message = message,
                    Payload = root
                };
            }
            return new PythonResult { Success = true, Payload = root };
        }
        catch (JsonException)
        {
            return Failure("error", $"Unexpected script output (exit code {exitCode}): {json[..Math.Min(json.Length, 300)]}");
        }
    }
}

public class CivitaiPostsCache
{
    public int Version { get; set; }
    public string? GeneratedAt { get; set; }
    public string? Username { get; set; }
    public string? RangeFrom { get; set; }
    public string? RangeTo { get; set; }
    public List<CivitaiPost> Posts { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public class CivitaiPost
{
    public long PostId { get; set; }
    public string? PublishedAt { get; set; }
    public bool Scheduled { get; set; }
    public string? Title { get; set; }
    public List<CivitaiPostImage> Images { get; set; } = new();

    [JsonIgnore]
    public DateTime PublishedAtUtc
    {
        get
        {
            if (DateTimeOffset.TryParse(PublishedAt, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var dto))
            {
                return dto.UtcDateTime;
            }
            return DateTime.MinValue;
        }
    }
}

public class CivitaiPostImage
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Url { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? NsfwLevel { get; set; }
}

public enum MatchStatus
{
    Matched,
    Ambiguous,
    Unmatched
}

public class ResolvedPostImage : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    // Lazily-loaded thumbnail shared by the month cells and day-view rows.
    private System.Windows.Media.ImageSource? _thumbnail;
    public System.Windows.Media.ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    /// <summary>True while a local thumbnail load is in flight, so rebuilds don't queue duplicates.</summary>
    public bool ThumbnailPending { get; set; }

    // Highlights the day-view row whose image is shown in the preview panel.
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public long CivitaiImageId { get; set; }
    public string? Name { get; set; }
    public string? Url { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long PostId { get; set; }
    public string? Title { get; set; }
    public DateTime PublishedAtUtc { get; set; }
    public bool Scheduled { get; set; }
    public MatchStatus Status { get; set; }
    public int? LocalImageId { get; set; }
    public string? LocalPath { get; set; }
    public List<ImageFileMatch> Candidates { get; set; } = new();

    public bool IsAmbiguous => Status == MatchStatus.Ambiguous;
    public bool IsUnmatched => Status == MatchStatus.Unmatched;

    /// <summary>
    /// True only while the post is still waiting to go live: the queue marker
    /// is meaningless once the scheduled time has passed and the post is
    /// publicly visible on CivitAI.
    /// </summary>
    public bool IsQueued => Scheduled && PublishedAtUtc > DateTime.UtcNow;
    public string CivitaiImageUrl => $"https://civitai.com/images/{CivitaiImageId}";
    public string CivitaiPostUrl => $"https://civitai.com/posts/{PostId}";
}
