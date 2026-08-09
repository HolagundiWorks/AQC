using Aorms.Bridge;

// Suite product shell — AQC Estimation
// Shares bbs_engine (via AQC) + Aorms.Bridge. Full WinUI domain UI lives in BBSApp.
// CLI: pull-jm [projectId] — smoke S11 joint-measurement pull (hub GET /api/ops/joint-measurements).
Console.WriteLine("AQC Estimation · product=estimation");
var opt = new BridgeOptions
{
    DeviceId = Environment.GetEnvironmentVariable("INSTALL_ID") ?? $"aqc-estimation-dev",
    HubUrl = Environment.GetEnvironmentVariable("ESTI_HUB_URL") ?? "http://127.0.0.1:4000",
    LicenseApiUrl = Environment.GetEnvironmentVariable("ESTI_LICENSE_API_URL") ?? "",
    ProductApiKey = Environment.GetEnvironmentVariable("ESTI_PRODUCT_API_KEY") ?? "",
    DeviceName = "AQC Estimation",
};
using var bridge = new AormsBridge(opt);
bridge.TryImportConnectSession(overwrite: true);
var cfg = bridge.HubConfigured();
Console.WriteLine($"syncReady={cfg.SyncReady} hub={cfg.HubUrl}");

if (args.Length >= 1 && string.Equals(args[0], "pull-jm", StringComparison.OrdinalIgnoreCase))
{
    var projectId = args.Length >= 2 ? args[1] : ConnectCatalog.List().FirstOrDefault()?.Id;
    if (string.IsNullOrWhiteSpace(projectId))
    {
        Console.Error.WriteLine("pull-jm: pass projectId or populate Connect catalog.json");
        Environment.ExitCode = 2;
        return;
    }

    try
    {
        var bundles = await bridge.PullJointMeasurementsAsync(projectId);
        var lines = bundles.Sum(b => b.Lines.Count);
        Console.WriteLine($"pull-jm OK project={projectId} bundles={bundles.Count} lines={lines}");
        foreach (var b in bundles)
            Console.WriteLine($"  {b.Id} · {b.Subject} · {b.Lines.Count} lines");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"pull-jm failed: {ex.Message}");
        Environment.ExitCode = 1;
    }
    return;
}

Console.WriteLine("OK shared Aorms.Bridge (suite three-app packaging).");
Console.WriteLine("Tip: AQC.Estimation pull-jm <projectId>");
