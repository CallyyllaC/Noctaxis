# Noctaxis

[![Windows x64 build](https://github.com/CallyyllaC/Noctaxis/actions/workflows/build-win-x64.yml/badge.svg?branch=main&event=push)](https://github.com/CallyyllaC/Noctaxis/actions/workflows/build-win-x64.yml?query=branch%3Amain) [![Windows ARM64 build](https://github.com/CallyyllaC/Noctaxis/actions/workflows/build-win-arm64.yml/badge.svg?branch=main&event=push)](https://github.com/CallyyllaC/Noctaxis/actions/workflows/build-win-arm64.yml?query=branch%3Amain) [![Linux x64 build](https://github.com/CallyyllaC/Noctaxis/actions/workflows/build-linux-x64.yml/badge.svg?branch=main&event=push)](https://github.com/CallyyllaC/Noctaxis/actions/workflows/build-linux-x64.yml?query=branch%3Amain) [![Linux ARM64 build](https://github.com/CallyyllaC/Noctaxis/actions/workflows/build-linux-arm64.yml/badge.svg?branch=main&event=push)](https://github.com/CallyyllaC/Noctaxis/actions/workflows/build-linux-arm64.yml?query=branch%3Amain) [![macOS x64 build](https://github.com/CallyyllaC/Noctaxis/actions/workflows/build-osx-x64.yml/badge.svg?branch=main&event=push)](https://github.com/CallyyllaC/Noctaxis/actions/workflows/build-osx-x64.yml?query=branch%3Amain) [![macOS ARM64 build](https://github.com/CallyyllaC/Noctaxis/actions/workflows/build-osx-arm64.yml/badge.svg?branch=main&event=push)](https://github.com/CallyyllaC/Noctaxis/actions/workflows/build-osx-arm64.yml?query=branch%3Amain)

[![Windows x64 tests](https://github.com/CallyyllaC/Noctaxis/actions/workflows/tests-win-x64.yml/badge.svg?branch=main&event=push)](https://github.com/CallyyllaC/Noctaxis/actions/workflows/tests-win-x64.yml?query=branch%3Amain) [![Linux x64 tests](https://github.com/CallyyllaC/Noctaxis/actions/workflows/tests-linux-x64.yml/badge.svg?branch=main&event=push)](https://github.com/CallyyllaC/Noctaxis/actions/workflows/tests-linux-x64.yml?query=branch%3Amain) [![macOS ARM64 tests](https://github.com/CallyyllaC/Noctaxis/actions/workflows/tests-osx-arm64.yml/badge.svg?branch=main&event=push)](https://github.com/CallyyllaC/Noctaxis/actions/workflows/tests-osx-arm64.yml?query=branch%3Amain)

*Build badges cover packaging and startup checks on the corresponding hosted runners. Automated tests run on Windows x64 and Linux x64, with an informational run on macOS ARM64. See [CI details](docs/releasing.md#continuous-integration).*

**[Wiki](https://github.com/CallyyllaC/Noctaxis/wiki)** · **[Getting Started](docs/user-guide/getting-started.md)** · [Locations](docs/user-guide/locations.md) · [Planner](docs/user-guide/planner.md) · [Settings](docs/user-guide/settings.md)

Noctaxis is my desktop planning application for photography and astrophotography. It brings locations, celestial targets, camera framing, terrain, light pollution, weather and time into one workspace. It gives you the information to plan a shot without deciding the composition for you.

![Noctaxis Planner showing the map, camera framing, Inspector and Timeline](images/wiki/planner-overview.png)

## Getting Noctaxis

Noctaxis is in active development and doesn't have a stable release yet. Start with [Installing Noctaxis](docs/user-guide/installing.md), then follow [Getting Started](docs/user-guide/getting-started.md). The [wiki](https://github.com/CallyyllaC/Noctaxis/wiki) covers the Planner and the rest of the application. If you're working on the project itself, see [building and releasing](docs/releasing.md).

## Platform support

**Windows x64 is the officially supported platform.** I develop and test Noctaxis on Windows.

I also offer best-effort builds for Windows ARM64, Linux (x64 and ARM64), and macOS (Intel and Apple silicon). Automated checks help catch problems, but I don't test those platforms on physical hardware or promise fixes for every platform-specific issue.

The app isn't currently code-signed, so your operating system might warn you when opening it. See the [installation guide](docs/releasing.md#what-users-see-without-trusted-signing) for details.

## Licence and contributions

Noctaxis is free to use and source-available under my [AWOO Licence v2.0](LICENSE). You can use it for personal projects and paid photography. Selling or otherwise commercially exploiting the software itself requires [separate permission](COMMERCIAL-LICENSING.md).

Optional appearance themes are available through the [Awoo supporter system](AWOO-SUPPORTER-SYSTEM.md). Third-party libraries and data have their own licences and credits in [Third-party notices](THIRD_PARTY_NOTICES.md).

I welcome contributions. Please read [Contributing](CONTRIBUTING.md) and the licence terms before submitting code or other original work. For licensing enquiries, contact [licensing@awoo.ltd](mailto:licensing@awoo.ltd).

Copyright © 2026 CallyyllaC.
