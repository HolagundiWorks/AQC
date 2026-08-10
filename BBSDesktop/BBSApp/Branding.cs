// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

namespace BBSApp;

/// <summary>
/// Product branding — AQC Core under the AORMS suite (Human Centric Works).
/// Keep display strings aligned with esti product-nomenclature / AORMS-SUITE.
/// </summary>
public static class Branding
{
    /// <summary>Window · tile · about — suite style (space, not hyphen).</summary>
    public const string AppName = "AQC Core";

    /// <summary>Legacy hyphenated name (files · older docs).</summary>
    public const string LegacyAppName = "AQC-Core";

    /// <summary>Short mark for dense chrome.</summary>
    public const string ShortName = "AQC";

    public const string FullName = "Accelerated Quantity and Costing";
    public const string SuiteLine = "AORMS technical";
    public const string Tagline =
        "Estimation · BBS · BOQ — local engine; publish totals to the firm hub";
    public const string HeroSupport =
        "RCC BBS, civil BOQ, shuttering, estimate & PDF drawing takeoff";

    public const string Company = "Human Centric Works";
    public const string CompanyShort = "HCW";
    public const string Location = "Hospet";
    public const string CompanyLine = "Human Centric Works · Hospet";
    public const string DevelopedBy = "Developed by Human Centric Works · Hospet";
    public const string Copyright = "© Human Centric Works · Hospet";
    public const string LicenseName = "GNU AGPL v3 (Community) or Commercial";
    public const string LicenseUrl = "https://www.gnu.org/licenses/agpl-3.0.html";
    public const string SuiteUrl = "https://aorms.in";

    /// <summary>%LocalAppData% folder (keep stable — do not rename).</summary>
    public const string AppDataFolder = "AQCCore";
    public const string LegacyAppDataFolder = "BOQCore";

    public const string ProjectFileFilter = "AQC Core Project";
    public const string ProjectExtension = ".bbsproj";

    public static string WindowTitle(string? projectName = null, bool dirty = false)
    {
        string baseTitle = string.IsNullOrWhiteSpace(projectName)
            ? AppName
            : $"{AppName} — {projectName}";
        return dirty ? $"{baseTitle} •" : baseTitle;
    }

    public static string AppDataDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppDataFolder);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string ResolveAppDataFile(string fileName)
    {
        var primary = Path.Combine(AppDataDirectory, fileName);
        if (File.Exists(primary)) return primary;

        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LegacyAppDataFolder, fileName);
        return File.Exists(legacy) ? legacy : primary;
    }

    public static string ResolveAppDataSubdir(string subdir)
    {
        var primary = Path.Combine(AppDataDirectory, subdir);
        Directory.CreateDirectory(primary);
        return primary;
    }
}
