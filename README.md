# Peripheral Battery Tray

A small Windows system tray app that shows the battery level of wireless peripherals such as
game controllers, keyboards, mice, and headsets. It talks to the hardware directly, so you do
not need Logitech G HUB, Razer Synapse, the Xbox Accessories app, or any other vendor software
running in the background.

The tray icon is a battery glyph, colored green, amber, or red based on whichever device is
lowest, with the percentage below it. Left-click opens a small panel listing each device and
its charge. Right-click opens a menu with Refresh, "Start with Windows", and Exit.

## Contents

- [Features](#features)
- [Supported devices](#supported-devices)
- [Requirements](#requirements)
- [Installation](#installation)
- [Usage](#usage)
- [Building from source](#building-from-source)
- [How it works](#how-it-works)
- [Project structure](#project-structure)
- [Notes and limitations](#notes-and-limitations)
- [License](#license)

## Features

- Shows battery for wireless controllers, keyboards, mice, and headsets in one place.
- No vendor software required; it reads the devices directly.
- Not tied to a fixed device list. Each device is described by what it is (keyboard, mouse,
  gamepad, headset, speaker).
- Left-click battery panel and a right-click action menu.
- Polls every 30 seconds and refreshes immediately when a USB device is plugged in or removed.
- Remembers a device's last reading when it goes to sleep instead of dropping it from the list.
- Warns once with a tray balloon when a device drops below 15 percent.
- Optional "Start with Windows".
- Light and dark theme aware.

## Supported devices

Every device type is read by a small self-contained source. Adding support is a one-line change
in `Sources/BatterySources.cs`.

| Device                | How it is read                                              | Notes                                                                                  |
| --------------------- | ---------------------------------------------------------- | -------------------------------------------------------------------------------------- |
| Xbox controllers      | Windows.Gaming.Input battery report, with XInput as backup | Charging state and, for rechargeable packs, an exact percent. XInput adds the coarse Empty/Low/Medium/Full bucket. |
| Logitech wireless     | HID++ 2.0 over raw HID                                      | Exact percent. Works with Lightspeed, Bolt, and Unifying receivers.                    |
| Razer wireless        | OpenRazer HID feature report                               | See the note below about hardware verification.                                        |
| Corsair wireless      | Bragi read-property over HID                               | See the note below about hardware verification.                                        |
| Astro A50             | Base station HID status frame                              | See the note below about hardware verification.                                        |
| Bluetooth devices     | Windows PnP battery property via WinRT                     | Exact percent. The same value the Settings app shows, for paired BLE and classic BT.   |

The Xbox, Logitech, and Bluetooth paths are the ones verified in day to day use.

The Razer, Corsair, and Astro sources are implemented from public protocol documentation but
have not been verified against real hardware yet. They only act on their own vendor IDs and
fail closed, so if a device does not answer with a plausible reading the source reports nothing
and cannot affect the verified devices. If you have one of these devices, run the app with
`--diag` (see [Usage](#usage)) and the output will show whether the source is reading it.

## Requirements

- 64-bit Windows 10 version 1809 (build 17763) or newer, or Windows 11.
- Nothing else to run the installer build. It bundles the .NET runtime.
- To build from source you need the .NET 8 SDK.

## Installation

Download the latest `BatteryTray-Setup-x.y.z.exe` from the
[Releases](https://github.com/fernandogoerl/input-battery-level/releases) page and run it.

The installer is per-user and does not require administrator rights. It installs to
`%LocalAppData%\Programs\BatteryTray`, checks that you are on a supported version of Windows,
and offers to start the app automatically when you sign in. To remove it, use "Add or remove
programs" in Windows Settings.

The installer is not code signed, so Windows SmartScreen may show a warning the first time you
run it. Choose "More info" and then "Run anyway".

## Usage

Once running, the app sits in the notification area:

- Left-click the tray icon to open the battery panel with a row per device.
- Right-click for the menu: Refresh now, Start with Windows, and Exit.
- Hover over the icon for a short text summary.

The app polls every 30 seconds, and also refreshes as soon as any USB device is connected or
disconnected. "Refresh now" forces an immediate read.

It also has a few command line modes, mainly for debugging and development:

```powershell
# Probe the hardware without a UI and print what each source sees, over several poll rounds
BatteryTray.exe --diag report.txt

# Run the internal checks for the sleep/merge logic; exit code 0 means all passed
BatteryTray.exe --selftest result.txt
```

## Building from source

You need the .NET 8 SDK. From the repository root:

```powershell
# Run from source
dotnet run --project src/BatteryTray -c Release

# Produce a self-contained single file exe (bundles the runtime, runs anywhere)
dotnet publish src/BatteryTray/BatteryTray.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# Produce a small exe that needs the .NET 8 Desktop Runtime installed
dotnet publish src/BatteryTray/BatteryTray.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true
```

To build the installer, install [Inno Setup 6](https://jrsoftware.org/isinfo.php) and run:

```powershell
installer\build.ps1
```

This publishes the self-contained exe and compiles it into
`dist\BatteryTray-Setup-x.y.z.exe`.

## How it works

Each device type implements a common `IBatterySource` interface and is listed in one place,
`Sources/BatterySources.cs`. Both the tray app and the `--diag` probe build their source list
from that registry.

The Logitech source speaks the same HID++ 2.0 protocol as Solaar and libratbag. It finds each
receiver's HID++ long report interface, pings the paired device, resolves a battery feature
(unified battery, then voltage, then the legacy battery status), and reads the device name and
type for the label.

The Xbox source combines two APIs. Windows.Gaming.Input provides a charging state and, for
rechargeable packs, an exact percentage. XInput is used as a fallback for presence and the
coarse battery bucket, and to detect a controller connected by cable as the input device, which
shows as "Wired".

Polling runs every 30 seconds on a timer, on any USB device change, and on demand. Sources are
polled in parallel under an 8 second budget so a slow source cannot stall the refresh, and a
refresh requested while another is in flight is queued rather than dropped.

## Project structure

```
src/BatteryTray/
  Program.cs                 Entry point, single instance guard, command line modes
  TrayContext.cs             Tray icon, menu, poll loop, low battery balloon, startup toggle
  BatteryReading.cs          Battery model, IBatterySource interface, DeviceKind
  BatteryFlyout.cs           The left-click battery panel
  DeviceChangeWatcher.cs     Watches for USB plug and unplug to trigger a refresh
  Diagnostics.cs             The --diag hardware probe
  SelfTest.cs                The --selftest checks for the sleep/merge logic
  Preview.cs                 Renders the battery panel to a PNG for visual checks
  Sources/
    BatterySources.cs        Registry that lists every source
    XboxControllerSource.cs  Xbox via Windows.Gaming.Input with an XInput fallback
    LogitechHidppSource.cs   Logitech wireless via HID++ 2.0
    RazerSource.cs           Razer wireless via the OpenRazer feature report protocol
    CorsairSource.cs         Corsair wireless via the Bragi read-property protocol
    AstroSource.cs           Astro A50 base station status frame
    BluetoothBatterySource.cs  Paired Bluetooth devices via the WinRT battery property
  Rendering/
    IconRenderer.cs          Draws the tray battery glyph

installer/
  BatteryTray.iss            Inno Setup script (per-user, with a requirements check)
  build.ps1                  Publish and compile the installer
```

## Notes and limitations

- Sleeping devices stay visible. Wireless mice, keyboards, and controllers power down when idle
  and stop reporting. The app keeps the last reading and shows it as "asleep" with how long ago
  it was seen, rather than dropping the device. A device first appears only after it has
  responded once, so a peripheral that is asleep at launch shows up the moment you use it.
  Devices not heard from for 12 hours are forgotten.
- Xbox battery detail depends on the controller. A rechargeable pack reports charging and an
  exact percent. Disposable AA batteries report only the coarse Empty, Low, Medium, or Full
  bucket, and no charging state.
- Logitech devices need to be paired to their own receiver and awake. If one shows nothing,
  the `--diag` output lists the HID interfaces it can see, which helps.
- Bluetooth shows only what Windows already tracks. A device appears once it is paired and
  Windows has read its battery, the same value shown in Settings. Charging state is not exposed
  for Bluetooth.
- Only one process can read the Logitech receivers at a time. The app uses a single instance
  lock, so do not run a second copy or a `--diag` probe while the tray app is live.
- Standard user rights are enough. No administrator access is required.

## License

This is a personal project and does not currently carry a formal open-source license. If you
want to reuse the code, please open an issue to discuss it.
