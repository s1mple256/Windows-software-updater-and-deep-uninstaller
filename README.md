# LuckyUpdater

LuckyUpdater is a Windows desktop application for viewing and managing software updates and uninstalling Winget-recognized applications.

## Features

- Scans available updates and installed packages with Windows Package Manager (`winget`).
- Shows software icons and version information.
- Updates one selected package at a time.
- Provides a staged uninstall flow with per-item evidence and confirmation before cleanup.
- Loads update lists and icons during the startup screen so the uninstall view can reuse the prepared cache.

## Requirements

- Windows with the Windows Package Manager (`winget`) available.
- .NET Framework 4.x compiler to build the current WinForms source.

## Build

Open PowerShell in this folder and run. The AI-generated background image is included and embedded in the executable.

```powershell
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /out:LuckyUpdater.exe /win32icon:LuckyUpdater.ico /resource:LuckyUpdater-landscape.png,LuckyUpdater.LuckyBackground.png /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Microsoft.CSharp.dll LuckyUpdater.cs
```

The background image is embedded into the executable under the resource name `LuckyUpdater.LuckyBackground.png`.

## Local protected paths

Machine-specific folders are not stored in this source tree. To configure extra folders that cleanup must protect, create:

```text
%LOCALAPPDATA%\LuckyUpdater\protected-paths.txt
```

Put one full folder path on each line. Environment variables such as `%USERPROFILE%` are expanded. See `protected-paths.example.txt` for examples. This local settings file is intentionally excluded from Git.

If the settings file cannot be read or contains an invalid path, cleanup is blocked for safety. If the file is missing, only the application's built-in broad-path safeguards apply; configure your important folders before using deep cleanup.

## License

- The source code, icon, and AI-generated background image are licensed under MIT; see `LICENSE`.
- Review the local protected-path settings on the computer where the app will run; do not commit that file.

Compiled `.exe` files and backups should not be committed with the source. If you want to distribute a ready-to-run build, publish it separately as a GitHub Release after reviewing it.
