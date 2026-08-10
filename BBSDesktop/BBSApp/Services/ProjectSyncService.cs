// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Aorms.Bridge;

namespace BBSApp.Services;

/// <summary>
/// Connect catalog bind + hub Flush / meta publish for the open .bbsproj.
/// Canon: esti PORTAL-SYNC-BRIDGE · AORMS-CONNECT · AQC-JM-SYNC.
/// </summary>
public static class ProjectSyncService
{
    public static AormsBridge OpenBridge()
    {
        var bridge = AormsBridgeHost.CreateFromEnvironment();
        bridge.TryImportConnectSession(overwrite: true);
        return bridge;
    }

    public static string StatusSummary()
    {
        using var bridge = OpenBridge();
        var cfg = bridge.HubConfigured();
        var outbox = bridge.OutboxCounts();
        var hubId = ProjectStore.Current.Info.HubProjectId?.Trim() ?? "";
        var bind = string.IsNullOrWhiteSpace(hubId)
            ? "unbound"
            : hubId.Length <= 8
                ? hubId
                : hubId[..8] + "…";
        var ready = cfg.SyncReady ? "ready" : cfg.HasSyncToken ? "no hub" : "no token";
        var pending = outbox.TotalPending;
        return pending > 0
            ? $"Sync {ready} · {bind} · pending {pending}"
            : $"Sync {ready} · {bind}";
    }

    /// <summary>Bind the open project to a Connect catalog row (shared UUID).</summary>
    public static string Bind(CatalogProject project)
    {
        if (string.IsNullOrWhiteSpace(project.Id))
            throw new InvalidOperationException("Catalog project missing id.");

        var info = ProjectStore.Current.Info;
        info.HubProjectId = project.Id.Trim();
        if (string.IsNullOrWhiteSpace(info.Name) ||
            string.Equals(info.Name, "Untitled Project", StringComparison.OrdinalIgnoreCase))
        {
            info.Name = string.IsNullOrWhiteSpace(project.Title)
                ? (string.IsNullOrWhiteSpace(project.Ref) ? project.Id : project.Ref)
                : project.Title;
            ProjectStore.Current.Name = info.Name;
        }

        ProjectStore.Current.Notify();
        return $"Bound · {info.Name} · {ShortId(info.HubProjectId)}";
    }

    public static void BindHubProjectId(string hubProjectId)
    {
        var id = hubProjectId?.Trim() ?? "";
        ProjectStore.Current.Info.HubProjectId = id;
        ProjectStore.Current.Notify();
    }

    public static string? RequireHubProjectId()
    {
        var id = ProjectStore.Current.Info.HubProjectId?.Trim();
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    public static async Task<(bool Ok, string Message)> FlushAsync(CancellationToken ct = default)
    {
        try
        {
            using var bridge = OpenBridge();
            var result = await bridge.FlushAsync(ct).ConfigureAwait(true);
            if (result.SkippedReason is not null)
                return (false, $"Flush skipped={result.SkippedReason} — Activate in AORMS Connect first.");
            return (true, $"Flush OK · metaSent={result.MetaSent} · artifacts={result.ArtifactsSent}");
        }
        catch (Exception ex)
        {
            return (false, $"Flush failed: {ex.Message}");
        }
    }

    public static async Task<(bool Ok, string Message)> PublishProjectStatusAsync(CancellationToken ct = default)
    {
        var pid = RequireHubProjectId();
        if (pid is null)
            return (false, "Bind a Connect project first (Settings › Project or ActionDock › Bind).");

        var info = ProjectStore.Current.Info;
        try
        {
            using var bridge = OpenBridge();
            bridge.EnqueueMeta("projectStatus", pid, new Dictionary<string, object?>
            {
                ["projectId"] = pid,
                ["ref"] = ConnectCatalog.FindById(pid)?.Ref ?? "",
                ["title"] = info.Name,
                ["status"] = "ACTIVE",
                ["phase"] = "",
                ["clientId"] = info.ClientName,
                ["jurisdiction"] = "",
                ["siteAddress"] = info.Location,
                ["workType"] = "estimation",
                ["updatedAt"] = DateTime.UtcNow.ToString("O"),
            });
            var result = await bridge.FlushAsync(ct).ConfigureAwait(true);
            if (result.SkippedReason is not null)
                return (false, $"Queued projectStatus; flush skipped={result.SkippedReason}");
            return (true, $"Published projectStatus · {ShortId(pid)} · metaSent={result.MetaSent}");
        }
        catch (Exception ex)
        {
            return (false, $"Publish status failed: {ex.Message}");
        }
    }

    public static async Task<(bool Ok, string Message)> PublishEstimateTotalsAsync(CancellationToken ct = default)
    {
        var pid = RequireHubProjectId();
        if (pid is null)
            return (false, "Bind a Connect project first.");

        var est = ProjectStore.Current.LastEstimate;
        if (est is null)
            return (false, "Calculate an estimate first (Outputs › Estimate).");

        var estimateId = string.IsNullOrWhiteSpace(est.RateBookVersionId)
            ? pid
            : $"{pid}:{est.RateBookVersionId}";
        var grandPaise = ToPaise(est.GrandTotal);
        var basePaise = ToPaise(est.BaseTotal);

        try
        {
            using var bridge = OpenBridge();
            bridge.EnqueueMeta("estimateTotals", estimateId, new Dictionary<string, object?>
            {
                ["estimateId"] = estimateId,
                ["projectId"] = pid,
                ["subtotalPaise"] = basePaise,
                ["contingencyPaise"] = 0,
                ["gstPaise"] = 0,
                ["grandTotalPaise"] = grandPaise,
                ["rateBookVersionId"] = est.RateBookVersionId,
                ["rateBookVersionName"] = est.RateBookVersionName,
                ["currency"] = "INR",
                ["updatedAt"] = DateTime.UtcNow.ToString("O"),
            });
            var result = await bridge.FlushAsync(ct).ConfigureAwait(true);
            if (result.SkippedReason is not null)
                return (false, $"Queued estimateTotals; flush skipped={result.SkippedReason}");
            return (true, $"Published estimateTotals · ₹{est.GrandTotal:N2} · metaSent={result.MetaSent}");
        }
        catch (Exception ex)
        {
            return (false, $"Publish estimate failed: {ex.Message}");
        }
    }

    static long ToPaise(double rupees) =>
        (long)Math.Round(rupees * 100.0, MidpointRounding.AwayFromZero);

    static string ShortId(string id) =>
        id.Length <= 8 ? id : id[..8] + "…";
}
