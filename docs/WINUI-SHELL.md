# AQC Core WinUI shell (HCW soft neu)

**Status:** HCW neumorphic · staff geography · **Updated:** 2026-08-10  
**App:** `BBSDesktop/BBSApp` (assembly `AQCCore` · display **AQC Core**)  
**Canon:** esti [`DESKTOP-WINUI-UX.md`](https://github.com/HolagundiWorks/aorms/blob/main/docs/esti/DESKTOP-WINUI-UX.md) · [`HCW-UI-UX-PRINCIPLES.md`](https://github.com/HolagundiWorks/aorms/blob/main/docs/esti/HCW-UI-UX-PRINCIPLES.md)

## Branding

| Field | Value |
| --- | --- |
| App name | **AQC Core** |
| Expansion | Accelerated Quantity and Costing |
| Suite | AORMS technical |
| Operator | Human Centric Works · Hospet (HCW) |

## Geography (cognitive jobs)

```text
Caption — document title only (OS drag)
MenuBar row — File · Edit · Help (dedicated strip)
Ribbon 56 — “AQC Core” once · persona · page · Local AI
Stage — flat command strip (active pillar only) + work
ActionDock ≤3 — New · Save · Publish
Taskbar 60 — Calc | Project · RCC · Civil · Outputs · More | Bind · Flush · Sync
Clock BR — Pomodoro
```

| Region | Role |
| --- | --- |
| **File** | New · Open · Save · Save as · Exit |
| **Edit** | Project / Engineering / Cost settings · Bind Connect |
| **Help** | About |
| **UI scale** | Shell `ScaleHost` **0.96×** (caption 1×) |
| Dropdowns | Item / overlay corner radius **0** |
| Hits | Dock **44** · taskbar **35** · chrome radius **8** |
| Hub sync | Taskbar Bind/Flush + chip — [`PROJECT-SYNC.md`](PROJECT-SYNC.md) |

Themes: `HcwTheme.xaml` · `HcwFluentBridge.xaml` · `FluentTokens.xaml`.

## Build / run (unpackaged)

```bat
msbuild BBSDesktop\BBSApp\BBSApp.csproj /p:Configuration=Release /p:Platform=x64 /p:WindowsPackageType=None
start BBSDesktop\BBSApp\bin\x64\Release\net8.0-windows10.0.19041.0\AQCCore.exe
```
