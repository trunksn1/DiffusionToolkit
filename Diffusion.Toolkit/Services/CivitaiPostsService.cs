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
        "Preferred fix: click 'Connect CivitAI Account' in Settings > CivitAI\n" +
        "and sign in through your browser.\n\n" +
        "Alternatively, enter a CivitAI API key in Settings > CivitAI\n" +
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
    /// How far ahead CivitAI accepts a scheduled publish date, taken from its
    /// own SchedulePostModal: <c>dayjs(now).add(3, 'month')</c>. Three CALENDAR
    /// months, not 90 days — the two differ by up to two days depending on the
    /// month, and a date past the ceiling is rejected by the form.
    /// </summary>
    public const int MaxScheduleMonthsAhead = 3;

    /// <summary>
    /// The floor CivitAI puts under a scheduled publish date
    /// (<c>POST_MINIMUM_SCHEDULE_MINUTES</c> in its constants): a post must be
    /// at least an hour out. "In the future" is not enough.
    /// </summary>
    public const int MinScheduleMinutesAhead = 60;

    /// <summary>The last date a post may be scheduled for, in local time.</summary>
    public static DateTime LastSchedulableDate => DateTime.Today.AddMonths(MaxScheduleMonthsAhead);

    /// <summary>The earliest instant a post may be scheduled for, in local time.</summary>
    public static DateTime EarliestSchedulableTime => DateTime.Now.AddMinutes(MinScheduleMinutesAhead);

    /// <summary>Shared wording for the ceiling, quoted by every schedule picker.</summary>
    public static string ScheduleLimitMessage =>
        $"CivitAI only accepts posts scheduled up to {MaxScheduleMonthsAhead} months ahead " +
        $"(through {LastSchedulableDate:d}).";

    /// <summary>Shared wording for the floor.</summary>
    public static string ScheduleFloorMessage =>
        $"CivitAI requires a scheduled post to be at least {MinScheduleMinutesAhead} minutes " +
        $"in the future (from {EarliestSchedulableTime:t}).";

    /// <summary>
    /// Fetches posts published from <paramref name="fromDate"/> onwards (plus
    /// the future queue, which is always fetched whole) into the cache.
    ///
    /// <paramref name="withEngagement"/> adds --image-stats: one extra request
    /// per post in range, and the only source of Buzz tips, view counts, and
    /// per-image (rather than post-wide) reaction/comment/collection numbers.
    /// It is opt-in because over a long range it dominates the cost.
    ///
    /// <paramref name="rebuild"/> discards the cached history instead of
    /// merging into it — the escape hatch for a cache that looks wrong.
    /// </summary>
    public Task<PythonResult> FetchPostsAsync(DateTime fromDate, bool withEngagement, bool rebuild,
        Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (fromDate < CivitaiFounding) fromDate = CivitaiFounding;
        var arguments = new StringBuilder($"main.py posts --from {fromDate:yyyy-MM-dd}");
        if (rebuild) arguments.Append(" --full");
        if (withEngagement) arguments.Append(" --image-stats");
        arguments.Append(UsernameArgument());
        return RunPythonAsync(arguments.ToString(), onProgress, cancellationToken);
    }

    /// <summary>
    /// Quick refresh used after scheduling a post from inside the app: current
    /// month onwards, with engagement, so the new post appears immediately.
    /// Bounded to a month or so, which is what makes --image-stats affordable.
    /// </summary>
    public Task<PythonResult> FetchUpcomingAsync(Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var firstOfMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        return FetchPostsAsync(firstOfMonth, withEngagement: true, rebuild: false,
            onProgress, cancellationToken);
    }

    /// <summary>
    /// " --username x" for the signed-in account, or empty.
    ///
    /// Python resolves the username itself via REST /v1/users/me and the
    /// NextAuth session endpoint, neither of which accepts an OAuth token - so
    /// a user who signed in with OAuth and has no API key would fail resolution
    /// and abort the whole fetch. We already know the name from /userinfo, so
    /// hand it over. A username is not a credential, so unlike the token it is
    /// safe on the command line (arguments are written to the log).
    /// </summary>
    private static string UsernameArgument()
    {
        var username = ServiceLocator.CivitaiOAuthService?.ConnectedUsername;
        if (string.IsNullOrWhiteSpace(username)) return "";
        // Defensive: a name with a quote would break argument parsing.
        return $" --username \"{username.Replace("\"", "")}\"";
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
        return RunPythonAsync(BuildPostArguments(filePaths, $" --publish-at \"{iso}\"", title),
            onProgress, cancellationToken);
    }

    /// <summary>
    /// Creates a post that goes live immediately, containing every file in
    /// <paramref name="filePaths"/> (in order).
    ///
    /// Same create/upload/attach/publish pipeline as scheduling — only the
    /// publish date differs — so it needs no browser window and reports real
    /// progress and errors.
    /// </summary>
    public Task<PythonResult> PostNowAsync(IEnumerable<string> filePaths, string? title,
        Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        return RunPythonAsync(BuildPostArguments(filePaths, " --publish-now", title),
            onProgress, cancellationToken);
    }

    private static string BuildPostArguments(IEnumerable<string> filePaths, string whenArgument, string? title)
    {
        var arguments = new StringBuilder("main.py schedule-post");
        foreach (var filePath in filePaths)
        {
            arguments.Append($" --file \"{filePath}\"");
        }
        arguments.Append(whenArgument);
        if (!string.IsNullOrWhiteSpace(title))
        {
            arguments.Append($" --title \"{title.Replace("\"", "'")}\"");
        }
        return arguments.ToString();
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

        // Awaited outside Task.Run: renewal is a network call, and the service
        // serializes it so concurrent calendar operations cannot race the
        // refresh-token rotation.
        var accessToken = ServiceLocator.CivitaiOAuthService == null
            ? null
            : await ServiceLocator.CivitaiOAuthService.TryGetAccessTokenAsync(cancellationToken);

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

            // Credentials via environment only - never on the command line (logged).
            var civitaiApiKey = ServiceLocator.Settings?.GetCivitaiApiKey();
            if (!string.IsNullOrWhiteSpace(civitaiApiKey))
            {
                processInfo.EnvironmentVariables["CIVITAI_API_KEY"] = civitaiApiKey;
            }
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                processInfo.EnvironmentVariables["CIVITAI_ACCESS_TOKEN"] = accessToken;
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

    /// <summary>
    /// The filename stem used for an image CivitAI kept no name for.
    ///
    /// Some ingestion paths discard the uploaded filename — verified 2026-08-05
    /// against a post submitted through a challenge page, whose images come back
    /// with name=null and an empty metadata block from every endpoint, while an
    /// ordinary post from the same account keeps both. Filename is all the
    /// matcher has, so those images can never be matched to the file that was
    /// uploaded. Downloading is the way out, and the download must land on a
    /// name the matcher can find again — hence one deterministic stem, used by
    /// both <see cref="DownloadMissingAsync"/> and <see cref="ResolveMatches"/>.
    /// </summary>
    public static string NamelessStem(long civitaiImageId) => $"civitai-{civitaiImageId}";

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

        // One download per CivitAI image id. Only the CDN key (Url) is required:
        // CivitAI does not always store a filename, and those nameless images are
        // exactly the ones that can never match a local file — excluding them here
        // would tell the user to press a button that cannot help them.
        var targets = resolved
            .Where(r => r.Status == MatchStatus.Unmatched
                        && !string.IsNullOrWhiteSpace(r.Url))
            .GroupBy(r => r.CivitaiImageId)
            .Select(g => g.First())
            .ToList();

        int downloaded = 0, skipped = 0, failed = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = targets[i];
            onProgress?.Invoke($"Downloading missing images: {i + 1} of {targets.Count}…");

            // Nameless images get a stable, identifiable name from their CivitAI
            // id — the same stem ResolveMatches looks for, so the copy matches
            // once the library has been rescanned.
            var named = !string.IsNullOrWhiteSpace(image.Name);
            var fileName = SanitizeFileName(named ? image.Name! : NamelessStem(image.CivitaiImageId));

            // With a name we know the extension up front and can skip an
            // already-downloaded file without touching the network. Without one
            // the extension comes from the response, so the existence check
            // happens after the request instead.
            if (named && !Path.HasExtension(fileName)) fileName += ".jpeg";
            if (named && File.Exists(Path.Combine(folder, fileName)))
            {
                skipped++;
                continue;
            }

            try
            {
                // original=true returns the file as uploaded (original format).
                var url = $"{CdnPrefix}/{image.Url}/original=true/{Uri.EscapeDataString(fileName)}";
                using var response = await DownloadClient.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();

                if (!Path.HasExtension(fileName))
                {
                    fileName += ExtensionFor(response.Content.Headers.ContentType?.MediaType);
                }
                var target = Path.Combine(folder, fileName);
                if (File.Exists(target))
                {
                    skipped++;
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
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

    /// <summary>
    /// File extension for a CDN response's media type. CivitAI posts can hold
    /// video as well as stills, so this is not jpeg-only.
    /// </summary>
    private static string ExtensionFor(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpeg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        _ => ".jpeg"
    };

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
    ///
    /// Images CivitAI kept no name for are looked up under
    /// <see cref="NamelessStem"/> instead, which finds the copy the Download
    /// step wrote for them.
    /// </summary>
    public List<ResolvedPostImage> ResolveMatches(CivitaiPostsCache cache)
    {
        var resolved = new List<ResolvedPostImage>();
        var dataStore = ServiceLocator.DataStore;
        if (dataStore == null || cache.Posts == null) return resolved;

        var allNames = cache.Posts
            .SelectMany(p => p.Images ?? new List<CivitaiPostImage>())
            .Select(MatchNameFor)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var matchesByName = dataStore.GetImagesByFileNames(allNames)
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
                    Stats = image.Stats,
                    StatsScope = image.StatsScope,
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

    /// <summary>
    /// The filename this image is matched under: its own, or the deterministic
    /// stem a nameless image's downloaded copy carries.
    /// </summary>
    private static string MatchNameFor(CivitaiPostImage image) =>
        string.IsNullOrWhiteSpace(image.Name) ? NamelessStem(image.Id) : image.Name!;

    private static List<ImageFileMatch> FindCandidates(
        Dictionary<string, List<ImageFileMatch>> matchesByName, CivitaiPostImage image)
    {
        var matchName = MatchNameFor(image);
        var stem = Path.GetFileNameWithoutExtension(matchName);
        if (!matchesByName.TryGetValue(stem, out var candidates))
        {
            return new List<ImageFileMatch>();
        }

        // Prefer exact filename (with extension) over stem-only matches.
        var exact = candidates.Where(c => string.Equals(c.FileName, matchName, StringComparison.OrdinalIgnoreCase)).ToList();
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

    /// <summary>
    /// Engagement counters, or null when the fetch did not report any (an
    /// unpublished post, or an entry carried over from a version 1 cache).
    /// Null means "unknown", never "zero".
    /// </summary>
    public CivitaiImageStats? Stats { get; set; }

    /// <summary>
    /// "image" when the counters are this image's own (and include tips and
    /// views), "post" when they are the post's totals shared by every image in
    /// it. Surfaced in the UI so shared numbers aren't read as per-image ones.
    /// </summary>
    public string? StatsScope { get; set; }
}

/// <summary>
/// Per-image engagement counters as reported by CivitAI. Every counter is
/// nullable: the API's stats block is not contractual, so a missing counter
/// is unknown rather than zero and the UI hides it instead of showing a 0.
/// </summary>
public class CivitaiImageStats
{
    public int? LikeCount { get; set; }
    public int? DislikeCount { get; set; }
    public int? HeartCount { get; set; }
    public int? LaughCount { get; set; }
    public int? CryCount { get; set; }
    public int? CommentCount { get; set; }
    public int? CollectedCount { get; set; }

    /// <summary>Buzz tipped on the image.</summary>
    public int? TippedAmountCount { get; set; }

    public int? ViewCount { get; set; }

    /// <summary>
    /// All reactions added up. Dislikes are counted: they are reactions, and
    /// leaving them out would make the compact total disagree with the
    /// breakdown next to it.
    /// </summary>
    public int TotalReactions =>
        (LikeCount ?? 0) + (DislikeCount ?? 0) + (HeartCount ?? 0)
        + (LaughCount ?? 0) + (CryCount ?? 0);
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
    /// CivitAI did not store a filename for this image — the usual cause is a
    /// post submitted through a challenge page, which drops the uploaded name.
    /// Filename is the only thing the matcher has, so the file you actually
    /// uploaded can never be recognised; the downloaded copy can, because it is
    /// written under <see cref="CivitaiPostsService.NamelessStem"/>.
    /// </summary>
    public bool HasNoName => string.IsNullOrWhiteSpace(Name);

    /// <summary>Shown only while a nameless image is still unmatched.</summary>
    public bool NeedsNamelessDownload => HasNoName && IsUnmatched;

    public string NamelessHint =>
        "CivitAI kept no filename for this image (posts submitted through a challenge page "
        + "lose it), so your original file cannot be recognised. Download it and rescan — "
        + $"the copy is saved as {CivitaiPostsService.NamelessStem(CivitaiImageId)} and matches from then on.";

    // --- Engagement -------------------------------------------------------
    //
    // Pre-formatted for the calendar templates: the chips are hidden when a
    // counter is unknown or zero, so a post with two hearts shows two hearts
    // rather than a row of zeroes. Everything is a snapshot from the last
    // fetch, not live - the panel's "Last updated" line is the caveat.

    public CivitaiImageStats? Stats { get; set; }
    public string? StatsScope { get; set; }

    /// <summary>False when the fetch reported no counters at all (unknown, not zero).</summary>
    public bool HasStats => Stats != null;

    /// <summary>True when the numbers are the post's totals, not this image's own.</summary>
    public bool StatsArePostWide => StatsScope == "post";

    /// <summary>
    /// True when the counters came from the per-image fetch, which is the only
    /// one that reports Buzz and views. A zero from it is a real zero, so those
    /// chips can be shown at 0 instead of hidden — with post-wide totals a
    /// missing counter and an untipped image look identical, and only hiding
    /// them tells the truth.
    /// </summary>
    public bool StatsAreImageWide => HasStats && !StatsArePostWide;

    public string StatsTooltip => StatsArePostWide
        ? "Totals for the whole post, shared by every image in it. Buzz and views are "
          + "missing entirely — tick 'Engagement counters' in ⬇ Download to fetch the "
          + "per-image numbers."
        : "Counts for this image.";

    /// <summary>Compact summary for the day list: total reactions.</summary>
    public string ReactionSummary => (Stats?.TotalReactions ?? 0).ToString();
    public bool HasReactions => (Stats?.TotalReactions ?? 0) > 0;

    public string CommentSummary => (Stats?.CommentCount ?? 0).ToString();
    public bool HasComments => (Stats?.CommentCount ?? 0) > 0;

    public string CollectedSummary => (Stats?.CollectedCount ?? 0).ToString();

    /// <summary>Buzz tipped, as shown in the preview panel's chip row.</summary>
    public string TipSummary => (Stats?.TippedAmountCount ?? 0).ToString();

    // Show-or-hide for the counters the user asked to always see: present at a
    // real zero once the per-image numbers are in, absent while all we have is
    // the post-wide block that cannot report them honestly.
    public bool ShowComments => HasComments || StatsAreImageWide;
    public bool ShowCollected => HasCollected || StatsAreImageWide;
    public bool ShowTips => HasTips || StatsAreImageWide;

    public string LikeText => $"👍 {Stats?.LikeCount ?? 0}";
    public bool HasLikes => (Stats?.LikeCount ?? 0) > 0;
    public string DislikeText => $"👎 {Stats?.DislikeCount ?? 0}";
    public bool HasDislikes => (Stats?.DislikeCount ?? 0) > 0;
    public string HeartText => $"❤ {Stats?.HeartCount ?? 0}";
    public bool HasHearts => (Stats?.HeartCount ?? 0) > 0;
    public string LaughText => $"😂 {Stats?.LaughCount ?? 0}";
    public bool HasLaughs => (Stats?.LaughCount ?? 0) > 0;
    public string CryText => $"😢 {Stats?.CryCount ?? 0}";
    public bool HasCries => (Stats?.CryCount ?? 0) > 0;
    public string CommentText => $"💬 {Stats?.CommentCount ?? 0}";
    public string CollectedText => $"🗂 {Stats?.CollectedCount ?? 0}";
    public bool HasCollected => (Stats?.CollectedCount ?? 0) > 0;
    public string TippedText => $"⚡ {Stats?.TippedAmountCount ?? 0}";
    public bool HasTips => (Stats?.TippedAmountCount ?? 0) > 0;
    public string ViewText => $"👁 {Stats?.ViewCount ?? 0}";
    public bool HasViews => (Stats?.ViewCount ?? 0) > 0;

    /// <summary>
    /// True when stats were reported but nothing at all would be rendered —
    /// worth saying explicitly so an untouched post doesn't look like a fetch
    /// that failed. Once per-image numbers are in, the zeroed chips say it
    /// themselves and this stays false.
    /// </summary>
    public bool HasNoEngagement => HasStats && !HasReactions && !ShowComments
                                   && !ShowCollected && !ShowTips && !HasViews;

    /// <summary>
    /// True only while the post is still waiting to go live: the queue marker
    /// is meaningless once the scheduled time has passed and the post is
    /// publicly visible on CivitAI.
    /// </summary>
    public bool IsQueued => Scheduled && PublishedAtUtc > DateTime.UtcNow;
    public string CivitaiImageUrl => $"https://civitai.com/images/{CivitaiImageId}";
    public string CivitaiPostUrl => $"https://civitai.com/posts/{PostId}";
}
