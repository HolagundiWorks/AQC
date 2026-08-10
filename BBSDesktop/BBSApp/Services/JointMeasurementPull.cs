// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Globalization;
using Aorms.Bridge;

namespace BBSApp.Services;

/// <summary>
/// Pull APPROVED joint measurements from the hub into rate book + earthwork MB rows.
/// Canon: esti docs/esti/AQC-JM-SYNC.md (S11). Idempotent on JM line id (mark) and itemCode.
/// </summary>
public static class JointMeasurementPull
{
    public sealed class Result
    {
        public int Bundles { get; init; }
        public int LinesSeen { get; init; }
        public int RateItemsAdded { get; init; }
        public int MbRowsAdded { get; init; }
        public string ProjectId { get; init; } = "";
        public string? Error { get; init; }
    }

    public static async Task<Result> PullAsync(string? projectId = null, CancellationToken ct = default)
    {
        try
        {
            using var bridge = AormsBridgeHost.CreateFromEnvironment();
            bridge.TryImportConnectSession(overwrite: true);

            var pid = projectId?.Trim();
            if (string.IsNullOrWhiteSpace(pid))
                pid = ProjectStore.Current.Info.HubProjectId?.Trim();
            if (string.IsNullOrWhiteSpace(pid))
            {
                return new Result
                {
                    Error = "Bind a Connect project first (ActionDock › Bind or Settings › Project › Hub project id)."
                };
            }

            var bundles = await bridge.PullJointMeasurementsAsync(pid, ct).ConfigureAwait(true);
            var rateAdded = 0;
            var mbAdded = 0;
            var linesSeen = 0;

            RateBookStore.Current.EnsureLoaded();
            var book = RateBookStore.Current.ActiveOrFirst();
            if (book is null)
                return new Result { Error = "No local rate book", ProjectId = pid };

            var byCode = RateBookStore.Current.IndexByCode(book);
            var sheet = ProjectStore.Current.Earthwork;
            var existingMarks = new HashSet<string>(
                sheet.Select(r => r.TryGetValue("mark", out var m) ? m : "")
                    .Where(m => !string.IsNullOrWhiteSpace(m)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var jm in bundles)
            {
                foreach (var line in jm.Lines.OrderBy(l => l.SortOrder))
                {
                    linesSeen++;
                    var code = string.IsNullOrWhiteSpace(line.Code)
                        ? $"JM-{jm.Id[..Math.Min(8, jm.Id.Length)]}"
                        : line.Code.Trim();

                    if (!byCode.ContainsKey(code))
                    {
                        var item = new RateItem
                        {
                            Code = code,
                            Category = "Joint measurement",
                            Description = line.Description,
                            Unit = line.Uom,
                            Rate = 0,
                        };
                        book.Items.Add(item);
                        byCode[code] = item;
                        rateAdded++;
                    }

                    var mark = $"JM:{line.Id}";
                    if (existingMarks.Contains(mark)) continue;

                    var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["mark"] = mark,
                        ["level"] = "JM",
                        ["work_type"] = jm.Subject,
                        ["length"] = Fmt(line.LengthMm),
                        ["breadth"] = Fmt(line.BreadthMm),
                        ["depth"] = Fmt(line.HeightMm),
                        ["nos"] = Fmt(line.CountNos ?? 1),
                        ["qty"] = line.Quantity.ToString(CultureInfo.InvariantCulture),
                        ["item_code"] = code,
                        ["particulars"] = line.Description,
                        ["uom"] = line.Uom,
                        ["source"] = $"jointMeasurement:{jm.Id}",
                    };
                    sheet.Add(row);
                    existingMarks.Add(mark);
                    mbAdded++;
                }
            }

            if (rateAdded > 0)
                RateBookStore.Current.UpdateVersion(book);

            if (mbAdded > 0 || rateAdded > 0)
                ProjectStore.Current.Notify();

            return new Result
            {
                Bundles = bundles.Count,
                LinesSeen = linesSeen,
                RateItemsAdded = rateAdded,
                MbRowsAdded = mbAdded,
                ProjectId = pid,
            };
        }
        catch (Exception ex)
        {
            return new Result { Error = ex.Message };
        }
    }

    static string Fmt(double? v) =>
        v is null ? "" : v.Value.ToString(CultureInfo.InvariantCulture);
}
