using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.Toolkit.Configuration;

namespace Diffusion.Toolkit.Services;

/// <summary>
/// OAuth 2.0 Authorization Code + PKCE (S256) client for CivitAI, replacing the
/// manually pasted API key for everything that runs over tRPC.
///
/// This is a public client: it ships a client ID and never a client secret,
/// which is the only safe shape for a distributed desktop application. The
/// authorization response comes back to a loopback redirect on a fixed port
/// (CivitAI matches redirect_uri exactly, so the port cannot be dynamic).
///
/// Tokens are held DPAPI-encrypted in settings; plaintext never reaches
/// settings.json. See CIVITAI_OAUTH_IMPLEMENTATION_PLAN.md for the measured
/// behavior this implementation is built on.
/// </summary>
public class CivitaiOAuthService
{
    private const string AuthBase = "https://auth.civitai.com";
    private const string AuthorizeEndpoint = AuthBase + "/api/auth/oauth/authorize";
    private const string TokenEndpoint = AuthBase + "/api/auth/oauth/token";
    private const string UserInfoEndpoint = AuthBase + "/api/auth/oauth/userinfo";

    /// <summary>
    /// Public client identifier, not a credential. Registered as a
    /// Browser / Mobile App with the loopback redirect below.
    /// </summary>
    private const string ClientId = "bbb201ea-c0f3-45ad-8e67-0c4e967d7ada";

    /// <summary>
    /// Must match the registered redirect URI exactly, so the port is fixed and
    /// cannot fall back to a free one.
    /// </summary>
    private const int CallbackPort = 8765;
    private const string CallbackPath = "/callback";
    private static string RedirectUri => $"http://127.0.0.1:{CallbackPort}{CallbackPath}";

    /// <summary>
    /// Profile &amp; Settings Read (1) | Media &amp; Posts Read (32) |
    /// Media &amp; Posts Write (64) | Collections Read (131072). Verified as
    /// granted verbatim. Sending no scope at all fails with invalid_scope, so
    /// this value is mandatory rather than a hint.
    /// </summary>
    public const int RequestedScope = 1 | 32 | 64 | 131072;

    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Refresh rotates the refresh token, so two concurrent refreshes would
    // rotate each other into invalidity. All token renewal is serialized.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Settings _settings;
    private readonly HttpClient _httpClient;

