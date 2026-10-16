Noctaxis for Linux
==================

This is a courtesy build: Noctaxis is developed and supported on Windows x64. This build is
checked automatically on Linux and offered on a best-effort basis; problem reports are welcome.

Run ./Noctaxis.Desktop in this folder. Nothing needs installing: the .NET runtime is included.
To add Noctaxis to your application menu, run ./install-desktop-entry.sh (for your user only, no
root); ./install-desktop-entry.sh --remove takes it away again. To remove Noctaxis, remove the menu
entry and delete the folder. Your settings, saved locations and installed datasets are in
~/.config/Noctaxis (or $XDG_CONFIG_HOME/Noctaxis); map and terrain caches, which Noctaxis downloads
again when needed, are in ~/.cache/Noctaxis (or $XDG_CACHE_HOME/Noctaxis). Only one Noctaxis runs
at a time for your user.

Needs an X11 or XWayland desktop session, fontconfig and the usual desktop libraries
(libX11, libICE, libSM). Opening web links uses xdg-open. Both the x64 and arm64 packages are
built and started automatically on their own kind of system.

Licence: Awoo Licence v2.0 (LICENSE).
Supporter definition: AWOO-SUPPORTER-SYSTEM.md.
Third-party notices: THIRD_PARTY_NOTICES.md.
