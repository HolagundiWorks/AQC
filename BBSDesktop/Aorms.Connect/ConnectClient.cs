// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Aorms.Connect;

public enum RestoreOutcome
{
    /// <summary>Nothing stored — show the sign-in screen.</summary>
    NoStoredSession,
    /// <summary>Signed in again silently; <see cref="ConnectClient.Session"/> is set.</summary>
    Restored,
    /// <summary>Signed in on another computer: offer "Sign in here" (<see cref="ConnectClient.TakeOverSessionAsync"/>).</summary>
    SessionReplaced,
    /// <summary>The studio isn't entitled (plan lapsed): continue in Community mode.</summary>
    NotConnected,
    /// <summary>The stored sign-in is no longer valid: show the sign-in screen.</summary>
    SignedOut,
}

/// <summary>
/// AQC connected mode client — implements docs/esti/AQC-CLIENT-SPEC.md §2 of the AORMS repo: config, token sign-in, the single
/// active session, token refresh, and error mapping. Later steps (projects, rows, versions, rate books) add typed calls on top of
/// <see cref="GetAsync{T}"/> / <see cref="PostAsync{T}"/> / <see cref="PutAsync{T}"/>, which already carry the bearer token and the
/// session header and refresh the access token when it is about to expire.
/// </summary>
public sealed class ConnectClient : IDisposable
{
    /// <summary>The wire contract this client implements. A server announcing a different one is refused ("Update AQC").</summary>
    public const string SupportedContract = "2026-10-aqc-1";
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly IConnectTokenStore _store;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private ConnectConfig? _config;
    private string? _access;
    private string? _refresh;
    private DateTimeOffset _expires;
    private string? _email;

    public ConnectClient(Uri baseUri, IConnectTokenStore store, HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = baseUri;
        _http.Timeout = TimeSpan.FromSeconds(60);
        _store = store;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The current session, or null when signed out / Community.</summary>
    public SessionInfo? Session { get; private set; }
    public ConnectConfig? Config => _config;
    public bool IsConnected => Session is { Entitlement.Connected: true };

    /// <summary>Raised (once per occurrence) when any call learns this session was taken over elsewhere. Stop syncing; keep the outbox.</summary>
    public event Action? SessionReplaced;
    /// <summary>Raised when the studio is no longer entitled to connected mode.</summary>
    public event Action<ConnectException>? NotConnected;
    /// <summary>Raised when the stored sign-in can no longer be refreshed.</summary>
    public event Action? SignedOut;

    // ── config ─────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<ConnectConfig> GetConfigAsync(CancellationToken ct = default)
    {
        if (_config is not null) return _config;
        using var res = await _http.GetAsync("api/aqc/v1/config", ct).ConfigureAwait(false);
        var cfg = await ReadAsync<ConnectConfig>(res, ct).ConfigureAwait(false);
        if (cfg.Contract != SupportedContract)
            throw new ConnectException(0, "update_required", $"This AORMS server speaks '{cfg.Contract}'; this AQC speaks '{SupportedContract}'. Update AQC.");
        return _config = cfg;
    }

    // ── sign in / session ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Password sign-in, then start the account's single active session (replacing any other computer's).</summary>
    public async Task<SessionInfo> SignInAsync(string email, string password, string clientLabel, CancellationToken ct = default)
    {
        await GetConfigAsync(ct).ConfigureAwait(false);
        var tokens = await PostRawAsync<LoginTokens>("api/aqc/v1/auth/login", new { email, password }, bearer: null, session: null, ct).ConfigureAwait(false);
        SetTokens(tokens.AccessToken, tokens.RefreshToken, DateTimeOffset.FromUnixTimeSeconds(tokens.ExpiresAt), tokens.Email);
        return await StartSessionAsync(clientLabel, ct).ConfigureAwait(false);
    }

    /// <summary>POST /session — makes this computer the account's active session.</summary>
    public async Task<SessionInfo> StartSessionAsync(string clientLabel, CancellationToken ct = default)
    {
        await EnsureFreshAsync(ct).ConfigureAwait(false);
        SessionInfo s;
        try
        {
            s = await PostRawAsync<SessionInfo>("api/aqc/v1/session", new { clientLabel }, _access, session: null, ct).ConfigureAwait(false);
        }
        catch (ConnectException e) when (e.IsNotConnected) { Session = null; NotConnected?.Invoke(e); throw; }
        Session = s;
        Persist();
        return s;
    }

    /// <summary>"Sign in here" after <see cref="RestoreOutcome.SessionReplaced"/> — same as starting a new session.</summary>
    public Task<SessionInfo> TakeOverSessionAsync(string clientLabel, CancellationToken ct = default) => StartSessionAsync(clientLabel, ct);

    /// <summary>
    /// Silent sign-in at app start from the stored refresh token. Never takes the session over: if another computer holds it the
    /// outcome is <see cref="RestoreOutcome.SessionReplaced"/> and the user decides.
    /// </summary>
    public async Task<RestoreOutcome> RestoreAsync(CancellationToken ct = default)
    {
        var stored = _store.Load();
        if (stored is null || string.IsNullOrEmpty(stored.RefreshToken)) return RestoreOutcome.NoStoredSession;
        _refresh = stored.RefreshToken; _email = stored.Email;
        try
        {
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (ConnectException) { ClearLocal(); return RestoreOutcome.SignedOut; } // RefreshAsync already raised SignedOut
        if (string.IsNullOrEmpty(stored.SessionId)) return RestoreOutcome.SessionReplaced;
        try
        {
            using var req = Build(HttpMethod.Get, "api/aqc/v1/session", body: null, _access, stored.SessionId);
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            Session = await ReadAsync<SessionInfo>(res, ct).ConfigureAwait(false);
            Persist();
            return RestoreOutcome.Restored;
        }
        catch (ConnectException e) when (e.IsSessionReplaced) { Session = null; return RestoreOutcome.SessionReplaced; }
        catch (ConnectException e) when (e.IsNotConnected) { Session = null; NotConnected?.Invoke(e); return RestoreOutcome.NotConnected; }
        catch (ConnectException e) when (e.Status == 401) { return Fail(); }

        RestoreOutcome Fail() { ClearLocal(); SignedOut?.Invoke(); return RestoreOutcome.SignedOut; }
    }

    /// <summary>Health check (also refreshes the server's last-seen). Call about once a minute while a project is open.</summary>
    public Task<SessionInfo> RefreshSessionAsync(CancellationToken ct = default) => GetAsync<SessionInfo>("api/aqc/v1/session", ct);

    public void SignOut()
    {
        ClearLocal();
        _store.Clear();
    }

    // ── authenticated calls (used by later steps) ──────────────────────────────────────────────────────────────

    public Task<T> GetAsync<T>(string path, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Get, path, null, ct);
    public Task<T> PostAsync<T>(string path, object? body, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Post, path, body, ct);
    public Task<T> PutAsync<T>(string path, object? body, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Put, path, body, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (_access is null || Session is null) throw new ConnectException(401, "missing_token", "Sign in to AORMS first.");
        await EnsureFreshAsync(ct).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var req = Build(method, path, body, _access, Session.SessionId);
                using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
                return await ReadAsync<T>(res, ct).ConfigureAwait(false);
            }
            catch (ConnectException e) when (e.Status == 401 && attempt == 0)
            {
                // The access token may have just expired: refresh once and retry.
                await RefreshAsync(ct).ConfigureAwait(false);
            }
            catch (ConnectException e) when (e.IsSessionReplaced) { Session = null; SessionReplaced?.Invoke(); throw; }
            catch (ConnectException e) when (e.IsNotConnected) { Session = null; NotConnected?.Invoke(e); throw; }
        }
    }

