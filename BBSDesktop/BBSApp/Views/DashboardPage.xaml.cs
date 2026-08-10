// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using BBSApp.Controls;
using BBSApp.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BBSApp.Views;

public sealed partial class DashboardPage : Page
{
    private readonly ResultTable _summaryTable = new();

    public DashboardPage()
    {
        InitializeComponent();
        BrandNameText.Text = Branding.AppName;
        BrandFullText.Text = $"{Branding.FullName} · {Branding.SuiteLine}";
        BrandTaglineText.Text = Branding.Tagline;
        BrandByText.Text = Branding.DevelopedBy;
        BrandCopyrightText.Text = Branding.Copyright;
        _summaryTable.SetAutomationName("Latest steel summary");
        SummaryHost.Child = _summaryTable;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ProjectStore.Current.Changed += Refresh;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        ProjectStore.Current.Changed -= Refresh;
    }

    private void Refresh()
    {
        var s = ProjectStore.Current;
        var info = s.Info;
        NameText.Text = info.Name;
        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.Location)) meta.Add(info.Location);
        if (!string.IsNullOrWhiteSpace(info.ClientName)) meta.Add($"Client: {info.ClientName}");
        if (!string.IsNullOrWhiteSpace(info.CompanyName)) meta.Add(info.CompanyDisplay);
        meta.Add(info.PreparedByLine);
        MetaText.Text = string.Join(" · ", meta);
        int civilCount = s.MasonryWalls.Count + s.Plaster.Count + s.PccBeds.Count + s.Earthwork.Count
            + s.SizeStone.Count + s.Shuttering.Count + s.Flooring.Count + s.Painting.Count
            + s.Doors.Count + s.Windows.Count;
        CountsText.Text =
            $"Levels {s.Levels.Count} · RCC {s.Columns.Count + s.Beams.Count + s.Slabs.Count + s.Footings.Count + s.Walls.Count + s.Stairs.Count} · " +
            $"Civil {civilCount} (MW {s.MasonryWalls.Count} · DR {s.Doors.Count} · WN {s.Windows.Count} · PL {s.Plaster.Count}) · " +
            $"Takeoff {s.Takeoff.Items.Count}";

        if (s.LastCivilSummary?.Headers is { Count: > 0 } ch && s.LastCivilSummary.Rows is { Count: > 0 } civil)
        {
            _summaryTable.SetTable(ch, civil.Cast<IReadOnlyList<string>>().ToList());
        }
        else if (s.LastSummary?.Headers is { Count: > 0 } sh && s.LastSummary.Rows is { Count: > 0 } rows)
        {
            _summaryTable.SetTable(sh, rows.Cast<IReadOnlyList<string>>().ToList());
        }
        else
        {
            _summaryTable.SetTable(
                new[] { "Note" },
                new IReadOnlyList<string>[]
                {
                    new[] { "Import a PDF under Drawing takeoff, or generate civil / RCC quantities from the ribbon." }
                });
        }
    }
}
