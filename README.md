# Stadia HID Diagnostic (source preview 0.1.0)

A small, read-only Windows console utility to inspect the Google Stadia controller's HID interface and record raw input reports. It does not create a virtual controller, install a driver, disable devices, alter Bluetooth settings, or modify DS4Windows.

## What it does

- Enumerates Windows HID interface paths.
- Looks for the two path patterns used by Stadia-ViGEm: `VID_18D1&PID_9400` (USB) and `vid&0218d1_pid&9400` (Bluetooth), case-insensitively.
- Reports VID/PID, HID Usage Page/Usage, and input/output/feature report lengths.
- Opens a matched candidate with shared read/write access and overlapped I/O; it does not request exclusive access.
- Saves every raw input report to `logs\StadiaHidDiagnostic-<timestamp>.log`.
- Prints a tentative decode based on Stadia-ViGEm's `libstadia/src/stadia.c` layout when report ID `0x03` and at least 10 bytes are present.

The tentative decode is for investigation only. It is not yet confirmed against the user's physical Bluetooth controller.

## Run on Windows

1. Pair and wake the Stadia controller in Windows Bluetooth settings.
2. For this diagnostic run, exit Steam and other controller-remapping tools if possible so they are less likely to interfere with the HID read. Do not uninstall drivers, change adapter settings, or enable HidHide for this test.
3. Build/publish this project using .NET 8 SDK, or use the GitHub Actions workflow included in `.github/workflows/build.yml` to create a self-contained Windows x64 executable.
4. Run `StadiaHidDiagnostic.exe`. Administrator privileges should not normally be required.
5. Press each button separately, move each stick to its extremes and center, and pull each trigger from released to fully pressed. Leave a few seconds between controls. Press Ctrl+C to stop.
6. Send the generated log file for analysis. The log includes HID device paths; redact any path substring you consider personally identifying before sharing.

If no Stadia candidate is found, the tool does not start reading any other controller. Send the log so the actual Windows HID path can be reviewed.

## Build locally

```powershell
dotnet publish .\StadiaHidDiagnostic\StadiaHidDiagnostic.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

The program uses only .NET and Windows HID/SetupAPI APIs; no NuGet packages are required.