    // ── token refresh (Supabase's own grant; URL and key come from /config) ────────────────────────────────────

    private async Task EnsureFreshAsync(CancellationToken ct)
    {
        if (_access is not null && _now() + RefreshSkew < _expires) return;
        if (_refresh is null) return;
        await RefreshAsync(ct).ConfigureAwait(false);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cfg = await GetConfigAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(cfg.SupabaseUrl) || string.IsNullOrEmpty(cfg.SupabaseAnonKey) || _refresh is null)
                throw new ConnectException(401, "invalid_token", "Your sign-in expired. Sign in again.");
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{cfg.SupabaseUrl.TrimEnd('/')}/auth/v1/token?grant_type=refresh_token")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { refresh_token = _refresh }), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("apikey", cfg.SupabaseAnonKey);
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                ClearLocal(keepStore: true);
                SignedOut?.Invoke();
                throw new ConnectException((int)res.StatusCode, "invalid_token", "Your sign-in expired. Sign in again.");
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            var expiresAt = root.TryGetProperty("expires_at", out var ea) && ea.TryGetInt64(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : _now().AddSeconds(root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600);
            SetTokens(root.GetProperty("access_token").GetString()!, root.GetProperty("refresh_token").GetString()!, expiresAt, _email);
            Persist();
        }
        finally { _refreshLock.Release(); }
    }

    // ── plumbing ───────────────────────────────────────────────────────────────────────────────────────────────

    private void SetTokens(string access, string refresh, DateTimeOffset expires, string? email)
    {
        _access = access; _refresh = refresh; _expires = expires; _email = email ?? _email;
    }

    private void Persist()
    {
        if (_refresh is not null) _store.Save(new StoredSession(_refresh, Session?.SessionId, _email));
    }

    private void ClearLocal(bool keepStore = false)
    {
        _access = null; _refresh = null; Session = null;
        if (!keepStore) _store.Clear();
    }

    private static HttpRequestMessage Build(HttpMethod method, string path, object? body, string? bearer, string? sessionId)
    {
        var req = new HttpRequestMessage(method, path);
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (sessionId is not null) req.Headers.Add("x-aqc-session", sessionId);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return req;
    }

    private async Task<T> PostRawAsync<T>(string path, object body, string? bearer, string? session, CancellationToken ct)
    {
        using var req = Build(HttpMethod.Post, path, body, bearer, session);
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        return await ReadAsync<T>(res, ct).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var ex = ConnectException.From((int)res.StatusCode, text);
            if (res.StatusCode == HttpStatusCode.TooManyRequests && ex.RetryAfterSeconds is null && res.Headers.RetryAfter?.Delta is { } d)
                ex = new ConnectException(429, ex.Code, ex.Message, null, (int)d.TotalSeconds);
            throw ex;
        }
        return JsonSerializer.Deserialize<T>(text, Json) ?? throw new ConnectException((int)res.StatusCode, "bad_response", "AORMS sent an empty answer.");
    }

    public void Dispose()
    {
        _http.Dispose();
        _refreshLock.Dispose();
    }
}
