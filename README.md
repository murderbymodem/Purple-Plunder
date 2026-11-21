# Purple Plunder

A fast, easy to use VOD downloader for Twitch, with 1440p support and a channel monitor that records broadcasts while they are live, so you keep the original audio.

## What's new in Purple Plunder

- **1440p downloads**: Twitch hides the 1440p (HEVC) source quality of enhanced broadcasts from its VOD playlists. Purple Plunder finds it anyway and lists it as `Source (2560x1440, HEVC)`, next to the usual qualities.
- **Channel monitor**: add the channels you care about and Purple Plunder checks every 15 seconds whether they are live. When one goes live, its VOD is recorded automatically.
- **Live capture before muting**: the VOD is downloaded while the stream is running, before Twitch's copyright check mutes parts of it and deletes the original audio. The download is finished seconds after the stream ends.
- **Crash-safe recordings**: live captures are written to disk as they happen. A recording cut off by a crash or power loss stays playable and is finished automatically on the next start.
- **Muted chunk fallback**: when the original version of a chunk is gone, Purple Plunder downloads the muted version instead of leaving a gap in the video.
- **Codec aware quality list**: qualities show their codec when it is not H.264, e.g. `HEVC` or `AV1`. A new `1440p` option is available for the default download quality, and `Source` always picks the best available quality.
- **Keeps your PC awake** while a live capture is running (Windows, and Linux desktops such as KDE Plasma).
- **Self-contained Windows build**: a single `PurplePlunder.exe`, no .NET installation needed.
- **Linux AppImage**: one file that runs on any modern 64-bit distribution, built with KDE Plasma integration in mind.

## Features

- Very easy to use, no manual needed
- Downloads thousands of video chunks in parallel, using all of your bandwidth
- Browse past broadcasts, uploads and highlights within the application
- Search by channel, VOD URL or VOD ID
- Sub-only VOD downloads (with your own subscription)
- Audio-only downloads
- Download only a time range of a VOD
- Queue multiple downloads
- Default search parameters, download quality, download folder and filename template with wildcards
- Save favorite channels and search presets
- Downloads VODs that are partly unavailable due to missing chunks
- Free and open source, built on .NET 10

## Download (Windows)

