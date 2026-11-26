# Win10 Menu Bridge

The Windows 10 taskbar and tray right-click menus, on Windows 11, as actual Win32 menus.

No XAML. No injection. No Explorer patching. No kernel drivers. No downloads.

Kill the process, and the stock Windows 11 menus come back. That is not a slogan. That is the design.

## Why this exists

Windows 7's taskbar and notification area were Win32, with DirectUI where the shell needed a lighter custom layout. They were lean. A right-click produced a real menu: the kind `TrackPopupMenu` has been drawing since the 1990s.

XAML was not "the Windows 7 shell." WPF and XAML were already on the machine, as an application framework. You used them when you wrote an app. You did not have to. The shell stayed native. That pairing is what made it fine: the heavier frameworks were optional, and they were not bolted onto the taskbar.

Windows 11 rebuilt the taskbar in XAML (`Taskbar.dll`). In the process, the Win32 tray menus went away — the ones that lived behind `pnidui.dll` and friends — along with items people actually used. Then extras got bolted on. Ripping out working menus while keeping things nobody asked for is a choice. This is the other choice.

This tool does not try to shove the old shell back into Explorer. Windows 11 dropped the code those old menus plugged into, so swapping DLLs or "loading them into memory" is a dead end. Instead, a small helper process watches for right-clicks on the network icon, the volume icon, the clock, and the empty taskbar, and shows the Windows 10 menu as a plain Win32 popup. The items open the same panels they always did.

That's the whole trick.

## What it does

Right-click one of these, and you get a native `TrackPopupMenu` popup with the Windows 10 items:

| Where you click | What you get |
| --- | --- |
| Network / Ethernet / Wi-Fi icon | Troubleshoot problems (classic Network Diagnostics), Open Network & Internet settings |
| Volume / speaker icon | Open Sound settings, Open Volume mixer (classic `SndVol`), Spatial sound (formats for the current playback device), Sounds, Troubleshoot sound problems |
| Clock | The Windows 10 taskbar menu, including Adjust date/time and Customize notification icons |
| Empty taskbar | Search, Show Task View button, Cascade / stacked / side by side, Show the desktop, Task Manager, Taskbar settings |

Everything else is left alone: app buttons, Start, Search, Task View, other tray icons, and every left-click. If the helper is unsure where you clicked, it does nothing, and Windows 11 shows its own menu.

The popup follows your Windows app theme (dark menus when the system is in dark mode).

## How it works

A separate user-mode process. Zero XAML. Nothing is loaded into `explorer.exe`.

1. A background thread uses **UI Automation, read-only**, to cache the screen rectangles of the primary taskbar, the Network button, the Volume button, the Clock, and the other taskbar buttons.
2. A **`WH_MOUSE_LL`** hook on its own thread checks each right-click against that cache (and a short-lived hover hit-test). Only a right-click on Network, Volume, Clock, or empty taskbar is swallowed. Anything else is passed through with `CallNextHookEx`.
3. The helper builds the menu with **`TrackPopupMenuEx`**, using built-in English strings. Commands launch the same Settings pages, classic applets, and Shell.Application calls those items have always used.
4. The **Spatial sound** submenu is filled at click time for the current default playback device. Format names and Store links (Dolby Access, DTS Sound Unbound) come from the endpoint's advertised list. The active format, and switching it, go through the public WinRT class `Windows.Media.Audio.SpatialAudioDeviceConfiguration` — the same API the Settings app uses. If a format is not installed, the item is labeled "(get from Store)" and opens that Store link instead of switching. If the device does not support spatial audio, the format items are grayed and Off stays checked.

There is no injection, no Explorer patching, no kernel component, and no network access. The compiler is the `csc.exe` that already ships with Windows, plus the `Windows.Foundation.winmd` and `Windows.Media.winmd` metadata files already in `%WINDIR%\System32\WinMetadata`. There is nothing to download.

## Fail-safe

This is a regular process. If it crashes, if you kill it, or if you choose Exit on its tray icon, the hook is gone and the stock Windows 11 menus return immediately. Explorer is never modified, so there is nothing to "repair."

