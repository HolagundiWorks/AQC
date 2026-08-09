// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text.Json.Serialization;

namespace Aorms.Bridge;

public sealed class ActivateRequest
{
    [JsonPropertyName("licenseKey")]
    public string LicenseKey { get; set; } = "";

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("deviceName")]
    public string? DeviceName { get; set; }
}

public sealed class ActivateResult
{
    [JsonPropertyName("licenseToken")]
    public string LicenseToken { get; set; } = "";

    [JsonPropertyName("syncToken")]
    public string SyncToken { get; set; } = "";
}

public sealed class HubConfigured
{
    public string HubUrl { get; init; } = "";
    public string LicenseApiUrl { get; init; } = "";
    public bool HasSyncToken { get; init; }
    public bool SyncReady { get; init; }
}

/// <summary>
/// Local licence snapshot for AORMS Connect Licence Manager (C3).
/// Not a full admin.aorms.in console — status + paths only.
/// </summary>
public sealed class LicenceSnapshot
{
    public string InstallId { get; init; } = "";
    public string? LicenceStatus { get; init; }
    public bool HasLicenseToken { get; init; }
    public bool HasSyncToken { get; init; }
    public string HubUrl { get; init; } = "";
    public string LicenseApiUrl { get; init; } = "";
    public string? UpdatedAt { get; init; }
    public bool SessionFilePresent { get; init; }
    public string SessionPath { get; init; } = "";
    public string FirmDbPath { get; init; } = "";
}

public sealed class FlushResult
{
    public int MetaSent { get; init; }
    public int ArtifactsSent { get; init; }
    public string? SkippedReason { get; init; }
}

/// <summary>Pending rows in firm.db outboxes (Connect DB connector).</summary>
public sealed class OutboxCounts
{
    public int PendingMeta { get; init; }
    public int PendingArtifacts { get; init; }
    public int TotalPending => PendingMeta + PendingArtifacts;
}

/// <summary>APPROVED joint measurement bundle from GET /api/ops/joint-measurements (S11).</summary>
public sealed class JointMeasurementBundle
{
    public string Id { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string? ContractorId { get; init; }
    public string Subject { get; init; } = "";
    public string? MeasuredOn { get; init; }
    public string Status { get; init; } = "APPROVED";
    public string? ReviewedAt { get; init; }
    public List<JointMeasurementLineDto> Lines { get; init; } = new();
}

public sealed class JointMeasurementLineDto
{
    public string Id { get; init; } = "";
    public string? Code { get; init; }
    public string Description { get; init; } = "";
    public string Uom { get; init; } = "";
    public string? MeasureKind { get; init; }
    public double? LengthMm { get; init; }
    public double? BreadthMm { get; init; }
    public double? HeightMm { get; init; }
    public double? CountNos { get; init; }
    public double Quantity { get; init; }
    public int SortOrder { get; init; }
}
