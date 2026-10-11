// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aorms.Connect;

public enum SyncState
{
    /// <summary>Nothing waiting; last flush succeeded.</summary>
    Synced,
    /// <summary>Changes are waiting to be sent (debounce, or retrying after a network failure).</summary>
    Pending,
    /// <summary>Cannot reach AORMS; changes are kept.</summary>
    Offline,
    /// <summary>Another person holds the edit lease; editing is disabled, unsent rows are kept for review.</summary>
    ReadOnlyLeaseHeld,
    /// <summary>The role cannot write (VIEWER); editing is disabled.</summary>
    ReadOnlyRole,
    /// <summary>Stopped because the session moved to another computer; the outbox is kept.</summary>
    SessionReplaced,
    /// <summary>The sign-in ended (refresh token refused). The outbox is kept: sign in again and open a new <see cref="ProjectSync"/> over the same <see cref="Outbox"/>.</summary>
    SignedOut,
}

/// <summary>Persists unsent changes so they survive restarts (the app supplies a file-backed store; AQC can be offline for days).</summary>
public interface IOutboxStore
{
    string? Load();
    void Save(string json);
}

public sealed class InMemoryOutboxStore : IOutboxStore
{
    string? _json;
    public string? Load() => _json;
    public void Save(string json) => _json = json;
}

/// <summary>A store that keeps nothing — for short-lived outboxes (e.g. the remainder of a first push) where re-serialising on every add would be quadratic.</summary>
public sealed class NullOutboxStore : IOutboxStore
{
    public string? Load() => null;
    public void Save(string json) { }
}

/// <summary>Unsent changes keyed by (section,row_id): a later change to the same row replaces the earlier one; order is first-change order.</summary>
public sealed class Outbox
{
    readonly List<(string Section, string RowId)> _order = new();
    readonly Dictionary<(string, string), SyncRow> _rows = new();
    readonly object _gate = new();
    readonly IOutboxStore _store;

    public Outbox(IOutboxStore? store = null)
    {
        _store = store ?? new InMemoryOutboxStore();
        if (_store.Load() is { Length: > 0 } json)
            foreach (var e in JsonNode.Parse(json)!.AsArray())
            {
                var r = ProjectMapper.FromWire(JsonDocument.Parse(e!.ToJsonString()).RootElement);
                Put(r, persist: false);
            }
    }

    public int Count { get { lock (_gate) return _rows.Count; } }
    public IReadOnlyList<SyncRow> Snapshot() { lock (_gate) return _order.Select(k => _rows[k]).ToList(); }

    public void Upsert(string section, string rowId, JsonObject fields) => Put(new SyncRow(section, rowId, (JsonObject)fields.DeepClone()));
    public void Delete(string section, string rowId) => Put(new SyncRow(section, rowId, new JsonObject(), true));

    /// <summary>Adds many rows and saves once (a per-row save rewrites the whole outbox, which is quadratic for thousands of rows).</summary>
    public void UpsertMany(IEnumerable<SyncRow> rows)
    {
        lock (_gate)
        {
            foreach (var r in rows) Put(r, persist: false);
            Persist();
        }
    }

    /// <summary>Removes rows that were sent, but only if they haven't changed again since the snapshot was taken.</summary>
    public void Acknowledge(IEnumerable<SyncRow> sent)
    {
        lock (_gate)
        {
            foreach (var s in sent)
            {
                var k = (s.Section, s.RowId);
                if (_rows.TryGetValue(k, out var cur) && ReferenceEquals(cur, s)) { _rows.Remove(k); _order.Remove(k); }
            }
            Persist();
        }
    }

    /// <summary>Drops one row (e.g. the server said it is invalid) so the rest can flow.</summary>
    public void Drop(string section, string rowId)
    {
        lock (_gate) { _rows.Remove((section, rowId)); _order.Remove((section, rowId)); Persist(); }
    }

    void Put(SyncRow r, bool persist = true)
    {
        lock (_gate)
        {
            var k = (r.Section, r.RowId);
            if (!_rows.ContainsKey(k)) _order.Add(k);
            _rows[k] = r;
            if (persist) Persist();
        }
    }

    void Persist() => _store.Save(ProjectMapper.ToWire(_order.Select(k => _rows[k])).ToJsonString());
}

public sealed record FlushResult(int Sent, int Remaining, SyncState State);
public sealed record CatchUpResult(List<SyncRow> Rows, long HeadSeq, bool NeedsReview);

/// <summary>
/// One open online project: edit lease, debounced outbox flush and delta catch-up (AQC-CLIENT-SPEC §4). The host calls
/// <see cref="TickAsync"/> about once a second (a UI timer) and <see cref="Changed"/>-style edits via <see cref="Outbox"/>;
/// nothing here starts threads, so it is deterministic to test.
/// </summary>
public sealed class ProjectSync
{
    public static readonly TimeSpan LeaseRenewEvery = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan LeaseRetryEvery = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    public const int BatchSize = 500;