Grab the latest `PurplePlunder-vX.Y.Z-win-x64.zip` from the [Releases](https://github.com/murderbymodem/Purple-Plunder/releases) page, unzip it anywhere and run `PurplePlunder.exe`.

- Windows 10/11, 64 bit
- No .NET installation required
- Keep `ffmpeg.exe` in the same folder as `PurplePlunder.exe`

## Download (Linux AppImage)

Grab the latest `PurplePlunder-X.Y.Z-x86_64.AppImage` from the [Releases](https://github.com/murderbymodem/Purple-Plunder/releases) page, make it executable and run it:

```bash
chmod +x PurplePlunder-*-x86_64.AppImage
```

```bash
./PurplePlunder-*-x86_64.AppImage
```

In Dolphin you can also right-click the file, open **Properties → Permissions**, tick **Is executable** and double-click it.

- 64-bit (x86_64) distributions; no .NET or FFmpeg installation needed, both are included
- Runs without `libfuse2`, which current distributions no longer install by default
- To get a Purple Plunder entry in the application launcher, use an AppImage manager such as [Gear Lever](https://flathub.org/apps/it.mijorus.gearlever) or [AppImageLauncher](https://github.com/TheAssassin/AppImageLauncher)

KDE Plasma notes:

- Folder and file pickers use the KDE file dialog (through xdg-desktop-portal-kde, installed with Plasma by default).
- "Open download folder" opens Dolphin with the video selected; links open in your default browser.
- The taskbar and task switcher show the Purple Plunder icon and group its windows.
- While a live stream is recorded, Purple Plunder holds a systemd-logind sleep inhibitor, which Plasma's power management respects, so the PC does not go to sleep mid-recording.
- On Plasma's Wayland session the app runs through XWayland. If it looks too small with display scaling, start it with a scale factor, e.g. `AVALONIA_GLOBAL_SCALE_FACTOR=1.5 ./PurplePlunder-*-x86_64.AppImage` (in Gear Lever or a launcher entry, add it as an environment variable).

## Using the channel monitor

1. Log in with your Twitch account (needed to check which channels are live).
2. Open **Monitor** in the title bar and add channels by name or URL.
3. Leave Purple Plunder running. When a channel goes live, a recording appears under **Downloads** and finishes on its own once the stream ends.

Good to know:

- Purple Plunder has to stay open for the monitor to work.
- The recording always starts from the beginning of the VOD, even if Purple Plunder notices the stream a little late.
- Channels that don't save their broadcasts as VODs can't be recorded this way.
- Recordings use your default download quality, folder and filename template from the preferences.
- While recording, the video is saved as `<name>.recording.mp4` in your download folder and written to disk continuously. If Purple Plunder or your PC stops unexpectedly, that file plays up to the moment it stopped, and the next time Purple Plunder starts it turns it into a normal video file automatically.
- When the stream ends, the recording is turned into the final video in seconds, without re-encoding, so the quality is exactly what Twitch delivered.

## How it is so fast

Most VOD downloaders let FFmpeg download the video, which rarely gets anywhere near the speed of your internet connection. Purple Plunder doesn't use FFmpeg for downloading at all. It downloads the small video chunks of a VOD in parallel, then FFmpeg only merges them into a single video file.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) and PowerShell.

FFmpeg is not stored in the repository. Download the pinned, checksum-verified build once:

```bash
pwsh scripts/get-ffmpeg.ps1
```

Then build:

```bash
dotnet publish PurplePlunder/PurplePlunder/PurplePlunder.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

To build the Linux AppImage (needs the .NET 10 SDK, `curl` and `tar`):

```bash
scripts/build-appimage.sh
```

A plain Linux build (`dotnet publish ... -r linux-x64`) without the AppImage uses the `ffmpeg` found on your `PATH`.

Releases are built automatically by GitHub Actions when a `v*` tag (e.g. `v4.1.0`) is pushed: the Windows zip and the Linux AppImage are attached to the release.

## Support & Issues

If you have a problem or a feature request, [open an issue](https://github.com/murderbymodem/Purple-Plunder/issues). Please include:

- The version of Purple Plunder you are using
- The VOD URL, channel or ID
- Your operating system (e.g. Windows 11 64 bit)
- A description of the problem
- For download problems, the download log (the log button on the download)

## Legal Disclaimer

1. **Purpose of the software.** Purple Plunder is a technical tool for personal use only. It is intended for downloading video content for offline viewing, archiving or personal backups (e.g. creators saving their own broadcasts).

2. **User responsibility.** By using this software you agree to comply with all applicable copyright laws and the Twitch Terms of Service. The developer does not encourage, support or induce copyright infringement or unauthorized distribution of content. You are solely responsible for the legality of the content you download.

3. **Access to restricted content.** This tool does not bypass or circumvent any digital rights management (DRM) or payment barriers. It only accesses publicly available video fragments. Sub-only VODs require a valid user-authorized token and can only be downloaded if you already have access to them through your own active subscription.

4. **No affiliation.** Purple Plunder is an independent open-source project and is not affiliated with, authorized, maintained, sponsored or endorsed by Twitch Interactive, Inc., Amazon.com, Inc., or any of their affiliates or subsidiaries. "Twitch" is a registered trademark of Twitch Interactive, Inc.

5. **Limitation of liability.** The software is provided "as is", without warranty of any kind. The developer shall not be liable for any claims, damages or other liabilities, including account suspensions or legal actions resulting from the use of this tool.

## Credits

Claudecoded by murderbymodem.

Purple Plunder is based on TwitchLeecher-DX by Manuel "Brainy" Schneider, which is a fork of Twitch Leecher by Dominik "Franiac" Rebitzer.

## License

Purple Plunder is licensed under the [MIT License](LICENSE), like the projects it is based on.

The Windows and Linux releases also contain third-party software, most notably FFmpeg (GPL-3.0), which ships as a separate program. Their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and included with every release (the `licenses` folder of the Windows zip, `usr/share/doc/purple-plunder` inside the AppImage).
