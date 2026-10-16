Noctaxis for macOS
==================

This is a courtesy build: Noctaxis is developed and supported on Windows x64. This build is
checked automatically on macOS and offered on a best-effort basis; problem reports are welcome.

Drag Noctaxis.app to Applications, then open it. Nothing else needs installing: the .NET runtime
is included. Requires macOS 14 (Sonoma) or later. Choose the arm64 download for Apple silicon
(M1 and later) and the x64 download for Intel Macs.

The first launch
----------------
Noctaxis is not notarised by Apple, so macOS will not open it the first time and says it cannot
verify the app. To open it:
  1. Open Noctaxis once and close the message (choose Done, not Move to Bin).
  2. Open System Settings > Privacy & Security, scroll to Security, and choose "Open Anyway"
     beside the message about Noctaxis. Confirm with your password.
From then on Noctaxis opens normally. Only download Noctaxis from its GitHub releases page.

Your settings, saved locations and installed datasets are in ~/Library/Application Support/Noctaxis;
map and terrain caches, which Noctaxis downloads again when needed, are in ~/Library/Caches/Noctaxis.
To remove Noctaxis, delete the app and both folders. Only one Noctaxis runs at a time for your user.

Licence: Awoo Licence v2.0 (LICENSE inside Noctaxis.app/Contents/MacOS).
Supporter definition: AWOO-SUPPORTER-SYSTEM.md in the same directory.
Third-party notices: THIRD_PARTY_NOTICES.md in the same directory.
