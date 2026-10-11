// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Aorms.Bridge;
using BBSApp.Models;
using BBSApp.Services;
using BBSApp.Services.Ai;
using BBSApp.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;
using Line = Microsoft.UI.Xaml.Shapes.Line;

namespace BBSApp;

public sealed partial class MainWindow : Window, IAppCommandBus, IAssistantConfirm
{
    private sealed record NavCmd(string Tag, string Label, object? Icon);

    private sealed record RibbonTab(string Id, string Title, NavCmd[] Commands);

    private readonly List<RibbonTab> _tabs = new();
    // Canonical navigable tags, matched case-sensitively to NavigateTo's switch arms.
    private readonly HashSet<string> _navTags = new(StringComparer.Ordinal);
    private string _activeTab = "project";
    private string _activeTag = "dashboard";
    private DispatcherTimer? _toastTimer;
    private DispatcherTimer? _clockTimer;
    private DispatcherTimer? _pomodoroClickTimer;
    private int _toastGeneration;
    private int _clockTicks;
    private int _pomodoroDurationSec = 25 * 60;
    private int _pomodoroLeftSec = 25 * 60;
    private bool _pomodoroRunning;
    private bool _pomodoroDragging;
    private bool _pomodoroPressActive;
    private Point _pomodoroPressOrigin;
    private AssistantService? _assistant;
    private bool _assistantBusy;

    public MainWindow()
    {
        InitializeComponent();
        App.MainWindow = this;
        // Opaque Fog canvas — no Mica/Acrylic (HCW pure neu; DESKTOP-WINUI-UX).
        SystemBackdrop = null;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyBrandingChrome();
        TrySetAppIcon();
        ProjectStore.Current.SeedDefaults();
        BuildRibbonModel();
        BuildRibbonTabs();
        SelectTab("project");
        NavigateTo("dashboard");
        UpdatePersonaBadge();
        RefreshSyncBadge();
        // Warm Connect session into firm.db for Flush readiness.
        _ = ProjectSyncService.StatusSummary();
        SetWindowTitle(Branding.WindowTitle(ProjectStore.Current.Name, ProjectStore.Current.IsDirty));
        StartClockPomodoro();
        AppNotify.Raised += OnAppNotify;
        ProjectStore.Current.Changed += OnStoreChanged;
        Closed += (_, _) =>
        {
            AppNotify.Raised -= OnAppNotify;
            ProjectStore.Current.Changed -= OnStoreChanged;
            _clockTimer?.Stop();
            _pomodoroClickTimer?.Stop();
        };
        if (Content is FrameworkElement root)
            root.KeyDown += Window_KeyDown;
    }

    // Was 0.8; user asked 1.2× of that → 0.96 of design.
    const double UiScale = 0.96;

