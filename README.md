# FastScreenRecorder

A tiny screen recorder for Windows. Press F9, record, get an MP4.

- Screen, window or any area, with system sound, microphone and cursor
- Frames stay on the GPU and are encoded by NVIDIA, Intel or AMD hardware, so 60 fps stays smooth while you game or work
- Pause as often as you like; when you stop, the parts are joined losslessly into one file
- No editor, no cloud, no sign-up. It never connects to the internet

## Download

Get **FastScreenRecorder-&lt;version&gt;-win64.zip** from [Releases](https://github.com/SYTILL/FastScreenRecorder/releases/latest), unzip it anywhere and run `FastScreenRecorder.exe`. Windows 10 / 11, 64-bit. FFmpeg and the .NET runtime are included.

## Build

Requires the .NET 9 SDK.

```
dotnet publish -c Release
```

Put `ffmpeg.exe` next to the built exe (the app looks there first, then on PATH).

## Third-party software

FFmpeg (GPL v3, run as a separate program), .NET runtime (MIT), NAudio (MIT), IBM Plex fonts (OFL 1.1). Details in [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).