    readonly ConnectClient _c;
    readonly Func<DateTimeOffset> _now;
    readonly bool _canWrite;
    DateTimeOffset _lastLeaseAttempt = DateTimeOffset.MinValue;
    DateTimeOffset _lastChange = DateTimeOffset.MinValue;
    DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    int _backoffSeconds;
    SyncState _state;

    public ProjectSync(ConnectClient client, string aqcId, long headSeq, Outbox outbox, Func<DateTimeOffset>? clock = null)
    {
        _c = client; AqcId = aqcId; HeadSeq = headSeq; Outbox = outbox; _now = clock ?? (() => DateTimeOffset.UtcNow);
        _canWrite = client.Session?.CanWrite ?? false;
        _state = !_canWrite ? SyncState.ReadOnlyRole : outbox.Count > 0 ? SyncState.Pending : SyncState.Synced;
        if (outbox.Count > 0) _lastChange = _now() - Debounce;
    }

    public string AqcId { get; }
    public long HeadSeq { get; private set; }
    public Outbox Outbox { get; }
    public bool HasLease { get; private set; }
    public SyncState State { get => _state; private set { if (_state != value) { _state = value; StateChanged?.Invoke(value); } } }
    public bool CanEdit => _canWrite && HasLease && State is not (SyncState.SessionReplaced or SyncState.SignedOut);
    public event Action<SyncState>? StateChanged;
    /// <summary>A row the server refused (400); it has been dropped from the outbox so the rest can flow.</summary>
    public event Action<SyncRow, string>? RowRejected;

    /// <summary>Call after any edit has been put in the outbox; starts the 2 s debounce.</summary>
    public void NoteChange()
    {
        _lastChange = _now();
        if (State is SyncState.Synced) State = SyncState.Pending;
    }

    /// <summary>Takes or renews the lease. False = someone else is editing (open read-only).</summary>
    public async Task<bool> AcquireLeaseAsync(CancellationToken ct = default)
    {
        if (!_canWrite) return false;
        _lastLeaseAttempt = _now();
        try
        {
            var j = await _c.PostAsync<JsonElement>($"api/aqc/v1/projects/{AqcId}/lease", null, ct);
            HasLease = j.TryGetProperty("held", out var h) && h.ValueKind == JsonValueKind.True;
        }
        catch (ConnectException e) when (e.IsSessionReplaced) { HasLease = false; State = SyncState.SessionReplaced; return false; }
        catch (ConnectException e) when (e.Status == 401) { HasLease = false; State = SyncState.SignedOut; return false; }
        catch (ConnectException e) when (e.Status >= 500 || e.IsRateLimited) { GoOffline(e.RetryAfterSeconds); return HasLease; }
        catch (Exception e) when (IsNetwork(e)) { GoOffline(); return HasLease; }
        if (!HasLease) State = SyncState.ReadOnlyLeaseHeld;
        else if (State is SyncState.ReadOnlyLeaseHeld or SyncState.Offline) State = Outbox.Count > 0 ? SyncState.Pending : SyncState.Synced;
        return HasLease;
    }

