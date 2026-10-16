Noctaxis for Windows
====================

Run Noctaxis.Desktop.exe in this folder. Nothing needs installing: the .NET runtime is included.
Keep the folder together; you can move it anywhere, for example into your user folder, and pin
Noctaxis.Desktop.exe to Start or the taskbar. To remove Noctaxis, delete the folder. Your settings,
saved locations and installed datasets are in %APPDATA%\Noctaxis; map and terrain caches, which
Noctaxis downloads again when needed, are in %LOCALAPPDATA%\Noctaxis\Cache. Only one Noctaxis runs
at a time: starting it again brings the open window forward.

The first launch
----------------
Noctaxis is not code-signed. Windows may show "Windows protected your PC" (Microsoft Defender
SmartScreen). Choose "More info", check the app is Noctaxis.Desktop.exe, then "Run anyway".
On Windows 11 with Smart App Control turned on, Windows blocks unsigned apps without offering that
choice, and Noctaxis will not start. Only download Noctaxis from its GitHub releases page.

Requires Windows 10 or 11 (64-bit). The x64 build is the officially supported one; the Arm64 build
is a courtesy build, checked automatically but offered on a best-effort basis.

Licence: Awoo Licence v2.0 (LICENSE).
Supporter definition: AWOO-SUPPORTER-SYSTEM.md.
Third-party notices: THIRD_PARTY_NOTICES.md.
