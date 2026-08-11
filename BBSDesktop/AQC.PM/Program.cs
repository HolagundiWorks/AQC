using Aorms.Bridge;

// Suite product shell — AQC Project Management
// Shares bbs_engine (via AQC) + Aorms.Bridge. Full WinUI domain UI lands next.
Console.WriteLine("AQC Project Management · product=pm");
var opt = new BridgeOptions
{
    DeviceId = Environment.GetEnvironmentVariable("INSTALL_ID") ?? $"aqc-pm-dev",
    HubUrl = Environment.GetEnvironmentVariable("ESTI_HUB_URL") ?? "http://127.0.0.1:4000",
    LicenseApiUrl = Environment.GetEnvironmentVariable("ESTI_LICENSE_API_URL") ?? "",
    ProductApiKey = Environment.GetEnvironmentVariable("ESTI_PRODUCT_API_KEY") ?? "",
    DeviceName = "AQC Project Management",
};
using var bridge = new AormsBridge(opt);
bridge.TryImportConnectSession(overwrite: true);
var cfg = bridge.HubConfigured();
Console.WriteLine($"syncReady={cfg.SyncReady} hub={cfg.HubUrl} (licence from AORMS Connect session.json)");
Console.WriteLine("OK shared Aorms.Bridge (suite three-app packaging).");