    public CivitaiOAuthService(Settings settings, HttpClient? httpClient = null)
    {
        _settings = settings;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Whether a session is stored and usable.</summary>
    public bool IsConnected => ReadSession() != null;

    /// <summary>Connected account name, or null when signed out.</summary>
    public string? ConnectedUsername => ReadSession()?.Username;

    /// <summary>
    /// Runs the full interactive sign-in: opens the system browser, waits for
    /// the loopback callback, exchanges the code, and stores the session.
    /// </summary>
    public async Task<CivitaiOAuthSession> SignInAsync(CancellationToken cancellationToken = default)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));

        // Bind before opening the browser: if the port is taken, fail now
        // rather than after the user has already approved consent.
        using var listener = new LoopbackCallbackListener(CallbackPort, CallbackPath);
        listener.Start();

        var authorizeUrl = BuildAuthorizeUrl(state, challenge);
        Logger.Log("CivitaiOAuthService: opening browser for authorization");
        OpenBrowser(authorizeUrl);

        var callback = await listener.WaitForCallbackAsync(CallbackTimeout, cancellationToken);

        if (!FixedTimeEquals(state, callback.GetValueOrDefault("state")))
        {
            throw new CivitaiOAuthException("CivitAI sign-in could not be verified. Please try again.");
        }

        if (callback.TryGetValue("error", out var error) && !string.IsNullOrEmpty(error))
        {
            throw new CivitaiOAuthException(error == "access_denied"
                ? "CivitAI sign-in was cancelled."
                : $"CivitAI sign-in failed: {Bounded(callback.GetValueOrDefault("error_description") ?? error, 180)}");
        }

        var code = callback.GetValueOrDefault("code");
        if (string.IsNullOrEmpty(code))
        {
            throw new CivitaiOAuthException("CivitAI did not return an authorization code.");
        }

        var token = await PostTokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
        }, previous: null, cancellationToken);

        var session = await AttachUserProfileAsync(token, cancellationToken);
        WriteSession(session);
        Logger.Log($"CivitaiOAuthService: signed in as {session.Username} (scope {session.Scope})");
        return session;
    }

    /// <summary>
    /// Returns a valid access token, refreshing when it is at or near expiry.
    /// Throws when no session is stored or the refresh is rejected.
    /// </summary>
    public async Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var current = ReadSession()
                ?? throw new CivitaiOAuthException("Not signed in to CivitAI.");

            if (current.ExpiresAtUtc > DateTime.UtcNow + RefreshSkew)
            {
                return current.AccessToken;
            }

            if (string.IsNullOrEmpty(current.RefreshToken))
            {
                SignOut();
                throw new CivitaiOAuthException("Your CivitAI session expired. Please sign in again.");
            }

            Logger.Log("CivitaiOAuthService: refreshing access token");
            try
            {
                var refreshed = await PostTokenRequestAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = current.RefreshToken,
                    ["client_id"] = ClientId,
                }, previous: current, cancellationToken);

                // The server rotates the refresh token, so this write is not
                // optional - losing it strands the session on the next renewal.
                WriteSession(refreshed);
                return refreshed.AccessToken;
            }
            catch (CivitaiOAuthHttpException e) when (e.StatusCode >= 400 && e.StatusCode < 500)
            {
                // The grant was revoked or the refresh token is spent; a retry
                // cannot help, so drop the session and make the user sign in.
                SignOut();
                throw;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Returns a valid access token, or null when signing in is not possible.
    /// For callers that must degrade to the API key rather than fail.
    /// </summary>
    public async Task<string?> TryGetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return ReadSession() == null ? null : await GetValidAccessTokenAsync(cancellationToken);
        }
        catch (Exception e)
        {
            Logger.Log($"CivitaiOAuthService: token unavailable ({e.Message})");
            return null;
        }
    }

    /// <summary>
    /// Clears the local session. For a public client this cannot revoke the
    /// server-side grant; that is done from CivitAI account settings.
    /// </summary>
    public void SignOut()
    {
        _settings.SetCivitaiOAuthSession(null);
        Logger.Log("CivitaiOAuthService: signed out locally");
    }

    private static string BuildAuthorizeUrl(string state, string challenge)
    {
        var query = new List<string>
        {
            "client_id=" + Uri.EscapeDataString(ClientId),
            "redirect_uri=" + Uri.EscapeDataString(RedirectUri),
            "response_type=code",
            "scope=" + RequestedScope.ToString(CultureInfo.InvariantCulture),
            "state=" + Uri.EscapeDataString(state),
            "code_challenge=" + Uri.EscapeDataString(challenge),
            "code_challenge_method=S256",
        };
        return AuthorizeEndpoint + "?" + string.Join("&", query);
    }

    private static void OpenBrowser(string url)
    {
        // UseShellExecute hands the URL to the default browser.
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async Task<CivitaiOAuthSession> PostTokenRequestAsync(
        Dictionary<string, string> form,
        CivitaiOAuthSession? previous,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = content };
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new CivitaiOAuthHttpException((int)response.StatusCode,
                $"CivitAI authentication failed: {DescribeError(body, response.StatusCode)}");
        }

        var token = JsonSerializer.Deserialize<TokenResponse>(body, JsonOptions)
            ?? throw new CivitaiOAuthException("CivitAI returned an unreadable token response.");

        if (string.IsNullOrEmpty(token.AccessToken))
        {
            throw new CivitaiOAuthException("CivitAI returned no access token.");
        }

        var refreshToken = string.IsNullOrEmpty(token.RefreshToken)
            ? previous?.RefreshToken ?? ""
            : token.RefreshToken;

        var expiresIn = Math.Clamp(token.ExpiresIn <= 0 ? 3600 : token.ExpiresIn, 60, 86_400);

        return new CivitaiOAuthSession
        {
            AccessToken = token.AccessToken,
            RefreshToken = refreshToken,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn),
            Scope = ParseScope(token.Scope) is var scope && scope != 0 ? scope : previous?.Scope ?? 0,
            UserId = previous?.UserId ?? 0,
            Username = previous?.Username ?? "",
        };
    }

    private async Task<CivitaiOAuthSession> AttachUserProfileAsync(
        CivitaiOAuthSession session, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return session;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var profile = JsonSerializer.Deserialize<UserInfoResponse>(body, JsonOptions);
            if (profile == null) return session;

            var id = profile.Id > 0 ? profile.Id
                : long.TryParse(profile.Sub, out var sub) ? sub : 0;

            session.UserId = id;
            session.Username = Bounded(
                !string.IsNullOrEmpty(profile.Username) ? profile.Username : profile.PreferredUsername ?? "", 100);
            return session;
        }
        catch (Exception e)
        {
            // Identity is a convenience for the UI and the calendar; a failure
            // here must not invalidate an otherwise good token.
            Logger.Log($"CivitaiOAuthService: could not read user profile ({e.Message})");
            return session;
        }
    }

    private CivitaiOAuthSession? ReadSession()
    {
        var json = _settings.GetCivitaiOAuthSession();
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var session = JsonSerializer.Deserialize<CivitaiOAuthSession>(json, JsonOptions);
            if (session == null || string.IsNullOrEmpty(session.AccessToken)) return null;
            return session;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void WriteSession(CivitaiOAuthSession session)
    {
        _settings.SetCivitaiOAuthSession(JsonSerializer.Serialize(session));
    }

    private static int ParseScope(object? value)
    {
        // Observed as a bare integer; tolerate the string and single-element
        // array shapes other CivitAI responses have used.
        switch (value)
        {
            case null:
                return 0;
            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.Number => element.TryGetInt32(out var number) ? number : 0,
                    JsonValueKind.String => int.TryParse(element.GetString(), out var text) ? text : 0,
                    JsonValueKind.Array when element.GetArrayLength() > 0 =>
                        element[0].ValueKind == JsonValueKind.Number && element[0].TryGetInt32(out var first)
                            ? first : 0,
                    _ => 0
                };
            default:
                return int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
        }
    }

    private static string DescribeError(string body, HttpStatusCode status)
    {
        try
        {
            var error = JsonSerializer.Deserialize<ErrorResponse>(body, JsonOptions);
            var detail = error?.ErrorDescription ?? error?.Error;
            if (!string.IsNullOrEmpty(detail)) return Bounded(detail, 180);
        }
        catch (JsonException)
        {
            // Fall through to the status code.
        }
        return $"HTTP {(int)status}";
    }

    private static bool FixedTimeEquals(string expected, string? actual)
    {
        if (actual == null) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Bounded(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= max ? value : value[..max];
    }

    private class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("token_type")] public string TokenType { get; set; } = "";
        [JsonPropertyName("scope")] public JsonElement? Scope { get; set; }
    }

    private class UserInfoResponse
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("sub")] public string? Sub { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("preferred_username")] public string? PreferredUsername { get; set; }
    }

    private class ErrorResponse
    {
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }
}

