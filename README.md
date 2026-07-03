# Peripheral Battery Tray

A lightweight Windows 11 system-tray app that shows the battery level of:

- **Xbox One / Series controller** connected through the Xbox Wireless Adapter (dongle)
- **Logitech G915 X LIGHTSPEED TKL** keyboard (Lightspeed dongle)
- **Logitech G502 X LIGHTSPEED** mouse (Lightspeed dongle)
- **Any paired Bluetooth device** whose battery Windows tracks (headphones, BLE mice/keyboards, …)

No Logitech G HUB or Xbox Accessories app required — it talks to the hardware directly.

The tray icon is a battery glyph colored by the **lowest** device (green / amber / red)
with the percentage below it. **Left-click** toggles a battery panel (per-device bars and
levels); **right-click** opens the action menu (refresh, "Start with Windows", exit). Hover
still shows a quick text tooltip. A balloon warns once when any device drops below 15%.

---

## How it reads each device

| Device | Method | Detail |
| --- | --- | --- |
| Xbox controller | **XInput** (`XInputGetBatteryInformation`, `xinput1_4.dll`) | Coarse buckets: Empty / Low / Medium / Full (this is all Xbox controllers expose). |
| G915 X keyboard | **Logitech HID++ 2.0** over raw HID | Exact %. Uses feature `UnifiedBattery (0x1004)`. |
| G502 X mouse | **Logitech HID++ 2.0** over raw HID | Exact %. Same path as the keyboard. |
| Any Bluetooth device | **Windows PnP battery property** (`DEVPKEY_Bluetooth_Battery`) via WinRT | Exact %. The same value Settings shows; covers BLE (GATT Battery Service) and classic BT. |

The Logitech path speaks the same protocol as Solaar / libratbag: it finds each Lightspeed
receiver's HID++ "long report" interface (usage page `0xFF00`, report id `0x11`), pings the
paired device, resolves the battery feature (`0x1004` → falls back to voltage `0x1001` →
legacy `0x1000`), and reads `DeviceNameAndType (0x0005)` for the friendly name and icon.

Verified working on the target hardware:

```
[Mouse]    G502 X LIGHTSPEED: 81%
[Keyboard] G915 X LS TKL:     59%
[Xbox]     Xbox Controller 1: Medium (~60%)
```

---

## Build & run

Requires the **.NET 8 SDK** (`winget install Microsoft.DotNet.SDK.8`).

```powershell
cd src/BatteryTray

# Run from source
dotnet run -c Release

# One-shot hardware probe (no UI) — prints what each source sees, over several poll rounds
dotnet run -c Release -- --diag "$env:TEMP\battery_diag.txt" ; Get-Content "$env:TEMP\battery_diag.txt"

# Self-test the poll/merge logic (asleep-device handling); exit code 0 = all passed
dotnet run -c Release -- --selftest "$env:TEMP\battery_selftest.txt" ; Get-Content "$env:TEMP\battery_selftest.txt"
```

### Produce a single exe

**Small (recommended)** — needs the .NET 8 Desktop Runtime installed (~0.4 MB exe):

```powershell
cd src/BatteryTray
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

**Portable** — bundles the runtime, runs on any Windows 10/11 machine (~154 MB exe):

```powershell
cd src/BatteryTray
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Both output to `bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/BatteryTray.exe`. Copy it
anywhere and double-click. Use the tray menu's **Start with Windows** to launch it at login
(it writes an `HKCU\...\Run` entry pointing at wherever the exe currently lives — keep the
exe in a stable folder).

---

## Project layout

```
src/BatteryTray/
  Program.cs                 Entry point + single-instance guard + --diag mode
  TrayContext.cs             NotifyIcon, menu, 60s poll loop, low-battery balloon, startup toggle
  BatteryReading.cs          Model + IBatterySource interface
  BatteryFlyout.cs           Left-click battery panel (owner-drawn borderless form)
  Diagnostics.cs             --diag hardware probe (multi-round)
  SelfTest.cs                --selftest for the asleep-device merge logic
  Preview.cs                 --previewflyout renders the panel to a PNG
  Sources/
    XInputXboxSource.cs      Xbox via XInput P/Invoke
    LogitechHidppSource.cs   G915 X / G502 X via HID++ 2.0
    BluetoothBatterySource.cs  Paired Bluetooth devices via WinRT PnP battery property
  Rendering/
    IconRenderer.cs          Draws the tray battery glyph
```

## Notes & limits

- **Sleeping devices stay visible.** Wireless mice/keyboards (and Xbox controllers) power
  down when idle and stop reporting. The tray remembers each device's last reading and shows
  it as `81% (asleep, 3m ago)` instead of dropping it. A device first appears only *after* it
  has responded once — so a mouse/keyboard that's asleep at launch shows up the moment you
  use it. Devices unheard-from for 12h are forgotten.
- **Polls every 60s** (plus on double-click or "Refresh now"). "Refresh now" re-reads awake
  devices immediately; asleep ones keep their last-known value until they wake.
- **Xbox is coarse by design.** The controller firmware only reports four levels; the "~%"
  shown is an approximation for the icon fill. The Logitech values are exact.
- If a Logitech device shows nothing, make sure it's paired to *its* dongle and awake. The
  `--diag` output lists every Logitech HID interface it can see, which helps debugging.
- **Bluetooth shows only what Windows already tracks.** A device appears once it's paired and
  Windows has read its battery (the same value in Settings › Bluetooth & devices). Devices
  that don't report battery, or are disconnected/asleep, won't appear. Charge state isn't
  exposed for Bluetooth, so those never show the ⚡ charging mark.
- Standard user rights are enough — no admin, no elevation.