## Build

On a 64-bit Windows 11 machine, from the repo folder:

```bat
build.cmd
```

That runs the .NET Framework 4 compiler already at `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, and references `Windows.Foundation.winmd` and `Windows.Media.winmd` from `%WINDIR%\System32\WinMetadata` (they ship with Windows). It produces `Win10MenuBridge.exe` next to the source. No Visual Studio, no NuGet, no SDK install, no downloads.

`app.manifest` marks the program Per-Monitor v2 DPI aware, so hit-testing stays in physical screen coordinates.

## Optional: Windows 10 menu text

You do not need any Windows 10 files. Built-in English strings are the default, including a Spatial sound submenu.

If you want the original Windows 10 wording and item order, you can copy these three copyrighted `.mui` files from **your own** Windows 10 install into a `res\` folder next to the exe. This repository does not ship them. Load them as data only; do not register them or drop them into Windows 11's System32.

```text
%SystemRoot%\System32\<locale>\pnidui.dll.mui
%SystemRoot%\System32\<locale>\SndVolSSO.dll.mui
%SystemRoot%\<locale>\explorer.exe.mui
```

`<locale>` is usually `en-US`.

## Run

Start `Win10MenuBridge.exe`. A tray icon named "Win10 Menu Bridge" appears. It is a single-instance program; a second launch exits immediately.

To have it start with Windows, put a shortcut to the exe in your Startup folder (`shell:startup`). This repo does not add a startup entry, a service, or a scheduled task for you.

## `--dump-spatial`

A read-only diagnostic. From the folder that contains the exe:

```bat
Win10MenuBridge.exe --dump-spatial
```

This does not start the hook or the tray icon. It writes `spatial-dump.txt` next to the exe (default playback endpoint, whether spatial audio is supported, the active format, and each advertised format's GUID, name, installed flag, and Store URI) and then exits. The dump file is not part of the repository.

## Uninstall

1. Tray icon → **Exit (restore Windows 11 menus)**, or `taskkill /im Win10MenuBridge.exe`
2. Delete the folder. If you added a Startup shortcut, delete that too.

There is no installer, no service, no driver, and no Explorer patch to undo. The only registry values the helper ever writes are the ones you pick from the menu, and they are the same values Windows' own Taskbar settings use:

- Search → Hidden / Show search icon / Show search box → `HKCU\Software\Microsoft\Windows\CurrentVersion\Search\SearchboxTaskbarMode`
- Show Task View button → `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\ShowTaskViewButton`

## Limits

A few Windows 10 items have no Windows 11 equivalent, so they are omitted: **Lock the taskbar**, Cortana, News and interests, People, Ink, touch keyboard / touchpad, Copilot, Exit Explorer, and Undo. Search "highlights" / hover options are also omitted; the Search submenu follows Windows 11's `SearchboxTaskbarMode` (hidden, icon, or box).

Other limits:

- **Primary monitor only.** The helper hit-tests `Shell_TrayWnd`. Secondary taskbars are not covered.
- **Mouse right-click only.** `Shift+F10` and the Menu key still open the stock Windows 11 menu.
- **Spatial sound** lists the formats advertised for the current default playback device and can only switch formats that device supports. If spatial audio is not supported on that endpoint, the format items are grayed. Dolby Atmos and DTS:X need their Store apps (Dolby Access, DTS Sound Unbound); if the app is missing, the item opens the Store link from the endpoint instead of switching. `--dump-spatial` is the way to see what the helper found for the current device.
- Icon names are matched in English (Network, Ethernet, Wi-Fi, Volume, Speaker, Clock, and common variants). A different display language may need a small classification tweak.
- Tested on **Windows 11 25H2, build 26200**. Other builds may change automation IDs or taskbar structure; if a click is missed, the stock menu still appears.

## Credit

Built by Lightsnipe with AI assistance.

## License

MIT. See [LICENSE](LICENSE). Microsoft Windows, the menu resources described above, and all Microsoft trademarks remain Microsoft's.
