# BassRouter

Simple Windows and Linux utility to route audio between headphones and a subwoofer.

## Why this exists

- Route treble to headphones and bass to subwoofer so that you can hear the vocals crystal clear but still get that room shaking effect
- This has been possible before with tools like Voicemeeter but those were too heavy and complex for my liking

> This is a purpose-built tool designed to do only one thing. Please do not open feature requests beyond the scope of this project.

## Features

- Adjust latency so that the headphones and speaker are perfectly synced up
- Detect that latency automatically with a microphone
- Adjust the low pass frequency to the range supported by your subwoofer

## Detecting latency

Press **Detect Latency**, pick a microphone and press start. BassRouter plays a few sweeps on your headphones and subwoofer (one at a time) and listens for them, then sets the artificial latency on whichever one plays first so they line up.

- Put the microphone where you sit. For headphones, hold an ear cup against it.
- Pause any other audio and keep the room quiet.
- Don't use the microphone built into a Bluetooth headset, using it switches the headset into a different mode with a different latency.

It only measures the difference between the two outputs, so the microphone's own latency doesn't matter. It takes about 15 seconds and starts routing if it isn't running already.

## Requirements

### Windows

You need *some sort* of kernel-level virtual audio driver. I just use the steam streaming speaker driver but it should support anything.

The reason this util doesn't come with it's own drivers is because you need a special EV certificate to sign them. So we just need to piggyback off an existing driver unfortunately.

### Linux

- [PipeWire](https://pipewire.org) with WirePlumber. This is the default audio setup on most modern distros (Fedora, Ubuntu 24.04+, Debian 12+, Linux Mint 22+, Arch, etc). If `pactl info` says `PulseAudio (on PipeWire ...)` you're good. Plain PulseAudio without PipeWire isn't supported.
- The .NET 10 runtime (`dotnet-runtime-10.0` on most distros).

No virtual driver needed, BassRouter creates its own virtual output device while it's running.

## Installing on Linux

Download the `linux-x64` (or `linux-arm64`) tarball from the releases page, extract it and run:

```sh
./install.sh
```

This installs BassRouter for your user and adds it to your app menu. Run `./install.sh --uninstall` to remove it.

To build it yourself instead:

```sh
dotnet run -c Release
```

### How it works on Linux

When you press start, BassRouter creates a virtual `BassRouter` output device, makes it the default, and loads two PipeWire filter chains (the same mechanism EasyEffects uses) that copy its audio to your headphones and a low passed version to your subwoofer. When you stop it, the previous default device is restored.

Since everything runs inside PipeWire's audio graph, both devices are kept in sync automatically even if they have different sample rates or clocks.

The volume sliders control BassRouter's own levels for each output. Your normal system volume controls the `BassRouter` device, so it works as a master volume for both.

The window hides to the system tray when closed. If your desktop doesn't have a tray (e.g. GNOME without the AppIndicator extension), closing the window quits instead.

## Limitations

- Your headphones and speakers need to have the same sample rate otherwise you might get some artifacting (Windows only, PipeWire handles this on Linux).
- Enabling spacial audio like windows sonic or dolby atmos might cause issues.

## Release Automation

Pushing to the `production` branch triggers [.github/workflows/production-release.yml](.github/workflows/production-release.yml), which:

- Builds release packages for `win-x64`, `win-arm64`, `linux-x64` and `linux-arm64`
- Publishes framework-dependent output for each runtime
- Signs `BassRouter.exe` with the certificate stored in GitHub Actions secrets
- Creates a GitHub Release tagged as `v<Version>` from `BassRouter.csproj`
- Uploads the Windows zip files and Linux tarballs to that release

Required repository secrets:

- `SIGNING_PFX_BASE64`: Base64-encoded contents of the code-signing `.pfx`
- `SIGNING_PASSWORD`: Password for that `.pfx`

Release versioning:

- The workflow reads the `<Version>` value from `BassRouter.csproj`
- That value must be valid semver, for example `1.2.3`
- Each production release must use a new version or the workflow will fail if `v<Version>` already exists