/// <summary>Stored OAuth session. Persisted DPAPI-encrypted, never in plaintext.</summary>
public class CivitaiOAuthSession
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public int Scope { get; set; }
    public long UserId { get; set; }
    public string Username { get; set; } = "";
}

public class CivitaiOAuthException : Exception
{
    public CivitaiOAuthException(string message) : base(message) { }
}

public class CivitaiOAuthHttpException : CivitaiOAuthException
{
    public int StatusCode { get; }

    public CivitaiOAuthHttpException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Minimal loopback HTTP endpoint that captures one OAuth callback.
///
/// Deliberately a raw TcpListener rather than HttpListener: HttpListener needs a
/// netsh urlacl reservation on Windows and throws "Access is denied" for
/// non-elevated processes, which is exactly how Diffusion Toolkit runs.
/// </summary>
internal sealed class LoopbackCallbackListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _callbackPath;

    public LoopbackCallbackListener(int port, string callbackPath)
    {
        _callbackPath = callbackPath;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (SocketException e)
        {
            throw new CivitaiOAuthException(
                $"Could not listen on 127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port} for the CivitAI " +
                "sign-in response. Another application is using that port. CivitAI requires this exact " +
                $"address, so it cannot be changed. ({e.SocketErrorCode})");
        }
    }

    /// <summary>
    /// Waits for the browser to hit the callback path and returns its query
    /// parameters. Unrelated requests (favicon prefetch, most commonly) are
    /// answered and ignored rather than treated as the callback.
    /// </summary>
    public async Task<Dictionary<string, string>> WaitForCallbackAsync(
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);

        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                throw new CivitaiOAuthException(
                    "Timed out waiting for the CivitAI sign-in response. Please try again.");
            }

            using (client)
            {
                var target = await ReadRequestTargetAsync(client, linked.Token);
                if (target == null) continue;

                var split = target.IndexOf('?');
                var path = split < 0 ? target : target[..split];
                if (!string.Equals(path, _callbackPath, StringComparison.Ordinal))
                {
                    await RespondAsync(client, 404, "Not found.", linked.Token);
                    continue;
                }

                var query = split < 0 ? "" : target[(split + 1)..];
                var parameters = ParseQuery(query);
                var ok = parameters.ContainsKey("code");
                await RespondAsync(client, ok ? 200 : 400,
                    ok
                        ? "Diffusion Toolkit is now connected to CivitAI. You can close this tab."
                        : "CivitAI sign-in did not complete. Return to Diffusion Toolkit for details.",
                    linked.Token);
                return parameters;
            }
        }
    }

    private static async Task<string?> ReadRequestTargetAsync(TcpClient client, CancellationToken cancellationToken)
    {
        // Only the request line is needed, and it arrives in the first packet.
        var buffer = new byte[8192];
        var stream = client.GetStream();
        var read = await stream.ReadAsync(buffer, cancellationToken);
        if (read <= 0) return null;

        var text = Encoding.ASCII.GetString(buffer, 0, read);
        var lineEnd = text.IndexOf('\r');
        var requestLine = lineEnd < 0 ? text : text[..lineEnd];
        var parts = requestLine.Split(' ');
        return parts.Length < 2 ? null : parts[1];
    }

    private static async Task RespondAsync(TcpClient client, int status, string message, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(
            "<!doctype html><meta charset=utf-8><title>Diffusion Toolkit</title>" +
            $"<body style='font:16px system-ui;padding:3rem'>{WebUtility.HtmlEncode(message)}</body>");
        var reason = status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Request";
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");

        var stream = client.GetStream();
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            if (split < 0)
            {
                result[Uri.UnescapeDataString(pair)] = "";
                continue;
            }
            var key = Uri.UnescapeDataString(pair[..split]);
            var value = Uri.UnescapeDataString(pair[(split + 1)..].Replace('+', ' '));
            result[key] = value;
        }
        return result;
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
        }
        catch (Exception)
        {
            // Nothing useful to do while tearing down a one-shot listener.
        }
    }
}
