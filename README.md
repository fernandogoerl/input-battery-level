# Peripheral Battery Tray

A lightweight Windows 11 system-tray app that shows the battery level of your wireless
peripherals. It isn't tied to a fixed device list — each source describes whatever it finds
(keyboard, mouse, gamepad, headset, …) by kind:

- **Xbox controllers** connected through the Xbox Wireless Adapter (dongle)
- **Logitech** wireless devices on a Lightspeed / Bolt / Unifying receiver (keyboards, mice,
  headsets — e.g. G915 X, G502 X), exact %
- **Razer**, **Corsair**, and **Astro** wireless devices *(implemented, pending hardware
  verification — see [Device support](#device-support))*
- **Any paired Bluetooth device** whose battery Windows tracks (headphones, BLE mice/keyboards, …)

No vendor software (Logitech G HUB, Razer Synapse, Xbox Accessories, …) required — it talks
to the hardware directly.

The tray icon is a battery glyph colored by the **lowest** device (green / amber / red)
with the percentage below it. **Left-click** toggles a battery panel (per-device bars and
levels); **right-click** opens the action menu (refresh, "Start with Windows", exit). Hover
still shows a quick text tooltip. A balloon warns once when any device drops below 15%.

---

## Install

Download the latest **`BatteryTray-Setup-<version>.exe`** from the
[Releases](https://github.com/fernandogoerl/input-battery-level/releases) page and run it.
It's a per-user install (no admin), goes to `%LocalAppData%\Programs\BatteryTray`, and can
start automatically at sign-in. The installer bundles the .NET runtime, so there's nothing
else to install; it checks up front that you're on 64-bit Windows 10 1809+ and stops with a
clear message otherwise. Uninstall from Windows *Add or remove programs*.

To build the installer yourself, see [`installer/`](installer/) and run `installer\build.ps1`
(needs the .NET 8 SDK and [Inno Setup 6](https://jrsoftware.org/isinfo.php)).

---

## How it reads each device

Each source implements a common `IBatterySource` and is registered in one place
(`Sources/BatterySources.cs`). Adding a device or brand is a one-line change there.

| Brand / class | Method | Detail |
| --- | --- | --- |
| Xbox controllers | **XInput** (`XInputGetBatteryInformation`, `xinput1_4.dll`) | Coarse buckets: Empty / Low / Medium / Full (this is all Xbox controllers expose). |
| Logitech wireless | **HID++ 2.0** over raw HID | Exact %. `UnifiedBattery (0x1004)` → voltage `0x1001` → legacy `0x1000`. |
| Razer wireless | **OpenRazer HID feature report** | 90-byte razer report, command class `0x07` (`0x80` level, `0x84` charging). |
| Corsair wireless | **Bragi read-property** over HID | Battery level property `0x0F`, charging `0x10`. |
| Astro A50 | **Base-station HID status frame** | Battery nibble from the status report. |
| Any Bluetooth device | **Windows PnP battery property** (`DEVPKEY_Bluetooth_Battery`) via WinRT | Exact %. The same value Settings shows; covers BLE (GATT Battery Service) and classic BT. |

The Logitech path speaks the same protocol as Solaar / libratbag: it finds each Lightspeed
receiver's HID++ "long report" interface (usage page `0xFF00`, report id `0x11`), pings the
paired device, resolves the battery feature, and reads `DeviceNameAndType (0x0005)` for the
friendly name and kind.

### Device support

Verified working on real hardware:

```
[Mouse]    G502 X LIGHTSPEED: 80%
[Keyboard] G915 X LS TKL:     98%
[Gamepad]  Xbox Controller 1: Medium (~60%)
```

The **Razer**, **Corsair**, and **Astro** sources are implemented from public protocol
documentation but **not yet verified against hardware** (I don't own those devices). They are
VID-guarded and fail closed — if a device doesn't answer with a plausible reading, the source
reports nothing, so they can't affect the devices above. The **Bluetooth** source likewise
reads the exact property Settings uses but hasn't been exercised with a battery-reporting BT
device. Run `--diag` with the hardware attached to confirm and, if needed, tune the offsets.

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
    BatterySources.cs        Central registry — CreateAll() lists every source
    XInputXboxSource.cs      Xbox via XInput P/Invoke
    LogitechHidppSource.cs   Logitech wireless via HID++ 2.0
    RazerSource.cs           Razer wireless via the OpenRazer feature-report protocol
    CorsairSource.cs         Corsair wireless via the Bragi read-property protocol
    AstroSource.cs           Astro A50 base station via its status frame
    BluetoothBatterySource.cs  Paired Bluetooth devices via WinRT PnP battery property
  Rendering/
    IconRenderer.cs          Draws the tray battery glyph
```

The installer lives in [`installer/`](installer/): `BatteryTray.iss` (Inno Setup script)
and `build.ps1` (publish + compile).

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
- If a device shows nothing, make sure it's paired to *its* dongle and awake. The `--diag`
  output lists every HID interface it can see per supported vendor (with report lengths),
  which helps debugging and verifying the not-yet-hardware-tested brand sources.
- **Bluetooth shows only what Windows already tracks.** A device appears once it's paired and
  Windows has read its battery (the same value in Settings › Bluetooth & devices). Devices
  that don't report battery, or are disconnected/asleep, won't appear. Charge state isn't
  exposed for Bluetooth, so those never show the ⚡ charging mark.
- Standard user rights are enough — no admin, no elevation.
