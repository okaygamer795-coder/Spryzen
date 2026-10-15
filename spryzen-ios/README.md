# Spryzen — on-device sideloading companion (SideStore-style)

Spryzen is the iOS companion to **SideLoadr** (the Windows app in `../src`).
Same idea as SideStore: a free Apple ID signs apps for **7 days**, and Spryzen
manages the on-device library, expiry tracking, and refreshes.

## Architecture (same split as SideStore + SideServer)

- **Spryzen (this app, on the iPhone)** — library UI, IPA import, Apple ID login,
  anisette fetch, expiry tracking, background refresh scheduling. Secrets in Keychain.
- **Helper (SideLoadr on a PC, or self-hosted)** — performs the privileged step:
  installing the resigned IPA over usbmux. Spryzen drives it via `HelperClient`
  (`POST {helper}/refresh`, `POST {helper}/install`).

First install is always via the PC helper over USB (iOS trusts only
properly-signed code — there is no way around a computer for step one).

## Build (requires a Mac with Xcode — an IPA cannot be compiled on Windows)

```bash
# install XcodeGen once
brew install xcodegen

cd spryzen-ios
xcodegen generate        # creates Spryzen.xcodeproj from project.yml
open Spryzen.xcodeproj   # build & run on your device from Xcode
```

Or without XcodeGen: create a blank iOS App project in Xcode named `Spryzen`
(bundle ID `com.spryzen.app`, iOS 16+) and drop the `Spryzen/` sources in.

Then in Xcode: set your free Apple ID team, build to your iPhone, and use
SideLoadr on the PC for the first install. After that, Spryzen handles refreshes
as long as the helper is reachable (same Wi-Fi, or self-hosted + VPN).

## Default anisette server

`AnisetteClient.defaultServerURL = https://ani.sidestore.io/v1/` — the public
server run by the SideStore team, pre-filled in Settings. Replace it with your
own anisette server if it ever stops working.

## Honest status

The app structure, library, Keychain storage, anisette client, auth flow, and
helper protocol are implemented. Like the PC side, the Apple login + portal calls
must be validated against a real Apple ID on first run — errors surface in the UI.
Background refresh re-signing end-to-end also needs on-device testing with a
reachable helper.