    /// <summary>Sends the outbox in ≤500-row batches, oldest first. Safe to call any time; never throws for expected failures.</summary>
    public async Task<FlushResult> FlushAsync(CancellationToken ct = default)
    {
        if (!_canWrite || State is SyncState.SessionReplaced or SyncState.SignedOut) return new FlushResult(0, Outbox.Count, State);
        int sent = 0;
        while (true)
        {
            var snap = Outbox.Snapshot();
            if (snap.Count == 0) { State = SyncState.Synced; _backoffSeconds = 0; break; }
            if (!HasLease && !await AcquireLeaseAsync(ct)) break; // read-only or offline: keep everything
            var batch = snap.Take(BatchSize).ToList();
            try
            {
                var j = await _c.PostAsync<JsonElement>($"api/aqc/v1/projects/{AqcId}/rows", new { rows = ProjectMapper.ToWire(batch) }, ct);
                if (j.TryGetProperty("headSeq", out var h)) HeadSeq = h.GetInt64();
                Outbox.Acknowledge(batch);
                sent += batch.Count;
                HasLease = true; // a successful push renews the lease
                _backoffSeconds = 0;
            }
            catch (ConnectException e) when (e.IsLeaseRequired)
            {
                HasLease = false;
                if (!await AcquireLeaseAsync(ct)) break; // someone else has it: keep the rows for review
            }
            catch (ConnectException e) when (e.IsSessionReplaced) { State = SyncState.SessionReplaced; break; }
            catch (ConnectException e) when (e.Status == 401) { State = SyncState.SignedOut; break; }
            catch (ConnectException e) when (e.Status == 403) { State = SyncState.ReadOnlyRole; break; }
            catch (ConnectException e) when (e.Status == 400)
            {
                // Isolate the offending row by sending singly, so one bad row doesn't block 499 good ones.
                if (batch.Count == 1) { Outbox.Drop(batch[0].Section, batch[0].RowId); RowRejected?.Invoke(batch[0], e.Message); }
                else
                {
                    foreach (var r in batch)
                    {
                        try
                        {
                            var j = await _c.PostAsync<JsonElement>($"api/aqc/v1/projects/{AqcId}/rows", new { rows = ProjectMapper.ToWire(new[] { r }) }, ct);
                            if (j.TryGetProperty("headSeq", out var h)) HeadSeq = h.GetInt64();
                            Outbox.Acknowledge(new[] { r }); sent++;
                        }
                        catch (ConnectException e2) when (e2.Status == 400) { Outbox.Drop(r.Section, r.RowId); RowRejected?.Invoke(r, e2.Message); }
                    }
                }
            }
            catch (ConnectException e) when (e.Status >= 500 || e.IsRateLimited) { GoOffline(e.RetryAfterSeconds); break; }
            catch (Exception e) when (IsNetwork(e)) { GoOffline(); break; }
        }
        if (Outbox.Count == 0 && State is SyncState.Pending or SyncState.Offline) State = SyncState.Synced;
        return new FlushResult(sent, Outbox.Count, State);
    }

    /// <summary>Fetches changes made elsewhere since <see cref="HeadSeq"/>. With unsent local changes the rows are returned but
    /// <c>NeedsReview</c> is set and <see cref="HeadSeq"/> is NOT advanced — the user chooses to pull and reapply.</summary>
    public async Task<CatchUpResult> CatchUpAsync(CancellationToken ct = default)
    {
        var rows = new List<SyncRow>();
        long head = HeadSeq, cursor = HeadSeq;
        while (true)
        {
            var j = await _c.GetAsync<JsonElement>($"api/aqc/v1/projects/{AqcId}/rows?since={cursor}", ct);
            foreach (var r in j.GetProperty("rows").EnumerateArray()) rows.Add(ProjectMapper.FromWire(r));
            if (j.TryGetProperty("headSeq", out var h)) head = h.GetInt64();
            if (!(j.TryGetProperty("more", out var m) && m.ValueKind == JsonValueKind.True)) break;
            var next = j.GetProperty("nextSince").GetInt64();
            if (next <= cursor) break;
            cursor = next;
        }
        bool review = Outbox.Count > 0 && rows.Count > 0;
        if (!review) HeadSeq = head;
        return new CatchUpResult(rows, head, review);
    }

    /// <summary>Accepts a reviewed catch-up (after the user reapplied or discarded local changes).</summary>
    public void AdoptHead(long head) => HeadSeq = Math.Max(HeadSeq, head);

    /// <summary>Replaces the project-level settings (levels, markups, covers…). Needs the lease; 1 MB cap is enforced by the server.</summary>
    public async Task PushSettingsAsync(JsonObject settings, CancellationToken ct = default)
    {
        if (!HasLease && !await AcquireLeaseAsync(ct)) throw new ConnectException(423, "lease_required", "Someone else is editing this project.");
        await _c.PutAsync<JsonElement>($"api/aqc/v1/projects/{AqcId}/settings", new { settings }, ct);
    }

    /// <summary>Drives timers: renews the lease every 60 s (or retries every 30 s when read-only) and flushes after the debounce.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (!_canWrite || State is SyncState.SessionReplaced or SyncState.SignedOut) return;
        var now = _now();
        var every = HasLease ? LeaseRenewEvery : LeaseRetryEvery;
        if (now - _lastLeaseAttempt >= every && State != SyncState.Offline) await AcquireLeaseAsync(ct);
        if (Outbox.Count > 0 && now - _lastChange >= Debounce && now >= _retryAt && State != SyncState.ReadOnlyLeaseHeld) await FlushAsync(ct);
        else if (Outbox.Count > 0 && State == SyncState.ReadOnlyLeaseHeld && HasLease) await FlushAsync(ct);
    }

    void GoOffline(int? retryAfter = null)
    {
        _backoffSeconds = Math.Min(Math.Max(_backoffSeconds * 2, 2), 120);
        _retryAt = _now().AddSeconds(retryAfter ?? _backoffSeconds);
        State = SyncState.Offline;
    }

    static bool IsNetwork(Exception e) => e is HttpRequestException or TaskCanceledException or IOException;
}