    void ScaleViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ScaleHost is null || e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        // RenderTransform does not affect layout — enlarge host so 0.8× fills the viewport.
        ScaleHost.Width = e.NewSize.Width / UiScale;
        ScaleHost.Height = e.NewSize.Height / UiScale;
        if (UiScaleTransform is not null)
        {
            UiScaleTransform.ScaleX = UiScale;
            UiScaleTransform.ScaleY = UiScale;
        }
    }

    void StartClockPomodoro()
    {
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            TickClock();
            TickPomodoro();
            _clockTicks++;
            if (_clockTicks % 15 == 0)
                RefreshSyncBadge();
        };
        _clockTimer.Start();
        TickClock();
        UpdatePomodoroUi();
    }

    void TickClock()
    {
        if (ClockHour is null) return;
        var now = DateTime.Now;
        const double cx = 50.0;
        const double cy = 50.0;
        SetHand(ClockHour, cx, cy, (now.Hour % 12 + now.Minute / 60.0) * 30.0, 22);
        SetHand(ClockMinute, cx, cy, now.Minute * 6.0, 32);
        ClockSecond.Opacity = _pomodoroRunning ? 0 : 1;
        if (!_pomodoroRunning)
            SetHand(ClockSecond, cx, cy, now.Second * 6.0, 36);
    }

    static void SetHand(Line line, double cx, double cy, double degrees, double length)
    {
        var rad = (degrees - 90) * Math.PI / 180.0;
        line.X1 = cx;
        line.Y1 = cy;
        line.X2 = cx + Math.Cos(rad) * length;
        line.Y2 = cy + Math.Sin(rad) * length;
    }

    void TickPomodoro()
    {
        if (!_pomodoroRunning || _pomodoroDragging) return;
        if (_pomodoroLeftSec <= 0)
        {
            _pomodoroRunning = false;
            _pomodoroLeftSec = 0;
            UpdatePomodoroUi();
            AppNotify.Success("Pomodoro", "Focus block complete.");
            return;
        }
        _pomodoroLeftSec--;
        UpdatePomodoroUi();
    }

    void UpdatePomodoroUi()
    {
        if (PomodoroLabel is null) return;
        var active = _pomodoroRunning || _pomodoroLeftSec < _pomodoroDurationSec || _pomodoroDragging;
        var label = FormatPomodoro(_pomodoroLeftSec);
        PomodoroLabel.Text = _pomodoroDragging
            ? $"{label} · set"
            : !_pomodoroRunning && !active
                ? $"{label} · Pomodoro"
                : _pomodoroRunning
                    ? $"{label} · running"
                    : $"{label} · paused";
        PomodoroLabel.Foreground = active
            ? BrushRes("HcwAccentBrush", Color.FromArgb(255, 0xFF, 0x4F, 0x18))
            : BrushRes("HcwMutedBrush", Color.FromArgb(255, 0x5C, 0x63, 0x70));

        var frac = Math.Clamp(_pomodoroLeftSec / 3600.0, 0, 1);
        UpdatePomodoroRing(frac);
    }

    void UpdatePomodoroRing(double frac)
    {
        if (PomodoroArm is null) return;
        const double outer = 127;
        const double cx = outer / 2;
        const double cy = outer / 2;
        const double r = outer * (72.5 / 165);

        var (hx, hy) = PointOnRing(cx, cy, r, frac);
        PomodoroArm.X1 = cx;
        PomodoroArm.Y1 = cy;
        PomodoroArm.X2 = hx;
        PomodoroArm.Y2 = hy;
        Canvas.SetLeft(PomodoroCrown, hx - 5);
        Canvas.SetTop(PomodoroCrown, hy - 5);
        Canvas.SetLeft(PomodoroCrownHit, hx - 14);
        Canvas.SetTop(PomodoroCrownHit, hy - 14);

        if (frac <= 0.001)
        {
            PomodoroArc.Data = null;
            return;
        }

        var (sx, sy) = PointOnRing(cx, cy, r, 0);
        var (ex, ey) = PointOnRing(cx, cy, r, Math.Min(frac, 0.9999));
        var fig = new PathFigure
        {
            StartPoint = new Point(sx, sy),
            IsClosed = false,
            IsFilled = false,
        };
        fig.Segments.Add(new ArcSegment
        {
            Point = new Point(ex, ey),
            Size = new Size(r, r),
            IsLargeArc = frac > 0.5,
            SweepDirection = SweepDirection.Clockwise,
            RotationAngle = 0,
        });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        PomodoroArc.Data = geo;
    }

    static (double x, double y) PointOnRing(double cx, double cy, double r, double frac)
    {
        var a = (-90 + frac * 360) * Math.PI / 180.0;
        return (cx + r * Math.Cos(a), cy + r * Math.Sin(a));
    }

    static string FormatPomodoro(int seconds)
    {
        var s = Math.Max(0, seconds);
        return $"{s / 60:00}:{s % 60:00}";
    }

    void PomodoroToggle_Click(object sender, RoutedEventArgs e) => TogglePomodoro();

    void Pomodoro_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_pomodoroDragging || _pomodoroPressActive) return;
        _pomodoroClickTimer?.Stop();
        _pomodoroClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _pomodoroClickTimer.Tick += (_, _) =>
        {
            _pomodoroClickTimer?.Stop();
            _pomodoroClickTimer = null;
            TogglePomodoro();
        };
        _pomodoroClickTimer.Start();
        e.Handled = true;
    }

    void Pomodoro_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _pomodoroClickTimer?.Stop();
        _pomodoroClickTimer = null;
        ResetPomodoro();
        e.Handled = true;
    }

    void TogglePomodoro()
    {
        if (_pomodoroLeftSec <= 0)
        {
            _pomodoroLeftSec = _pomodoroDurationSec;
            _pomodoroRunning = true;
        }
        else
        {
            _pomodoroRunning = !_pomodoroRunning;
        }
        UpdatePomodoroUi();
        AppNotify.Info("Pomodoro", _pomodoroRunning ? "Running" : "Paused");
    }

    void ResetPomodoro()
    {
        _pomodoroRunning = false;
        _pomodoroDragging = false;
        _pomodoroPressActive = false;
        _pomodoroLeftSec = _pomodoroDurationSec;
        UpdatePomodoroUi();
        AppNotify.Info("Pomodoro", "Reset to focus block.");
    }

    void PomodoroCrown_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_pomodoroRunning) return;
        _pomodoroPressActive = true;
        _pomodoroDragging = false;
        _pomodoroPressOrigin = e.GetCurrentPoint(PomodoroCanvas).Position;
        PomodoroCrownHit.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    void PomodoroCrown_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pomodoroPressActive || _pomodoroRunning) return;
        var pt = e.GetCurrentPoint(PomodoroCanvas).Position;
        var dx = pt.X - _pomodoroPressOrigin.X;
        var dy = pt.Y - _pomodoroPressOrigin.Y;
        if (!_pomodoroDragging && (dx * dx + dy * dy) < 36) return;
        _pomodoroDragging = true;
        ApplyPomodoroDrag(pt);
        e.Handled = true;
    }

    void PomodoroCrown_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pomodoroPressActive) return;
        var wasDragging = _pomodoroDragging;
        _pomodoroPressActive = false;
        _pomodoroDragging = false;
        try { PomodoroCrownHit.ReleasePointerCapture(e.Pointer); } catch { /* ignore */ }

        if (wasDragging)
        {
            var minutes = Math.Max(5, Math.Min(60, (int)Math.Round(_pomodoroLeftSec / 60.0 / 5.0) * 5));
            _pomodoroDurationSec = minutes * 60;
            _pomodoroLeftSec = _pomodoroDurationSec;
            UpdatePomodoroUi();
            AppNotify.Info("Pomodoro", $"Set · {minutes} min");
        }
        else
        {
            TogglePomodoro();
        }
        e.Handled = true;
    }

    void ApplyPomodoroDrag(Point pt)
    {
        const double cx = 63.5;
        const double cy = 63.5;
        var deg = Math.Atan2(pt.Y - cy, pt.X - cx) * 180.0 / Math.PI;
        var fromTop = (deg + 90) % 360;
        if (fromTop < 0) fromTop += 360;
        var frac = Math.Max(5.0 / 60.0, Math.Min(1.0, fromTop / 360.0));
        _pomodoroLeftSec = (int)Math.Round(frac * 3600);
        UpdatePomodoroUi();
    }

    private void OnStoreChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SetWindowTitle(Branding.WindowTitle(ProjectStore.Current.Name, ProjectStore.Current.IsDirty));
            UpdatePersonaBadge();
            RefreshSyncBadge();
        });
    }

    private void Window_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl) return;
        string? tab = e.Key switch
        {
            VirtualKey.Number1 or VirtualKey.NumberPad1 => "project",
            VirtualKey.Number2 or VirtualKey.NumberPad2 => "rcc",
            VirtualKey.Number3 or VirtualKey.NumberPad3 => "civil",
            VirtualKey.Number4 or VirtualKey.NumberPad4 => "schedule",
            VirtualKey.Number5 or VirtualKey.NumberPad5 => "office",
            VirtualKey.Number6 or VirtualKey.NumberPad6 => "contracts",
            VirtualKey.Number7 or VirtualKey.NumberPad7 => "accounts",
            VirtualKey.Number8 or VirtualKey.NumberPad8 => "stores",
            VirtualKey.Number9 or VirtualKey.NumberPad9 => "outputs",
            _ => null
        };
        if (tab is null) return;
        SelectTab(tab);
        e.Handled = true;
    }

    private void BuildRibbonModel()
    {
        _tabs.Clear();
        _tabs.Add(new RibbonTab("project", "Project", new[]
        {
            Cmd("dashboard", "Dashboard", Glyph("\uE80F")),
            Cmd("levels", "Levels", Glyph("\uF158")),
            Cmd("takeoff", "Drawing takeoff", Glyph("\uE8A5")),
        }));
        _tabs.Add(new RibbonTab("rcc", "RCC steel", new[]
        {
            Cmd("columns", "Columns", NavIcon("columns")),
            Cmd("beams", "Beams", NavIcon("beams")),
            Cmd("pedestals", "Pedestals", NavIcon("pedestals")),
            Cmd("lintels", "Lintels", NavIcon("lintels")),
            Cmd("slabs", "Slabs", NavIcon("slabs")),
            Cmd("footings", "Footings", NavIcon("footings")),
            Cmd("walls", "Retaining walls", NavIcon("walls")),
            Cmd("stairs", "Stairs", NavIcon("stairs")),
        }));
        _tabs.Add(new RibbonTab("civil", "Civil BOQ", new[]
        {
            Cmd("masonry", "Masonry walls", NavIcon("masonry")),
            Cmd("plaster", "Plastering", NavIcon("plaster")),
            Cmd("pcc", "PCC bed", NavIcon("pcc")),
            Cmd("earthwork", "Earthwork", NavIcon("earthwork")),
            Cmd("ssm", "Size stone", NavIcon("ssm")),
            Cmd("shuttering", "Shuttering", NavIcon("shuttering")),
            Cmd("flooring", "Flooring", NavIcon("flooring")),
            Cmd("painting", "Painting", NavIcon("painting")),
            Cmd("waterproofing", "Waterproofing", NavIcon("waterproofing")),
            Cmd("dpc", "DPC", NavIcon("dpc")),
            Cmd("coping", "Coping", NavIcon("coping")),
            Cmd("screed", "Screed", NavIcon("screed")),
            Cmd("vdf", "VDF", NavIcon("vdf")),
            Cmd("skirting", "Skirting", NavIcon("skirting")),
            Cmd("parapet", "Parapet", NavIcon("parapet")),
            Cmd("plinth_protection", "Plinth protection", NavIcon("plinth_protection")),
            Cmd("doors", "Doors", NavIcon("doors")),
            Cmd("windows", "Windows", NavIcon("windows")),
        }));
        _tabs.Add(new RibbonTab("schedule", "Schedule", new[]
        {
            Cmd("schedule_activities", "Activities", Glyph("")),
            Cmd("schedule_network", "Network", Glyph("")),
            Cmd("schedule_gantt", "Gantt", Glyph("")),
        }));
        _tabs.Add(new RibbonTab("office", "Office", new[]
        {
            Cmd("correspondence", "Correspondence", Glyph("")),
        }));
        _tabs.Add(new RibbonTab("contracts", "Contracts", new[]
        {
            Cmd("contracts_list", "Contracts", Glyph("")),
            Cmd("contracts_rates", "Rate schedule", Glyph("")),
            Cmd("contracts_terms", "Terms", Glyph("")),
        }));
        _tabs.Add(new RibbonTab("accounts", "Accounts", new[]
        {
            Cmd("accounts_bills", "Running bills", Glyph("")),
            Cmd("accounts_cash", "Cash book", Glyph("")),
            Cmd("accounts_ledger", "Ledger", Glyph("")),
        }));
        _tabs.Add(new RibbonTab("stores", "Stores", new[]
        {
            Cmd("stores_po", "Purchase orders", Glyph("")),
            Cmd("stores_grn", "Goods receipt", Glyph("")),
            Cmd("stores_inventory", "Inventory", Glyph("")),
            Cmd("stores_masters", "Masters", Glyph("")),
        }));
        _tabs.Add(new RibbonTab("hr", "HR & resources", new[]
        {
            Cmd("org_sites", "Sites", Glyph("")),
            Cmd("org_resources", "Resources", Glyph("")),
            Cmd("org_employees", "Employees", Glyph("")),
            Cmd("org_payroll", "Payroll", Glyph("")),
        }));
        _tabs.Add(new RibbonTab("outputs", "Outputs", new[]
        {
            Cmd("quantities", "Quantities", Glyph("\uE9D2")),
            Cmd("po", "Purchase orders", Glyph("\uE7BF")),
            Cmd("estimate", "Estimate", Glyph("\uE8EF")),
            Cmd("links", "Item links", Glyph("\uE71B")),
            Cmd("datamodel", "Data model", Glyph("\uE8AB")),
            Cmd("ratebook", "Rate book", Glyph("\uE8F1")),
            Cmd("pull_jm", "Pull JM", Glyph("\uE8F2")),
            Cmd("report", "Report", Glyph("\uE8A5")),
        }));

        _navTags.Clear();
        foreach (var cmd in _tabs.SelectMany(t => t.Commands))
            _navTags.Add(cmd.Tag);
        // Edit menu + sync actions (not duplicated in Outputs strip).
        foreach (var tag in new[] { "settings_project", "settings_engineering", "settings_cost",
                     "bind_project", "flush_sync", "publish_sync" })
            _navTags.Add(tag);
    }

    private static NavCmd Cmd(string tag, string label, object? icon) => new(tag, label, icon);

    /// <summary>SVG theme icon, or 1–2 letter badge when the asset is missing.</summary>
    private static FrameworkElement NavIcon(string tag)
    {
        string? key = tag.ToLowerInvariant() switch
        {
            "columns" or "column" => "NavIconColumn",
            "beams" or "beam" => "NavIconBeam",
            "pedestals" or "pedestal" => "NavIconPedestal",
            "lintels" or "lintel" => "NavIconLintel",
            "slabs" or "slab" => "NavIconSlab",
            "footings" or "footing" => "NavIconFooting",
            "walls" or "wall" => "NavIconWall",
            "stairs" or "stair" => "NavIconStair",
            "masonry" => "NavIconMasonry",
            "plaster" => "NavIconPlaster",
            "pcc" => "NavIconPcc",
            "earthwork" => "NavIconEarthwork",
            "ssm" => "NavIconSsm",
            "shuttering" => "NavIconShuttering",
            "flooring" => "NavIconFlooring",
            "painting" => "NavIconPainting",
            "waterproofing" => "NavIconWaterproofing",
            "dpc" => "NavIconDpc",
            "coping" => "NavIconCoping",
            "screed" => "NavIconScreed",
            "vdf" => "NavIconVdf",
            "skirting" => "NavIconSkirting",
            "parapet" => "NavIconParapet",
            "plinth_protection" or "plinth" => "NavIconPlinth",
            "doors" or "door" => null,
            "windows" or "window" => null,
            _ => null
        };
        if (key is not null && ThemeIcon(key) is { } img)
            return img;
        return LetterIcon(LetterForTag(tag));
    }

    private static string LetterForTag(string tag) => tag.ToLowerInvariant() switch
    {
        "columns" or "column" => "C",
        "beams" or "beam" => "B",
        "pedestals" or "pedestal" => "P",
        "lintels" or "lintel" => "L",
        "slabs" or "slab" => "S",
        "footings" or "footing" => "F",
        "walls" or "wall" => "W",
        "stairs" or "stair" => "ST",
        "masonry" => "MW",
        "plaster" => "PL",
        "pcc" => "PC",
        "earthwork" => "EW",
        "ssm" => "SS",
        "shuttering" => "SH",
        "flooring" => "FL",
        "painting" => "PT",
        "waterproofing" => "WP",
        "dpc" => "DP",
        "coping" => "CP",
        "screed" => "SC",
        "vdf" => "VD",
        "skirting" => "SK",
        "parapet" => "PR",
        "plinth_protection" or "plinth" => "PP",
        "doors" or "door" => "DR",
        "windows" or "window" => "WN",
        "estimate" => "ES",
        "links" => "LK",
        "datamodel" => "DM",
        "ratebook" => "RB",
        _ => tag.Length >= 2 ? tag[..2].ToUpperInvariant() : tag.ToUpperInvariant()
    };

    private static FrameworkElement LetterIcon(string letters, double size = 16)
    {
        string text = letters.Length > 2 ? letters[..2] : letters;
        return new Border
        {
            Width = size + 6,
            Height = size + 6,
            CornerRadius = new CornerRadius(8),
            Background = BrushRes("HcwSoftBrush", Color.FromArgb(255, 0xEC, 0xEE, 0xF2)),
            BorderBrush = BrushRes("HcwEdgeBrush", Color.FromArgb(255, 0xD0, 0xD4, 0xDC)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = text.Length > 1 ? size * 0.55 : size * 0.7,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = BrushRes("HcwInkBrush", Color.FromArgb(255, 0x14, 0x15, 0x17)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0)
            }
        };
    }

    private static ImageIcon? ThemeIcon(string key)
    {
        if (!Application.Current.Resources.TryGetValue(key, out var src) || src is null)
            return null;
        return new ImageIcon { Source = (ImageSource)src, Width = 16, Height = 16 };
    }

    private readonly Dictionary<string, Button> _pillarButtons = new(StringComparer.OrdinalIgnoreCase);

    private void BuildRibbonTabs()
    {
        // Pillars live in the taskbar (staff geography) — progressive commands in stage strip.
        TaskbarPillarPanel.Children.Clear();
        _pillarButtons.Clear();
        RibbonTabPanel.Children.Clear();

        var primary = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "project", "rcc", "civil", "outputs" };
        var moreFlyout = new MenuFlyout();

        foreach (var tab in _tabs)
        {
            if (primary.Contains(tab.Id))
            {
                var btn = new Button
                {
                    Content = tab.Title,
                    Tag = tab.Id,
                    Style = (Style)Application.Current.Resources["HcwGhostButton"],
                };
                ToolTipService.SetToolTip(btn, $"{tab.Title} — show commands");
                btn.Click += Pillar_Click;
                _pillarButtons[tab.Id] = btn;
                TaskbarPillarPanel.Children.Add(btn);
            }
            else
            {
                var item = new MenuFlyoutItem { Text = tab.Title, Tag = tab.Id };
                item.Click += PillarMenu_Click;
                moreFlyout.Items.Add(item);
            }
        }

        if (moreFlyout.Items.Count > 0)
        {
            var moreBtn = new Button
            {
                Content = "More",
                Style = (Style)Application.Current.Resources["HcwGhostButton"],
                Flyout = moreFlyout,
            };
            ToolTipService.SetToolTip(moreBtn, "Schedule · Office · Contracts · Accounts · Stores · HR");
            TaskbarPillarPanel.Children.Add(moreBtn);
        }
    }

    private void Pillar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        SelectTab(id);
        var first = _tabs.FirstOrDefault(t => t.Id == id)?.Commands.FirstOrDefault();
        if (first is not null) Navigate(first.Tag);
    }

    private void PillarMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        SelectTab(id);
        var first = _tabs.FirstOrDefault(t => t.Id == id)?.Commands.FirstOrDefault();
        if (first is not null) Navigate(first.Tag);
    }

    private void SelectTab(string id)
    {
        _activeTab = id;
        var accent = BrushRes("HcwAccentBrush", Color.FromArgb(255, 0xFF, 0x4F, 0x18));
        var ink = BrushRes("HcwInkBrush", Color.FromArgb(255, 0x14, 0x15, 0x17));
        var muted = BrushRes("HcwMutedBrush", Color.FromArgb(255, 0x5C, 0x63, 0x70));
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        foreach (var kv in _pillarButtons)
        {
            bool on = string.Equals(kv.Key, id, StringComparison.OrdinalIgnoreCase);
            kv.Value.FontWeight = on
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
            kv.Value.BorderThickness = on ? new Thickness(0, 0, 0, 2) : new Thickness(0);
            kv.Value.BorderBrush = on ? accent : transparent;
            kv.Value.Foreground = on ? ink : muted;
            kv.Value.Background = transparent;
        }

        var tab = _tabs.FirstOrDefault(t => t.Id == id) ?? _tabs[0];
        RibbonCommandPanel.Children.Clear();
        double captionSize = ResourceDouble("RibbonCommandCaptionSize", 12);
        double iconSize = ResourceDouble("RibbonCommandIconSize", 22);
        foreach (var cmd in tab.Commands)
        {
            var stack = new StackPanel { Spacing = 6, Width = 88 };
            FrameworkElement iconEl = cmd.Icon switch
            {
                FontIcon fi => CloneFontIcon(fi, iconSize),
                ImageIcon ii => new ImageIcon { Source = ii.Source, Width = iconSize, Height = iconSize },
                FrameworkElement fe => CloneFrameworkIcon(fe, iconSize),
                _ => LetterIcon(LetterForTag(cmd.Tag), iconSize - 2)
            };
            iconEl.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(iconEl);
            stack.Children.Add(new TextBlock
            {
                Text = cmd.Label,
                FontSize = captionSize,
                Foreground = muted,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.WrapWholeWords,
                MaxLines = 2,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            bool active = string.Equals(cmd.Tag, _activeTag, StringComparison.OrdinalIgnoreCase);
            var btn = new Button
            {
                Content = stack,
                Tag = cmd.Tag,
                Padding = new Thickness(10, 8, 10, 8),
                MinWidth = 88,
                MinHeight = 56,
                Background = active
                    ? BrushRes("HcwSoftBrush", Color.FromArgb(255, 0xEC, 0xEE, 0xF2))
                    : transparent,
                BorderBrush = active ? accent : transparent,
                BorderThickness = active ? new Thickness(0, 0, 0, 2) : new Thickness(0),
                CornerRadius = new CornerRadius(8)
            };
            ToolTipService.SetToolTip(btn, cmd.Label);
            btn.Click += RibbonCommand_Click;
            RibbonCommandPanel.Children.Add(btn);
        }
    }

    private static FontIcon CloneFontIcon(FontIcon src, double size = 16) => new()
    {
        Glyph = src.Glyph,
        FontSize = size,
        FontFamily = src.FontFamily
    };

    private static FrameworkElement CloneFrameworkIcon(FrameworkElement src, double size = 20)
    {
        if (src is ImageIcon ii)
            return new ImageIcon { Source = ii.Source, Width = size, Height = size };
        if (src is Border)
            return LetterIcon(
                src is Border { Child: TextBlock tb } ? tb.Text : "?",
                size);
        return LetterIcon("?", size);
    }

    private static FontIcon Glyph(string g, double size = 16) => new()
    {
        Glyph = g,
        FontSize = size,
        FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"]
    };

    private static double ResourceDouble(string key, double fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var v) && v is double d)
            return d;
        return fallback;
    }

    private void RibbonCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        Navigate(tag);
    }

    // ——— IAppCommandBus: the single external route into navigation ———

    /// <summary>Canonical navigable ribbon tags, in ribbon order.</summary>
    public IReadOnlyList<string> KnownTags =>
        _tabs.SelectMany(t => t.Commands).Select(c => c.Tag).Distinct().ToArray();

    /// <summary>
    /// Navigate to a ribbon command tag. Returns <c>false</c> and does nothing for
    /// an unknown tag, instead of falling through to the dashboard, so callers can
    /// tell whether the request was understood.
    /// </summary>
    public bool Navigate(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var norm = tag.Trim();
        if (!_navTags.Contains(norm)) return false;
        NavigateTo(norm);
        return true;
    }

    private void NavigateTo(string tag)
    {
        if (string.Equals(tag, "pull_jm", StringComparison.OrdinalIgnoreCase))
        {
            _ = PullJointMeasurementsAsync();
            return;
        }
        if (string.Equals(tag, "bind_project", StringComparison.OrdinalIgnoreCase))
        {
            BindProject_Click(this, new RoutedEventArgs());
            return;
        }
        if (string.Equals(tag, "flush_sync", StringComparison.OrdinalIgnoreCase))
        {
            Flush_Click(this, new RoutedEventArgs());
            return;
        }
        if (string.Equals(tag, "publish_sync", StringComparison.OrdinalIgnoreCase))
        {
            PublishStatus_Click(this, new RoutedEventArgs());
            return;
        }

        _activeTag = tag;
        // Keep matching tab selected for context; ribbon stays expanded.
        var owner = _tabs.FirstOrDefault(t => t.Commands.Any(c => c.Tag == tag));
        if (owner is not null && owner.Id != _activeTab)
            SelectTab(owner.Id);
        else
            RefreshCommandHighlight();

        ContentFrame.Content = tag switch
        {
            "dashboard" => new DashboardPage(),
            "levels" => new LevelsPage(),
            "columns" => new ElementPage(ElementSpecs.Columns(), ProjectStore.Current.Columns),
            "beams" => new ElementPage(ElementSpecs.Beams(), ProjectStore.Current.Beams),
            "pedestals" => new ElementPage(ElementSpecs.Pedestals(), ProjectStore.Current.Pedestals),
            "lintels" => new ElementPage(ElementSpecs.Lintels(), ProjectStore.Current.Lintels),
            "slabs" => new ElementPage(ElementSpecs.Slabs(), ProjectStore.Current.Slabs),
            "footings" => new ElementPage(ElementSpecs.Footings(), ProjectStore.Current.Footings),
            "walls" => new ElementPage(ElementSpecs.Walls(), ProjectStore.Current.Walls),
            "stairs" => new ElementPage(ElementSpecs.Stairs(), ProjectStore.Current.Stairs),
            "takeoff" => new TakeoffPage(),
            "masonry" => new ElementPage(ElementSpecs.MasonryWalls(), ProjectStore.Current.MasonryWalls),
            "plaster" => new ElementPage(ElementSpecs.Plaster(), ProjectStore.Current.FinishPropose),
            "pcc" => new ElementPage(ElementSpecs.PccBeds(), ProjectStore.Current.PccBeds),
            "earthwork" => new ElementPage(ElementSpecs.Earthwork(), ProjectStore.Current.Earthwork),
            "ssm" => new ElementPage(ElementSpecs.SizeStone(), ProjectStore.Current.SizeStone),
            "shuttering" => new ElementPage(ElementSpecs.Shuttering(), ProjectStore.Current.Shuttering),
            "flooring" => new ElementPage(ElementSpecs.Flooring(), ProjectStore.Current.Flooring),
            "painting" => new ElementPage(ElementSpecs.Painting(), ProjectStore.Current.Painting),
            "waterproofing" => new ElementPage(ElementSpecs.Waterproofing(), ProjectStore.Current.Waterproofing),
            "dpc" => new ElementPage(ElementSpecs.Dpc(), ProjectStore.Current.Dpc),
            "coping" => new ElementPage(ElementSpecs.Coping(), ProjectStore.Current.Coping),
            "screed" => new ElementPage(ElementSpecs.Screed(), ProjectStore.Current.Screed),
            "vdf" => new ElementPage(ElementSpecs.Vdf(), ProjectStore.Current.Vdf),
            "skirting" => new ElementPage(ElementSpecs.Skirting(), ProjectStore.Current.Skirting),
            "parapet" => new ElementPage(ElementSpecs.Parapet(), ProjectStore.Current.Parapet),
            "plinth_protection" => new ElementPage(ElementSpecs.PlinthProtection(), ProjectStore.Current.PlinthProtection),
            "doors" => new ElementPage(ElementSpecs.Doors(), ProjectStore.Current.Doors),
            "windows" => new ElementPage(ElementSpecs.Windows(), ProjectStore.Current.Windows),
            "schedule" or "schedule_activities" => new SchedulePage(SchedulePage.ScheduleTab.Activities),
            "schedule_network" => new SchedulePage(SchedulePage.ScheduleTab.Network),
            "schedule_gantt" => new SchedulePage(SchedulePage.ScheduleTab.Gantt),
            "office" or "correspondence" => new CorrespondencePage(),
            "contracts" or "contracts_list" => new ContractsPage(ContractsPage.ContractsTab.Contracts),
            "contracts_rates" => new ContractsPage(ContractsPage.ContractsTab.Rates),
            "contracts_terms" => new ContractsPage(ContractsPage.ContractsTab.Terms),
            "accounts" or "accounts_bills" => new AccountsPage(AccountsPage.AccountsTab.Bills),
            "accounts_cash" => new AccountsPage(AccountsPage.AccountsTab.Cash),
            "accounts_ledger" => new AccountsPage(AccountsPage.AccountsTab.Ledger),
            "stores" or "stores_po" => new StoresPage(StoresPage.StoresTab.Orders),
            "stores_grn" => new StoresPage(StoresPage.StoresTab.Grn),
            "stores_inventory" => new StoresPage(StoresPage.StoresTab.Inventory),
            "stores_masters" => new StoresPage(StoresPage.StoresTab.Masters),
            "hr" or "org_sites" => new OrgPage(OrgPage.OrgTab.Sites),
            "org_resources" => new OrgPage(OrgPage.OrgTab.Resources),
            "org_employees" => new OrgPage(OrgPage.OrgTab.Employees),
            "org_payroll" => new OrgPage(OrgPage.OrgTab.Payroll),
            "quantities" => new QuantitiesPage(),
            "po" => new PurchaseOrderPage(),
            "estimate" => new EstimatePage(),
            "links" => new LinkRulesPage(),
            "datamodel" => new DataModelPage(),
            "ratebook" => new RateBookPage(),
            "report" => new ReportPage(),
            "settings" or "settings_project" => new SettingsPage(SettingsPage.SettingsTab.Project),
            "settings_engineering" => new SettingsPage(SettingsPage.SettingsTab.Engineering),
            "settings_cost" => new SettingsPage(SettingsPage.SettingsTab.CostPercent),
            _ => new DashboardPage()
        };

        var label = owner?.Commands.FirstOrDefault(c => c.Tag == tag)?.Label ?? tag;
        if (RibbonPageLabel != null) RibbonPageLabel.Text = label;
    }

    /// <summary>S11 — pull APPROVED joint measurements into rate book + earthwork MB.</summary>
    private async void BindProject_Click(object sender, RoutedEventArgs e)
    {
        var catalog = ConnectCatalog.List();
        if (catalog.Count == 0)
        {
            AppNotify.Warning(
                "Connect catalog empty",
                @"Open AORMS Connect and add projects, then retry. Expected: %LocalAppData%\AORMS-Connect\catalog.json");
            return;
        }

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 280,
            ItemsSource = catalog.Select(c =>
                $"{(string.IsNullOrWhiteSpace(c.Title) ? c.Ref : c.Title)}  ·  {c.Ref}  ·  {c.Id}").ToList(),
        };
        if (list.Items.Count > 0) list.SelectedIndex = 0;

        var dlg = new ContentDialog
        {
            Title = "Bind to Connect project",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Select the shared AORMS project UUID for Flush, publish, and Pull JM.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    list,
                },
            },
            PrimaryButtonText = "Bind",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var idx = list.SelectedIndex;
        if (idx < 0 || idx >= catalog.Count)
        {
            AppNotify.Warning("Bind", "Select a catalog project.");
            return;
        }

        try
        {
            var msg = ProjectSyncService.Bind(catalog[idx]);
            RefreshSyncBadge();
            SetWindowTitle(Branding.WindowTitle(ProjectStore.Current.Name, ProjectStore.Current.IsDirty));
            AppNotify.Success("Bound", msg);
        }
        catch (Exception ex)
        {
            AppNotify.Error("Bind failed", ex.Message);
        }
    }

    private async void Flush_Click(object sender, RoutedEventArgs e)
    {
        AppNotify.Info("Flush", "Sending outbox to hub…");
        var (ok, message) = await ProjectSyncService.FlushAsync().ConfigureAwait(true);
        RefreshSyncBadge();
        if (ok) AppNotify.Success("Flush", message);
        else AppNotify.Warning("Flush", message);
    }

    private async void PublishStatus_Click(object sender, RoutedEventArgs e)
    {
        AppNotify.Info("Publish", "Queuing projectStatus…");
        var (ok, message) = await ProjectSyncService.PublishProjectStatusAsync().ConfigureAwait(true);
        RefreshSyncBadge();
        if (ok) AppNotify.Success("Publish", message);
        else AppNotify.Warning("Publish", message);
    }

    private async void PublishEstimate_Click(object sender, RoutedEventArgs e)
    {
        AppNotify.Info("Publish", "Queuing estimateTotals…");
        var (ok, message) = await ProjectSyncService.PublishEstimateTotalsAsync().ConfigureAwait(true);
        RefreshSyncBadge();
        if (ok) AppNotify.Success("Publish", message);
        else AppNotify.Warning("Publish", message);
    }

    private void SyncBadge_Tapped(object sender, TappedRoutedEventArgs e)
    {
        RefreshSyncBadge();
        NavigateTo("settings_project");
    }

    private void RefreshSyncBadge()
    {
        if (SyncBadgeText is null) return;
        try
        {
            SyncBadgeText.Text = ProjectSyncService.StatusSummary();
        }
        catch
        {
            SyncBadgeText.Text = "Sync unavailable";
        }
    }

    private async Task PullJointMeasurementsAsync()
    {
        AppNotify.Info("Pull JM", "Fetching approved joint measurements from hub…");
        var result = await JointMeasurementPull.PullAsync().ConfigureAwait(true);
        RefreshSyncBadge();
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            AppNotify.Error("Pull JM failed", result.Error);
            return;
        }

        AppNotify.Success(
            "Pull JM",
            $"{result.Bundles} measurement(s) · {result.MbRowsAdded} MB row(s) · {result.RateItemsAdded} rate item(s) · project {result.ProjectId}");
        NavigateTo("earthwork");
    }

    private void RefreshCommandHighlight()
    {
        var accent = BrushRes("HcwAccentBrush", Color.FromArgb(255, 0xFF, 0x4F, 0x18));
        var selectedBg = BrushRes("HcwSoftBrush", Color.FromArgb(255, 0xEC, 0xEE, 0xF2));
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        foreach (var child in RibbonCommandPanel.Children)
        {
            if (child is not Button btn || btn.Tag is not string tag) continue;
            bool on = string.Equals(tag, _activeTag, StringComparison.OrdinalIgnoreCase);
            btn.Background = on ? selectedBg : transparent;
            btn.BorderBrush = on ? accent : transparent;
            btn.BorderThickness = on ? new Thickness(0, 0, 0, 2) : new Thickness(0);
            btn.CornerRadius = new CornerRadius(8);
        }
    }

    static Brush BrushRes(string key, Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b)
            return b;
        return new SolidColorBrush(fallback);
    }

    private void OnAppNotify(NotifyRequest req)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _toastGeneration++;
            int gen = _toastGeneration;
            ToastBar.Title = req.Title;
            ToastBar.Message = req.Message;
            ToastBar.Severity = req.Severity;
            ToastHost.Visibility = Visibility.Visible;
            ToastBar.IsOpen = true;

            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1200, req.DurationMs)) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer.Stop();
                if (gen == _toastGeneration)
                    CloseToast();
            };
            _toastTimer.Start();
        });
    }

    private void ToastBar_CloseClick(InfoBar sender, object args)
    {
        _toastGeneration++;
        _toastTimer?.Stop();
        CloseToast();
    }

    private void CloseToast()
    {
        ToastBar.IsOpen = false;
        ToastHost.Visibility = Visibility.Collapsed;
    }

    private void TrySetAppIcon()
    {
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(ico))
                AppWindow.SetIcon(ico);
        }
        catch
        {
            // Icon is optional for unpackaged runs.
        }
    }

    private void CalcFab_Click(object sender, RoutedEventArgs e)
    {
        var open = FloatingCalcPanel.Visibility != Visibility.Visible;
        FloatingCalcPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
            CalcInput.Focus(FocusState.Programmatic);
    }

    private void CalcClose_Click(object sender, RoutedEventArgs e)
    {
        FloatingCalcPanel.Visibility = Visibility.Collapsed;
        NavCalcBtn?.Focus(FocusState.Programmatic);
    }

    private void CalcInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            string text = (CalcInput.Text ?? "").Trim();
            // Arithmetic stays the instant path; anything else goes to the assistant.
            if (QuickCalc.TryEvaluate(text, out var result))
            {
                CalcResult.Text = result;
                CalcResult.Focus(FocusState.Programmatic);
                CalcResult.SelectAll();
            }
            else if (!string.IsNullOrWhiteSpace(text))
            {
                _ = AskAssistantAsync(text);
            }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            if (!string.IsNullOrEmpty(CalcInput.Text) || !string.IsNullOrEmpty(CalcResult.Text)
                || AssistantReplyHost.Visibility == Visibility.Visible)
            {
                CalcInput.Text = "";
                CalcResult.Text = "";
                AssistantReply.Text = "";
                AssistantReplyHost.Visibility = Visibility.Collapsed;
            }
            else
            {
                FloatingCalcPanel.Visibility = Visibility.Collapsed;
                NavCalcBtn?.Focus(FocusState.Programmatic);
            }
            e.Handled = true;
        }
    }

    private async Task AskAssistantAsync(string prompt)
    {
        if (_assistantBusy) return;

        _assistant ??= AssistantService.TryCreate(this, this, DispatcherQueue);
        if (_assistant is null)
        {
            if (await PromptForKeyAsync())
                _assistant = AssistantService.TryCreate(this, this, DispatcherQueue);
            if (_assistant is null)
            {
                ShowAssistantReply("Assistant not enabled — no Anthropic API key provided.");
                return;
            }
        }

        _assistantBusy = true;
        AssistantReply.Text = "";
        AssistantReplyHost.Visibility = Visibility.Visible;
        AssistantBusy.IsActive = true;
        AssistantBusy.Visibility = Visibility.Visible;
        try
        {
            string reply = await _assistant.AskAsync(prompt);
            ShowAssistantReply(string.IsNullOrWhiteSpace(reply) ? "(no reply)" : reply);
        }
        catch (Exception ex) when (LooksLikeAuthError(ex))
        {
            // A stored key was rejected — clear it so the next ask re-prompts.
            AssistantSettings.ApiKey = null;
            _assistant = null;
            ShowAssistantReply("That API key was rejected. Ask again to re-enter it.");
        }
        catch (Exception ex)
        {
            ShowAssistantReply($"Assistant error: {ex.Message}");
        }
        finally
        {
            AssistantBusy.IsActive = false;
            AssistantBusy.Visibility = Visibility.Collapsed;
            _assistantBusy = false;
        }
    }

    private void ShowAssistantReply(string text)
    {
        AssistantReplyHost.Visibility = Visibility.Visible;
        AssistantReply.Text = text;
    }

    // IAssistantConfirm — modal gate for any assistant-driven change to project state.
    public async Task<bool> ConfirmAsync(string title, string details)
    {
        try
        {
            var dlg = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Apply",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };
            return await dlg.ShowAsync() == ContentDialogResult.Primary;
        }
        catch
        {
            // e.g. another ContentDialog is already open — treat as declined.
            return false;
        }
    }

    /// <summary>Prompt for and store the Anthropic API key. Returns true if a key was saved.</summary>
    private async Task<bool> PromptForKeyAsync()
    {
        var box = new PasswordBox { PlaceholderText = "sk-ant-…" };
        var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
        panel.Children.Add(new TextBlock
        {
            Text = "Paste your Anthropic API key to enable the assistant. "
                 + "It is stored locally under your Windows user profile.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(box);
        try
        {
            var dlg = new ContentDialog
            {
                Title = "Enable assistant",
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return false;
            var key = box.Password?.Trim();
            if (string.IsNullOrWhiteSpace(key)) return false;
            AssistantSettings.ApiKey = key;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Heuristic: does this exception (or an inner one) look like an API-key/auth failure?
    /// Uses only the exception type name and message so it needs no SDK type references.</summary>
    private static bool LooksLikeAuthError(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            string name = e.GetType().Name;
            if (name.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Authentication", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
                return true;
            string m = e.Message ?? "";
            if (m.Contains("api key", StringComparison.OrdinalIgnoreCase)
                || m.Contains("x-api-key", StringComparison.OrdinalIgnoreCase)
                || m.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                || m.Contains("401"))
                return true;
        }
        return false;
    }

    private void CalcInput_TextChanged(object sender, TextChangedEventArgs e) =>
        EvaluateCalc(selectResult: false);

    private void EvaluateCalc(bool selectResult)
    {
        if (QuickCalc.TryEvaluate(CalcInput.Text, out var result))
        {
            CalcResult.Text = result;
            if (selectResult)
            {
                CalcResult.Focus(FocusState.Programmatic);
                CalcResult.SelectAll();
            }
        }
        else if (string.IsNullOrWhiteSpace(CalcInput.Text))
        {
            CalcResult.Text = "";
        }
        else if (!CalcInput.Text.Any(c => char.IsDigit(c)))
        {
            CalcResult.Text = "";
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void NavMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        SelectNavTag(tag);
    }

    private void SelectNavTag(string tag)
    {
        Navigate(tag);
    }

    // ——— Persona badge (read-only; declared under File → Project settings) ———

    private void UpdatePersonaBadge()
    {
        if (PersonaBadgeText is null) return;
        // PersonaBadge chrome removed — status line in top ribbon only.
        var p = ProjectStore.Current.Parties.ActiveParty;
        string who = string.IsNullOrWhiteSpace(p.Company) ? p.Role.Display() : p.Company;
        PersonaBadgeText.Text = $"{p.Role.Display()} · {who}";
    }

    void ApplyBrandingChrome()
    {
        // Product name once — top ribbon only. Caption shows the open document.
        Title = Branding.AppName;
        if (BrandTitleText != null) BrandTitleText.Text = Branding.AppName;
        if (BrandSubText != null) BrandSubText.Text = Branding.FullName;
        if (TitleBarText != null)
            TitleBarText.Text = ProjectStore.Current.Name;
    }

    private void SetWindowTitle(string text)
    {
        Title = text;
        // Caption = document name only (avoid repeating "AQC Core").
        if (TitleBarText != null)
        {
            var name = ProjectStore.Current.Name;
            if (string.IsNullOrWhiteSpace(name)) name = "Untitled Project";
            TitleBarText.Text = ProjectStore.Current.IsDirty ? $"{name} *" : name;
        }
    }

    private void RefreshWindowTitle() =>
        SetWindowTitle(Branding.WindowTitle(ProjectStore.Current.Name, ProjectStore.Current.IsDirty));


    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var logo = new Border
        {
            Width = 72,
            Height = 72,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Image
            {
                Source = new BitmapImage(new Uri("ms-appx:///Assets/logo.png")),
                Stretch = Stretch.Uniform,
                Margin = new Thickness(8)
            }
        };

        var body = new StackPanel { Spacing = 8, MaxWidth = 360 };
        body.Children.Add(logo);
        body.Children.Add(new TextBlock
        {
            Text = Branding.AppName,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            HorizontalAlignment = HorizontalAlignment.Center
        });
        body.Children.Add(new TextBlock
        {
            Text = Branding.FullName,
            Style = (Style)Application.Current.Resources["BodyStrongStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        });
        body.Children.Add(new TextBlock
        {
            Text = Branding.Tagline,
            Style = (Style)Application.Current.Resources["CaptionSecondaryStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        });
        body.Children.Add(new TextBlock
        {
            Text = Branding.DevelopedBy,
            Style = (Style)Application.Current.Resources["BodyStrongStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        });
        body.Children.Add(new TextBlock
        {
            Text = Branding.Copyright,
            Style = (Style)Application.Current.Resources["MetaTextStyle"],
            HorizontalAlignment = HorizontalAlignment.Center
        });
        body.Children.Add(new TextBlock
        {
            Text = $"Licensed under {Branding.LicenseName}",
            Style = (Style)Application.Current.Resources["MetaTextStyle"],
            HorizontalAlignment = HorizontalAlignment.Center
        });

        var dlg = new ContentDialog
        {
            Title = $"About {Branding.AppName}",
            Content = body,
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
            DefaultButton = ContentDialogButton.Close
        };
        await dlg.ShowAsync();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        ProjectStore.Current.Reset();
        RefreshWindowTitle();
        UpdatePersonaBadge();
        RefreshSyncBadge();
        SelectNavTag("dashboard");
    }

    private async void ImportCad_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        CadImportService.Prepared prep;
        try { prep = CadImportService.Prepare(file.Path); }
        catch (Exception ex)
        {
            AppNotify.Error("Import from CAD", ex.Message);
            return;
        }

        var plan = prep.Plan;
        var lines = plan.Items
            .Where(i => i.Action != Aqc.CadImport.PlanAction.Unchanged)
            .Select(i => $"{i.Action,-17} {i.Section,-17} {(i.Mark.Length > 0 ? i.Mark : i.Ref)}{(i.Reason.Length > 0 ? "  - " + i.Reason : "")}")
            .ToList();
        var other = plan.OtherDrawingId is null
            ? ""
            : "\nThis project already holds rows from a different drawing; importing adds this drawing's rows alongside them.\n";
        var dlg = new ContentDialog
        {
            Title = "Import from CAD",
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    Text = plan.Summary + "\n" + other + "\n" + string.Join("\n", lines),
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
            },
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = plan.HasChanges,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        CadImportService.Apply(prep);
        RefreshWindowTitle();
        AppNotify.Success("Import from CAD", plan.Summary);
    }

    private async void ExportCad_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedFileName = ProjectStore.Current.Name.Replace(' ', '_') + ".aqcbridge";
        picker.FileTypeChoices.Add("AQC bridge file", new List<string> { ".json" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            await System.IO.File.WriteAllTextAsync(file.Path, CadImportService.BuildExport());
        }
        catch (Exception ex)
        {
            AppNotify.Error("Export for CAD failed", ex.Message);
            return;
        }
        AppNotify.Success("Export for CAD", $"{file.Name}: project details, floors above the plinth and beam depths. Apply it in the CAD program.");
    }

    private void UndoCadImport_Click(object sender, RoutedEventArgs e)
    {
        if (!CadImportService.CanUndo) { AppNotify.Warning("Import from CAD", "Nothing to undo."); return; }
        CadImportService.Undo();
        RefreshWindowTitle();
        AppNotify.Success("Import from CAD", "Last CAD import undone.");
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".bbsproj");
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var root = EngineClient.LoadProject(file.Path, out var err);
        if (root is null)
        {
            AppNotify.Error("Open failed", err ?? "Could not open project.");
            return;
        }
        ProjectStore.Current.LoadFrom(root);
        ProjectStore.Current.FilePath = file.Path;
        RefreshWindowTitle();
        UpdatePersonaBadge();
        RefreshSyncBadge();
        SelectNavTag("dashboard");
        AppNotify.Success("Project opened", ProjectStore.Current.Name);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ProjectStore.Current.FilePath))
        {
            SaveAs_Click(sender, e);
            return;
        }
        if (!EngineClient.SaveProject(ProjectStore.Current.FilePath, ProjectStore.Current.ToJson(), out var err))
            AppNotify.Error("Save failed", err ?? "Could not save.");
        else
        {
            ProjectStore.Current.IsDirty = false;
            RefreshWindowTitle();
            AppNotify.Success("Saved", Path.GetFileName(ProjectStore.Current.FilePath));
        }
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedFileName = ProjectStore.Current.Name.Replace(' ', '_');
        picker.FileTypeChoices.Add(Branding.ProjectFileFilter, new List<string> { Branding.ProjectExtension });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        if (!EngineClient.SaveProject(file.Path, ProjectStore.Current.ToJson(), out var err))
        {
            AppNotify.Error("Save failed", err ?? "Could not save.");
            return;
        }
        ProjectStore.Current.FilePath = file.Path;
        ProjectStore.Current.IsDirty = false;
        RefreshWindowTitle();
        AppNotify.Success("Saved", file.Name);
    }
}
