// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text.Json.Serialization;

namespace Aorms.Connect;

/// <summary>GET /api/aqc/v1/config — public values for this AORMS deployment.</summary>
public sealed record ConnectConfig(
    [property: JsonPropertyName("contract")] string Contract,
    [property: JsonPropertyName("supabaseUrl")] string? SupabaseUrl,
    [property: JsonPropertyName("supabaseAnonKey")] string? SupabaseAnonKey,
    [property: JsonPropertyName("limits")] ConnectLimits? Limits);

public sealed record ConnectLimits(
    [property: JsonPropertyName("rowBatch")] int RowBatch,
    [property: JsonPropertyName("maxRowsPerProject")] int MaxRowsPerProject,
    [property: JsonPropertyName("maxFileBytes")] long MaxFileBytes,
    [property: JsonPropertyName("leaseSeconds")] int LeaseSeconds);

/// <summary>POST /api/aqc/v1/auth/login response.</summary>
public sealed record LoginTokens(
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("refreshToken")] string RefreshToken,
    [property: JsonPropertyName("expiresAt")] long ExpiresAt,
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("email")] string Email);

public sealed record SessionAccount(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string? Email);

public sealed record SessionStudio(
    [property: JsonPropertyName("firmId")] string FirmId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("publicId")] string? PublicId);

public sealed record SessionEntitlement(
    [property: JsonPropertyName("connected")] bool Connected,
    [property: JsonPropertyName("plan")] string? Plan,
    [property: JsonPropertyName("expiresAt")] string? ExpiresAt);

/// <summary>POST/GET /api/aqc/v1/session response.</summary>
public sealed record SessionInfo(
    [property: JsonPropertyName("contract")] string Contract,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("account")] SessionAccount Account,
    [property: JsonPropertyName("studio")] SessionStudio Studio,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("capabilities")] Dictionary<string, bool> Capabilities,
    [property: JsonPropertyName("entitlement")] SessionEntitlement Entitlement)
{
    public bool Can(string capability) => Capabilities.TryGetValue(capability, out var v) && v;
    /// <summary>Editing needs `write`; viewers open projects read-only.</summary>
    public bool CanWrite => Can("write");
}

/// <summary>What the client keeps between runs (Windows Credential Manager in the app; never firm.db or a plain file).</summary>
public sealed record StoredSession(string RefreshToken, string? SessionId, string? Email);
