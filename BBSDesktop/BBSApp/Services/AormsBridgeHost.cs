// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Aorms.Bridge;

namespace BBSApp.Services;

/// <summary>
/// Factory for the AORMS hub bridge (firm.db under LocalAppData\AQCCore).
/// Configure via env: ESTI_LICENSE_API_URL, ESTI_HUB_URL, ESTI_PRODUCT_API_KEY, INSTALL_ID.
/// </summary>
public static class AormsBridgeHost
{
    public static AormsBridge CreateFromEnvironment()
    {
        var opt = new BridgeOptions
        {
            LicenseApiUrl = Environment.GetEnvironmentVariable("ESTI_LICENSE_API_URL") ?? "",
            HubUrl = Environment.GetEnvironmentVariable("ESTI_HUB_URL") ?? "",
            ProductApiKey = Environment.GetEnvironmentVariable("ESTI_PRODUCT_API_KEY") ?? "",
            DeviceId = Environment.GetEnvironmentVariable("INSTALL_ID")
                ?? $"aqc-{Environment.MachineName}".ToLowerInvariant(),
        };
        var bridge = new AormsBridge(opt);
        // Connect-first: pull session.json syncToken into firm.db when present.
        bridge.TryImportConnectSession(overwrite: true);
        return bridge;
    }
}
