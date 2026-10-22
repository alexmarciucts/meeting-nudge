# MeetingNudge ⏰

A full-screen, flashing, impossible-to-ignore reminder before every meeting on your Google Calendar —
with a big **JOIN NOW** button that drops you straight into Zoom / Meet / Teams.

- Alerts as many times as you like before each meeting (e.g. 10, 5 and 1 min)
- Your own reminders too: once, daily, weekdays or weekly
- Skips Focus Time, lunch blocks, all-day events and meetings you declined
- Lives in the menu bar (Mac) or system tray (Windows) and shows the time to your next meeting
- Your calendar link stays on your Mac; the app only talks to Google

Works on **macOS** and **Windows**.

## Install on Mac

Paste this into **Terminal** and press Enter:

```bash
d=$(mktemp -d) && curl -fsSL https://github.com/alexmarciucts/meeting-nudge/archive/refs/heads/main.tar.gz | tar xz -C "$d" && bash "$d"/meeting-nudge-main/install.sh
```

It builds the app on your Mac (first time: macOS may ask to install Apple's free Command Line Tools —
click Install, then run the command again). Re-run the same command any time to update.

## Install on Windows

Open **PowerShell** (Start → type "PowerShell"), paste this and press Enter:

```powershell
irm https://raw.githubusercontent.com/alexmarciucts/meeting-nudge/main/windows/install.ps1 | iex
```

It builds the app on your PC with tools already in Windows (no admin rights, no "unknown publisher" warning),
adds MeetingNudge to the Start Menu and starts it with Windows. Look for the red circle in the system tray —
it shows the minutes to your next meeting. Re-run the same command any time to update.

## Set up (1 minute)

The Settings window opens on first launch:

1. Open [calendar.google.com](https://calendar.google.com) → ⚙️ → **Settings**
2. Left sidebar: click your calendar → **Integrate calendar**
3. Copy **Secret address in iCal format** (not the public one)
4. Paste it into MeetingNudge → **Connect**

Treat that link like a password — anyone who has it can read your calendar.
If it ever leaks, reset it on the same Google page and paste the new one.

Click the ⏰ in the menu bar (Mac) or right-click the tray icon (Windows) for Settings, a test alert,
pausing alerts or quitting.

## Good to know

- Google takes ~30s to send the calendar, and MeetingNudge refreshes every 3 minutes,
  so meetings created in the last ~4 minutes before they start may be missed.
- Mac: log in `~/Library/Logs/MeetingNudge.log`, settings in `~/Library/Application Support/MeetingNudge/`.
- Windows: log and settings in `%APPDATA%\MeetingNudge`, the app in `%LOCALAPPDATA%\MeetingNudge`.

## Uninstall

**Mac:** menu bar ⏰ → Quit, then:

```bash
rm -rf ~/Applications/MeetingNudge.app ~/Library/Application\ Support/MeetingNudge ~/Library/Logs/MeetingNudge.log
```

**Windows:** tray icon → Quit MeetingNudge, then in PowerShell:

```powershell
Remove-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run MeetingNudge -ErrorAction SilentlyContinue; Remove-Item -Recurse -Force "$env:LOCALAPPDATA\MeetingNudge", "$env:APPDATA\MeetingNudge", "$([Environment]::GetFolderPath('Programs'))\MeetingNudge.lnk"
```

## Repo layout

- Mac app: `main.swift`, `ICS.swift`, `Overlay.swift`, `Settings.swift`, `install.sh`
- Windows app: `windows/` (C#, built by `windows/install.ps1`)
- `windows/test/`: a tricky calendar both apps must read identically — CI checks the Windows build against the Mac answers
