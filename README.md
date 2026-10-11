# FileDock

A temporary shelf for files on Windows. Collect files from different folders, then drag them
out together: into another folder, an email, a browser upload area or any app that accepts
dropped files.

FileDock only remembers *where* files are. It never copies, moves, renames or uploads them.

## Using FileDock

| To | Do this |
|---|---|
| Open the shelf | Point at the thin handle on the screen edge (right by default), click the tray icon, or press the shortcut. |
| Add files or folders | Drag them from File Explorer onto the handle. The shelf opens immediately; release over it. Each batch is added to what is already there; duplicates are ignored. |
| Drag everything out | Drag the **Drag all N items** strip into the destination. |
| Drag some items out | Select rows (click, Ctrl+click, Shift+click, Ctrl+A) and drag any selected row. Dragging an unselected row drags just that row. |
| Remove from the shelf | The × on a row, **Remove**, the Delete key, or **Clear**. A message offers **Undo**. Files on disk are never touched. |
| Keep it open | The pin button in the header (or **Pin open** in the tray menu). |
| Close it | Move the pointer away (it closes after the delay set in Settings), press the shortcut again, or press Esc while the panel has keyboard focus. |
| More actions | Right-click a row: Open, Show in folder, Copy path, Remove from shelf. |

Outgoing drags always **copy**. FileDock offers the destination a copy operation only, so the
originals stay where they are, and the shelf keeps its items after the drag.

If an original is moved, deleted or its drive is disconnected, the row is marked
**Unavailable** and is left out of drags; it can be removed with one click.

The default shortcut is **Ctrl+Alt+Space**. If another app already owns it, FileDock switches
once to **Ctrl+Alt+Shift+Space** and says so. Any shortcut can be recorded in Settings.

### Settings

Screen edge, display (when more than one is connected), handle position, close delay,
confirmation before clearing, start with Windows (per-user Run key; off by default), and the
shortcut. **Reset to defaults** restores all of them.

### Exiting completely

Right-click the FileDock tray icon and choose **Exit**. Closing the panel only hides it, and
the shelf contents are kept either way.

## Where data is stored

Everything is local, in `%LOCALAPPDATA%\FileDock\`:

| File | Contents |
|---|---|
| `shelf.json` | The paths on the shelf |
| `settings.json` | Settings |
| `filedock.log` | Small diagnostic log (capped at 512 KB, one previous copy kept as `filedock.old.log`) |

State files are written to a temporary file first and then swapped in, so a crash never leaves
a half-written file. If a file is damaged, FileDock renames it to `*.corrupt-<date>` for
troubleshooting and starts from a clean state. There is no network access, telemetry or account.

Deleting the folder resets FileDock completely.

## Building from source

### Prerequisites

- Windows 10 or 11, 64-bit
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Build, test and run

```powershell
dotnet build FileDock.slnx
dotnet test FileDock.slnx
dotnet run --project src\FileDock
```

### Publish the release executable

```powershell
dotnet publish src\FileDock\FileDock.csproj -p:PublishProfile=win-x64
```

This produces a single self-contained file, `publish\win-x64\FileDock.exe`, that runs without
any .NET installation. The profile is equivalent to:

```powershell
dotnet publish src\FileDock\FileDock.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=false -p:DebugType=embedded -o publish\win-x64
```

On first launch the WPF native libraries are unpacked to `%TEMP%\.net\FileDock\`; this is
standard for single-file WPF apps.

The executable is not code-signed, so Windows SmartScreen may warn the first time it is run
after being downloaded.

## Project layout

```
FileDock.slnx
src/FileDock/
  App.xaml(.cs)            Startup, single instance, tray, settings host, shutdown
  Models/                  StagedItem, ShelfState, AppSettings
  ViewModels/              ShelfViewModel, ItemViewModel, SettingsViewModel, DragSelection
  Views/                   ShelfWindow, SettingsWindow, DockController (handle/panel behaviour)
  Services/                Shelf and settings persistence, file inspection, shell icons,
                           drag and drop, placement, hotkey, tray, startup registration, log
  Native/                  Win32 and Windows Shell interop declarations
  Themes/                  Control styles and the light/dark/high-contrast palette
  Properties/PublishProfiles/win-x64.pubxml
tests/FileDock.Tests/      xUnit tests (test-only dependencies; not shipped)
publish/win-x64/           Release output
```

## How it works

- **One window, clipped.** The dock window always has the expanded panel's size and position.
  When collapsed, a window region clips it to the handle. Expanding removes the region; the
  window never moves, resizes or is recreated. A drag that reaches the handle therefore
  continues into the panel without interrupting the OLE drag operation.
- **Incoming drops** accept only standard file-drop data (`CF_HDROP`). FileDock always reports
  *Copy* to the source and never *Move*: some sources delete the originals after a drop that
  reports Move.
- **Outgoing drags** use the shell's own data object (the same one File Explorer creates, with
  `CF_HDROP` and Shell ID lists) and `SHDoDragDrop`, which also draws the standard drag image.
  Only `DROPEFFECT_COPY` is offered. If the shell object cannot be created, a plain WPF file
  drop is used instead.
- **Auto-close** uses a short timer that runs only while the panel is open. It never closes the
  panel while a drag (in either direction), a menu or a dialog is active, or while it is pinned.
- **Full-screen apps.** The handle steps aside while a full-screen app (video, game,
  presentation) is in front on the same display.
- WPF's stylus/touch stack is disabled. On a test machine with touch/virtual-display drivers
  it added 2–3 seconds to every window creation. Touch and pen still work through Windows'
  mouse promotion.
- The release is published uncompressed: compression would roughly halve the file (148 MB →
  66 MB) but doubled private memory (68 MB → 145 MB) for an app that stays running.

## Known limitations

- Only real files and folders can be added. Items that exist only inside another app
  (Outlook attachments, browser images, files inside a .zip opened in Explorer) are not
  file-drop data; the shelf explains this and adds nothing.
- Destination support depends on the destination. Verified: File Explorer folders and a
  browser HTML5 drop zone (the mechanism webmail attachment areas use, tested in Microsoft
  Edge). Not verified: the Outlook desktop app and specific webmail services.
- Because only *copy* is offered, a destination that insists on *move* will refuse the drop.
- Windows blocks drag and drop from a normal app into an app running as administrator. Run
  FileDock and the destination at the same privilege level.
- Some destinations cannot handle paths longer than 260 characters even though FileDock can
  stage them.
- Multi-monitor placement, display changes and high-contrast mode are implemented but were only
  tested on a single 1920×1080 display at 125% scaling, with the light and dark palettes.
- Developer option: set the environment variable `FILEDOCK_THEME` to `light`, `dark` or
  `contrast` to preview a palette without changing Windows settings.
