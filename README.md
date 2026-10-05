# StartDock

A lightweight, resizable icon-dock that replaces the Windows 11 Start Menu — no
"Recommended" section, no auto-grouped folders, just the apps you choose to pin,
plus a menu bar (bottom by default, or top — see Settings) with your account
name, Settings, Power, and File Explorer.

## How it works (and what it doesn't do)

Windows 11's real Start Menu (`StartMenuExperienceHost.exe`) is a closed system
component. Tools like StartAllBack replace it by injecting code into `explorer.exe`
and patching deep shell internals — that requires admin rights, is fragile across
Windows updates, and can trip Microsoft's own hardening against exactly that
technique (several such tools have broken on recent Windows 11 builds).

StartDock does **not** do that. It runs as an ordinary background app and uses two
much lighter, non-invasive techniques:

1. **A global low-level keyboard hook** watches for the Windows key. A press held
   *alone* (released with nothing else pressed in between) never reaches Windows —
   the native Start Menu doesn't open, and StartDock shows its own dock instead.
   Ctrl+Esc is suppressed the same way. The moment any other key goes down while
   Windows is still held, the hook recognizes it as a combo (Win+D, Win+L, Win+Tab,
   Win+Shift+S, ...) and reconstructs the real key sequence for Windows instead of
   opening the dock, so system shortcuts built on the Windows key keep working.
2. **A global low-level mouse hook** watches every click system-wide and swallows
   any that lands on a taskbar's Start button — the primary taskbar and, on a
   multi-monitor setup with "show taskbar on all displays" enabled, any
   secondary-monitor taskbar that exposes one too (found via UI Automation, so
   each button's actual position is tracked live — including Windows 11's default
   centered taskbar). Clicking any of them opens StartDock instead of native
   Start. (An earlier version of this used an invisible window pinned on top of
   the button instead; that had to keep winning a z-order fight against Explorer
   to receive the click at all, which it didn't always win. A mouse hook
   intercepts the click before Windows routes it to any window, so there's no
   z-order to lose.)

Neither technique touches `explorer.exe`, requires administrator rights, or
survives as a persistent system modification — if you exit StartDock, the taskbar
Start button and Windows key immediately go back to normal.

## Requirements

- Windows 10 (1809+) or Windows 11
- To build: **Visual Studio 2022** (Community is fine) with the **.NET desktop
  development** workload, or the **.NET 8 SDK** if you'd rather build from the
  command line
- No administrator rights needed to build, run, or use it — elevation is
  entirely optional, offered as a fallback from the tray icon (*Restart as
  Administrator*) for the one case that needs it; see Known limitations below

## Building

### Visual Studio
1. Open `StartDock.sln`.
2. Set the solution platform to `x64` (or `x86`/`arm64` to match your machine) in
   the toolbar.
3. Press **F5** to build and run, or **Ctrl+Shift+B** to just build.

### Command line
```
cd src/StartDock
dotnet build -c Release -p:Platform=x64
```
The built app lands in `src/StartDock/bin/x64/Release/net8.0-windows10.0.19041.0/`.

> The very first build restores NuGet packages and may take a minute.

> **Rebuilding while StartDock is already running will fail** — it's a
> background tray app (see "Running it" below), so a previous run keeps
> `StartDock.exe` open, and the build can't overwrite a locked file (you'll see
> `MSB3021`/`MSB3027` "the process cannot access the file... it is being used by
> another process"). Exit it first: right-click the tray icon (may be under the
> `^` overflow arrow) → **Exit**, or end `StartDock.exe` in Task Manager if you
> don't see the tray icon — then rebuild.

## Distributing it (another PC, or someone else)

A plain build above already produces `StartDock.exe` — that's what you've been
running all along — but it's a **framework-dependent** build: it doesn't carry
its own copy of .NET, so the machine you copy it to needs the matching **.NET 8
Desktop Runtime** already installed (Visual Studio installs it alongside the
SDK, so a dev machine usually already has it; a plain consumer PC usually
doesn't). There's no installer and none is needed — StartDock runs straight out
of wherever you put the folder, with no admin rights required either to copy it
or to run it.

Two ways to actually hand it to another machine, both run from
`src/StartDock`:

- **Framework-dependent publish** (small download, needs .NET 8 Desktop
  Runtime on the target machine):
  ```
  dotnet publish -c Release -r win-x64 --self-contained false -p:Platform=x64
  ```
  Copy the whole `bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/publish/`
  folder to the other PC (a zip over cloud storage, a USB drive, whatever) and
  run `StartDock.exe` from inside it. If that PC doesn't already have the
  runtime, Windows will say so when you try to launch it — grab the free ".NET
  Desktop Runtime 8" installer from Microsoft, a one-time minute-long install.

- **Self-contained single file** (bigger download — everything .NET needs is
  bundled in, roughly 100–150 MB — but nothing to install first, works on a
  totally bare Windows machine):
  ```
  dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Platform=x64
  ```
  This produces one standalone `StartDock.exe` in that same `publish/` folder —
  just that one file needs to make the trip.

(Swap `win-x64` for `win-x86` or `win-arm64` if the other machine isn't x64.)

A couple of things worth knowing before you do this:

- **Windows SmartScreen will likely flag it the first time it's run**,
  especially if it arrived via a browser download or cloud-storage link —
  "Windows protected your PC," with a **More info** link that reveals **Run
  anyway**. This is purely because the .exe isn't code-signed (that needs a
  paid certificate), not a sign of anything actually wrong; it's normal for any
  small, unsigned, homemade app and only ever shows up the first time.
- **Settings and pinned apps don't travel with the .exe.** They live in
  `%AppData%\StartDock\` on whichever machine StartDock is running on (see
  "Running it" below), separately from the program files, so a fresh copy on a
  new PC starts with an empty dock. To carry your current setup over too, copy
  `%AppData%\StartDock\config.json` (and, if you're using them, the
  `IconCache`/`Background` subfolders alongside it) into the same folder on the
  other machine before first launch there.
- **Autostart-at-sign-in is per-machine**, too — once StartDock's run there and
  that setting's on, it writes to *that* machine's own registry, not yours.

## Running it

Launch `StartDock.exe`. It has no visible window at first — it sits in the system
tray (bottom-right, may be under the `^` overflow arrow) waiting for its hotkey.
Press the **Windows key**, or click the taskbar **Start button**, and the dock
opens.

- **Add an app**: click the `+` tile in the menu bar, to the left of Task
  Manager (it used to sit next to the search box — it moved here so it's
  still reachable even when the search bar is hidden; see *Hide the search
  bar* under Settings below). This opens a searchable list of everything
  registered in your Start Menu — Store/UWP apps included — pulled the same way
  the real Start Menu's all-apps list gets it (PowerShell's `Get-StartApps`
  under the hood). Pick one and click **Add**. Can't find what you want there (a
  portable `.exe` that never registered itself, say)? Click *"Browse for a file or
  shortcut instead"* at the bottom to fall back to a plain file picker.
- **Remove an app**: right-click its tile → *Remove from StartDock*.
- **Categories**: your pinned apps live in named, independently-wrapping rows
  called categories (similar to how the Windows 11 Start Menu groups
  "Pinned"/"Recommended" under their own banners) rather than one single free
  grid. Every category wraps to as many rows as it needs on its own, so a
  category with a handful of apps stays short while another with dozens wraps
  to several rows beneath its own banner. A brand-new install starts with no
  categories at all — pin your first app (via the `+` tile) and StartDock
  creates a "Pinned" category for it automatically. Upgrading from an older
  version that used the original free-placement grid does the same thing once,
  automatically, the first time you launch the new build: every icon you'd
  already pinned is carried over into a single "Pinned" category, in the same
  order it used to sit in.
  - **Rearrange apps**: drag a tile near the edge of another one, within the
    same category or into a different one — a thin line shows where it'll
    land, the same cue the Windows Start Menu uses — and drop it there to slot
    it in.
  - **Group apps into a folder**: drag one app tile and drop it *squarely on
    the center* of another (plain) app tile — a rounded outline shows when
    you're centered enough — to group both into a new folder, exactly like the
    Windows Start Menu. Drag another app onto an existing folder tile the same
    way to add it in; this works across categories too. Click a folder tile to
    open it — the search row becomes a folder header where you can rename it
    (type and press Enter or click away) or click the icon on the right to
    *ungroup* it (moves its apps back into its own category, right where the
    folder itself used to sit). Removing apps from inside a folder down to one
    remaining app automatically ungroups it. (Folders can't be nested — and
    unlike apps, dragging a folder onto another tile just reorders or swaps,
    never groups.)
  - **Create a category**: click *+ Add category* underneath the last one, or
    just drag any app tile down past the last category — dropping it into the
    strip below creates a brand-new category and drops the app straight into
    it. Either way, the new category's name box is focused and selected so you
    can type a name right away.
  - **Rename a category**: click its banner's name and type — same as
    renaming a folder, it commits when you press Enter or click away — or
    right-click the banner and choose *Rename Category*, which does the same
    thing (handy if the banner's already showing an in-progress name you
    don't want to disturb by clicking into the middle of it).
  - **Reorder categories**: drag a category's own banner up or down past
    another category's banner to swap their order; a thin line above or below
    the banner you're hovering shows where it'll land.
  - **Delete a category**: right-click its banner and choose *Remove
    Category* — grayed out until the category is empty, so drag or remove its
    last app first. There's no way to delete a category that still has apps
    in it (drag them out, or into another category, first), and an empty
    category otherwise just stays put until you remove it yourself.
- **Resize**: drag the top edge (height) or right edge (width) of the dock,
  and, on the right edge, the width snaps to whichever whole number of tile
  columns you're closest to as you drag — the same "jumps between fixed sizes"
  feel the Windows 10 Start Menu has — instead of tracking the cursor
  pixel-for-pixel. The top edge is a plain, unsnapped resize: with categories
  independently wrapping to their own number of rows (see above), there's no
  longer one single "whole number of rows" the dock's total height could
  cleanly snap to the way its width still can. The size is remembered.
  Hovering directly over either edge's own 10px resize strip shows a faint
  highlight there regardless of Background opacity (see Settings below) — so
  even with the dock faded all the way down to invisible, you can still find
  exactly where to grab it by slowly running the mouse along its top or right
  edge.
- **Search**: press the Windows key and just start typing — like the real Start
  Menu, the search box grabs your keystrokes automatically, no need to click it
  first. Typing searches *everything*, not just what's pinned: your pinned apps
  (and folders) are matched first, followed by every other app installed on the
  computer (the same list the `+` tile's picker uses, so it's already warm by the
  time you search), and a short curated list of common Windows Settings pages
  (Bluetooth, Wi-Fi, Display, Sound, Windows Update, and so on — see
  Services/WindowsSettingsCatalog.cs for the full list) — so searching
  "bluetooth" finds the Bluetooth settings page itself, not just an app that
  happens to mention it. Click any result to launch it immediately — for an app
  or settings page that isn't pinned yet, right-click it and choose *Pin to
  StartDock* instead to add it to your grid without launching it (it lands in
  your first category). Clear the search box to go back to your categories.
- **Keyboard**: Up/Down/Left/Right move between tiles (Down from the search box
  jumps to the first tile, Up from the top row goes back to the search box), Enter
  launches the focused tile (or the first result, from the search box), Escape
  closes the dock.
- **Settings** (gear icon, menu bar) — laid out as three side-by-side columns
  (activation/layout, content/text, and visuals/theme) rather than one long
  list, and the whole settings area scrolls independently of Save/Cancel,
  which stay pinned at the bottom — so the dialog stays usable even on a
  short display where it wouldn't otherwise fit on screen:
  - **Activation shortcut**: **Windows key** (fully replaces native Start),
    **Shift+Windows key** (leaves a plain Windows key press alone), or a
    **Custom keybind** — click the field below that option and press whatever
    key combo you want (e.g. Ctrl+Alt+Space); Escape cancels. A modifier is
    optional — a single bare key works too, but pick one you don't otherwise
    type (a spare function key, say): StartDock will catch *every* press of
    it, everywhere, system-wide, while Custom mode is active, the same way it
    normally only does that for the Windows key itself.
  - **Position**: nine options, grouped bottom row / top row / middle row —
    **Bottom left** (the original placement — anchored to the taskbar corner,
    appears/disappears instantly), **Bottom center**, **Bottom right**, **Top
    left**, **Top center**, **Top right**, **Middle left**, **Middle center**
    (dead center of the screen), **Middle right**.
    - Bottom-row and top-row positions slide in/out vertically (up from the
      taskbar edge for the bottom row, down from the top for the top row),
      like the Windows 11 Start Menu.
    - **Middle center** fades in/out instead, since there's no single edge to
      slide from at dead-center.
    - **Middle left** / **Middle right** slide in/out *horizontally* instead —
      from off-screen to the left or right respectively — since they're
      anchored to a side edge rather than top or bottom.
    Every centered or middle-anchored position (Bottom/Top center, Middle
    center/left/right) stays actively centered on its axis while you
    drag-resize the dock too, not just the next time it's shown; the
    right-anchored positions (Top/Bottom/Middle right) do the equivalent for
    their own right edge.
    Every position measures against the monitor the dock is actually on (or,
    on the very first open, whichever monitor the mouse cursor is on) and
    that monitor's own DPI scaling — not just the primary display — so this
    is accurate even on a multi-monitor or non-100%-scaled setup.
  - **Move the menu bar to the top of the dock**: relocates the whole menu
    bar (avatar, Task Manager, Volume Mixer, File Explorer, Settings, Power)
    from the bottom of the dock to the top — the divider line beside it moves
    along with it. This is independent of **Position** above: Position is
    where the whole dock window sits on screen, this is which edge of the
    dock's own content the menu bar sits against.
  - **Icon row alignment**: **Left** (the original packing), **Center**, or
    **Right**. Only visible on a row that doesn't already fill the dock's
    width — most often a category's last row, or a short category that fits
    on one; a row that already spans the full width looks identical either
    way. A category's own banner name follows the same setting (left-, center-,
    or right-aligned along with its row), so the two stay visually consistent.
  - **Icon size**: a slider (48–160px) resizing every app tile — the icon
    graphic inside each tile, the grid's own spacing, and the dock's
    resize-to-columns snapping all scale together with it. Default (92px)
    matches StartDock's original, fixed tile size.
  - **Reset Dock Size**: shrinks the dock to its smallest allowed size the
    next time you save. Mainly for reaching a size you otherwise can't —
    working remotely on a laptop with no way to drag the dock's own top/right
    resize grips, say.
  - **Hide category names** / **Hide icon names**: two independent visual
    toggles. The first collapses every category's banner (you lose renaming
    and drag-to-reorder-by-banner while it's off, same trade-off as hiding
    search); the second collapses just the text label under each tile,
    leaving an icon-only grid on the main categories screen. Neither removes
    the underlying feature — switch it back on to get at it again. Hide icon
    names only declutters the main screen: a tile's name still shows in
    search results (so you can actually read what you found) and inside an
    open folder. The tile itself always stays the same square size; with the
    name hidden, the icon graphic inside it grows a little and sits centered
    on that square instead of at its normal size with empty space left below
    it.
  - **Category name size** / **Icon name size**: two independent sliders for
    the font size of a category's own name and an icon tile's own name label
    — tune either without affecting the other. Each has no visible effect
    while its matching "Hide" checkbox above is on, since there's no text
    showing to resize.
  - **Bold category names** / **Bold icon names**: two independent toggles
    that bolden a category's own name or an icon tile's own name label —
    either can be turned on without the other.
  - **Background opacity**: a slider from fully solid down to fully invisible.
    Fades the dock's background fill, border, and shadow, together with the
    menu bar's own background (which now matches the rest of the dock instead
    of being its own separate color) — icons, text, and buttons always stay
    fully crisp so the dock stays usable even at low opacity.
  - **Background image**: **Choose Image...** picks a picture (copied into
    `%AppData%\StartDock\Background`, so moving or deleting the original
    afterward doesn't break anything) to show behind the icons instead of the
    plain theme color; **Remove** reverts to that plain color. Background
    opacity above fades the image exactly the same way it fades a solid color.
  - **Fit the whole image to the dock**: off (default) crops the picture to
    fill the dock edge-to-edge with no empty space; on instead shrinks it to
    show the whole picture with nothing cut off, which can leave a sliver of
    the plain background color on one side if the picture's own proportions
    don't match the dock's.
  - **Show a background behind each icon**: gives every icon tile a
    persistent square background instead of showing one only on hover — helps
    an icon whose own artwork has a transparent background stand out against
    the dock instead of reading as though it's floating on nothing. Hover and
    click still darken it a little further, same as always. The dropdown
    beneath it picks the background's own color: **Theme** (default) matches
    the dock's normal hover color and follows a light/dark theme switch;
    **Black** or **White** instead pin it to a literal color regardless of
    theme.
  - **Frost the background**: adds a translucent, blurred-glass effect behind
    the dock (a "frosted glass" look, the same idea as Windows' own Mica/
    Acrylic surfaces) showing whatever's behind it on the desktop, blurred,
    rather than either a flat color or full transparency. Works together with
    Background opacity and a custom background image — the frosting sits
    underneath either, so a low-opacity or image background still gets the
    blur showing through it.
  - **Remove the border entirely**: hides the dock's outline and the menu
    bar's divider line altogether, leaving just the plain background with no
    edge drawn around it. Takes priority over Bold border below — there's
    nothing left for it to bolden while this is on, so it's grayed out (not
    unchecked) for as long as this stays checked.
  - **Bold border**: strengthens the dock's existing outline and the menu
    bar's divider line — both already there, just a subtle gray by default —
    to a solid, high-contrast color instead: black on the light theme, white
    on the dark theme, whichever one actually reads as bold against that
    theme's background. Not a second border on top of the existing one, just
    the same lines made a lot more visible.
  - **Hide the menu bar entirely**: a master switch that collapses the whole
    bar (user avatar and every utility button, plus the divider line beside
    it) in one go, wherever **Move the menu bar to the top** currently has it
    sitting — for a plain icon grid with nothing else around it, rather than
    picking through the individual buttons below. It doesn't reset any of
    those individual checkboxes (they're just grayed out while this is on,
    with nothing to show); turn it back off and whichever ones you'd left
    checked are back exactly as you left them. Settings stays reachable from
    the tray icon either way.
  - **Menu bar buttons**: independent show/hide checkboxes for each of the
    five utility buttons — Task Manager, Volume Mixer, File Explorer,
    Settings, and Power. Hiding a button just removes it from the bar; Settings
    stays reachable from the tray icon even if you hide its button here, so
    there's always a way back in to turn a hidden one back on.
  - **Hide the search bar until typing**: keeps the top of the dock clean
    (handy paired with a low background opacity) — nothing about search
    itself is disabled, the row just stays collapsed until you start typing,
    exactly like it would if you'd clicked into it first.
  - **Text color**: overrides the dock's text color regardless of the theme
    setting above — Default (the normal choice) follows whatever the active
    theme would normally use; Black/White instead pin every label on the
    dock to that literal color, useful if a background image or Frosted
    background makes the theme's usual text hard to read against it.
  - Also here: whether the taskbar Start button opens StartDock, whether a
    button opens the real Windows Start Menu (see below), theme, and
    autostart-at-sign-in.
- **Open the Windows Start Menu** (menu bar, next to the `+` tile): opens the
  real, native Start Menu — for anyone StartDock gets shared with who still
  wants a way back to it, since the options above otherwise intercept every
  Windows key press and every taskbar Start-button click. On by default;
  hideable via Settings.
- **Task Manager** (menu bar): opens Task Manager.
- **Volume Mixer** (menu bar): opens the classic per-app Volume Mixer (`SndVol.exe`
  — the same thing "sndvol" from the Run dialog opens), not the quick single-slider
  popup the taskbar's speaker icon shows.
- **Power** (menu bar): Lock, Sleep, Sign out, Restart, Shut down. If Windows
  Update has a reboot-pending update staged, Restart/Shut down relabel
  themselves to **Update and restart** / **Update and shut down** — checked
  fresh every time you open the Power flyout, the same way the native Start
  menu's own power button does. Either way they go through the normal Windows
  shutdown path (`shutdown.exe`), so a staged update is applied on the way
  down regardless of which label is showing; the label just tells you
  up-front rather than surprising you with an update screen.
- **File Explorer** (menu bar): opens a new Explorer window.
- Right-click an app tile's icon and choose **Refresh icon** if it's showing a
  blank or generic placeholder instead of the app's real icon — clears the
  cached icon and re-extracts it. Most often needed for games (Steam titles
  especially) installed on a drive other than the one Windows itself is on;
  Windows sometimes fails to resolve that icon the first time it's extracted,
  and this forces a clean retry without needing to remove and re-add the tile.
- Right-click the **tray icon** for *Open StartDock*, *Settings*, *Restart as
  Administrator* (hidden if StartDock is already running elevated — see
  "Fullscreen games and the Windows key" under Known limitations below for
  why you might need this), and *Exit*.

Your pinned apps and settings are stored in
`%AppData%\StartDock\config.json`; cached icons live in `%AppData%\StartDock\IconCache`,
and a chosen background image lives in `%AppData%\StartDock\Background`.

## Uninstalling / turning it off

- To stop it for this session: right-click the tray icon → **Exit**. The taskbar
  Start button and Windows key immediately behave normally again.
- To stop it from launching at sign-in: open **Settings** in the dock and untick
  *Start StartDock automatically*, or delete the `StartDock` value under
  `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` yourself.
- To remove all data: delete the `%AppData%\StartDock` folder.

## Known limitations (v1)

- **Fullscreen games and the Windows key**: pressing the Windows key while an
  elevated fullscreen app has focus — most commonly a game running under
  anti-cheat software (EasyAntiCheat, BattlEye, Vanguard, and similar all run
  elevated) — can fall through to the native Start Menu instead of opening
  StartDock, or not respond at all, for the same underlying reason as the Task
  Manager case just below: Windows' UIPI security boundary blocks a
  non-elevated app's global keyboard hook from seeing key events while an
  elevated window has focus. This is a genuine Windows security boundary, not
  a bug in the hook itself, so there's no fix that keeps StartDock running
  non-elevated. If it bothers you with a particular game, right-click the tray
  icon → **Restart as Administrator** — StartDock relaunches itself elevated
  (one UAC prompt) and the hook then has the same privilege level as the game,
  so it works normally. This is opt-in rather than the default specifically to
  keep the common case running without ever prompting for admin rights (see
  "No administrator rights needed" above).
- **The Windows key can briefly fall through to native Start after opening Task
  Manager**: on many systems Task Manager auto-elevates itself when launched
  (you can check via its own Details tab → right-click a column header → Select
  columns → *Elevated*). While an elevated window has focus, Windows' UIPI
  security boundary blocks StartDock's global keyboard hook — deliberately
  non-elevated, see "No administrator rights needed" above — from seeing key
  events at all, so the very next Windows-key press opens the native Start Menu
  instead of StartDock. It's self-healing: as soon as focus moves off Task
  Manager (e.g. that native Start Menu opening and closing), the hook works
  again and the following press opens StartDock normally. Fixing this for real
  would mean either running StartDock itself elevated (a UAC prompt on every
  launch, including at sign-in — a much bigger trade-off than this quirk
  warrants) or routing Task Manager through a background Scheduled Task to
  force it non-elevated (extra moving parts for a cosmetic, one-press hiccup).
  Left as-is for now.
- **Secondary-monitor Start button coverage depends on Windows actually having
  one**: the click-catcher now covers every taskbar it can find a Start button on,
  primary or secondary, but plenty of Windows 11 configurations simply don't put a
  Start button on secondary-monitor taskbars in the first place — in that case
  there's nothing for StartDock to cover there, which isn't something StartDock can
  work around from outside `explorer.exe`.
- **Opt-in, not mirrored**: StartDock still doesn't auto-mirror your existing Start
  Menu layout — the `+` tile's picker lists everything *available* to add, but
  nothing is pinned until you choose it. (Deliberate, not a missing feature.)
- **Folders are one level deep**: they can't contain other folders (dragging a
  folder onto another tile just reorders/swaps it), and reordering/grouping by
  drag is only available in the categories view, not while browsing inside an
  open folder or in search results.
- **Categories are one level deep too**: there's no nesting or sub-grouping of
  categories, just a flat, reorderable list of them.
- **The installed-apps list shells out to PowerShell** (`Get-StartApps`)
  rather than using WinRT package APIs directly — simpler and more reliable to
  maintain. StartDock kicks this off itself in the background the moment it
  starts (well before you ever open the dock), along with extracting every
  app's icon, so by the time you actually search or open the `+` tile's
  picker the list is normally already sitting in memory — no per-search delay.
  On a slower machine or a very large Start Menu, a search made in the first
  second or two after launch can still catch that warm-up mid-flight; it just
  falls back to waiting on whatever part isn't done yet, same as before. The
  list and icons are cached for the rest of the session and shared between
  the picker and search. If PowerShell is unavailable or blocked by policy,
  both fall back to nothing extra (search still matches your pinned apps; the
  picker points you at "Browse for a file" instead).
- **Remote Desktop**: the drop-shadow effect on the dock can render as a solid
  black box over RDP, a known WPF/RDP interaction. It's cosmetic only.
- **Depends on the taskbar's internal structure**: the Start-button click hook
  still finds the button's on-screen rectangle via UI Automation (by AutomationId,
  with a case-insensitive name-based fallback) — a global mouse hook then swallows
  clicks that land inside it (see "How it works" above for why this replaced an
  earlier overlay-window version). This is the same category of thing
  StartAllBack-style tools rely on, just at a much shallower level — if a Windows
  build renames or restructures the button in a way neither lookup matches, it
  simply won't be found (the Windows key hook is unaffected either way, since it
  doesn't depend on taskbar structure at all). If clicking the Start button still
  does nothing after a rebuild, check two files after relaunching:
  `%AppData%\StartDock\overlay-diagnostics.log` (a one-time, detailed dump of what
  the lookup actually found, written the first time it fails to find the button
  at all) and `%AppData%\StartDock\crash.log` (in case something threw before the
  lookup even ran, e.g. a Group Policy or security-software restriction on the
  hook itself) — between the two there should be enough to pin down exactly where
  this is failing on a given machine, rather than it just silently not working.
- **Unsigned executable**: since this isn't code-signed, Windows SmartScreen may
  show a warning the first time you run a build you downloaded rather than built
  yourself. Signing is out of scope for a personal/lightweight tool like this.
- **The dock's drop shadow loses a little of its corners**: the window itself is
  now clipped to a rounded-rectangle region (see MainWindow.ApplyWindowRegion)
  so Frost the background's blur actually follows the dock's own rounded shape
  instead of blurring behind it as a hard-cornered square. That clip applies to
  everything about the window, shadow included — the shadow already only had the
  10px margin around the dock to bleed into (its own BlurRadius is 24), so this
  mostly shows up, if at all, as very slightly less shadow right at the four
  corners specifically, not along the edges.

## Project layout

```
StartDock.sln
src/StartDock/
  App.xaml(.cs)                  Startup: single-instance guard, wires up all services
  app.manifest                   Per-Monitor-V2 DPI awareness, no-admin execution level
  Models/
    DockIcon.cs                  One pinned tile — or, with Children set, a folder of tiles
    Category.cs                  One named, independently-wrapping row of DockIcons
    AppConfig.cs                 Everything persisted to config.json (a list of Categories)
  Services/
    NativeMethods.cs             All Win32 P/Invoke in one place
    HotkeyService.cs             Windows-key / Shift+Windows-key interception
    StartButtonOverlayService.cs Click-catcher over the real taskbar Start button
    ClickOutsideService.cs       Global click hook that closes the dock on a real outside click
    ConfigService.cs             Load/save config.json
    IconExtractor.cs             Shell icon extraction + caching (files and shell:AppsFolder items)
    AppLauncher.cs                Launches pinned targets
    StartAppsService.cs          Lists installed apps via Get-StartApps for the Add-icon picker
    InstalledAppsCache.cs        In-memory, session-lifetime cache of that list + extracted icons
    WindowsSettingsCatalog.cs    Curated ms-settings: pages surfaced in search
    UserInfoService.cs           Display name + account picture
    PowerActions.cs               Lock/Sleep/Sign out/Restart/Shut down
    AutostartService.cs          Run-key registration
    ThemeService.cs              Light/dark resolution
    TrayIconService.cs           System tray icon + menu
  Views/
    MainWindow.xaml(.cs)         The dock itself
    SettingsWindow.xaml(.cs)     Settings dialog
    PickInstalledAppDialog.xaml(.cs) The + tile's installed-apps picker
    DockIconViewModel.cs         Binding wrapper for a DockIcon
    CategoryViewModel.cs         Binding wrapper for a Category
    InstalledAppViewModel.cs     Binding wrapper for an installed app in the picker
    FlowGridPanel.cs             Dense flow layout + drag feedback for one category's icon row
  Resources/
    Theme.xaml / Theme.Dark.xaml Light/dark color resources (swapped at runtime)
    Styles.xaml                  Control styles (tiles, bottom-bar buttons)
    StartDock.ico                App icon (.exe, tray, UAC prompts — see ApplicationIcon in the .csproj)
```
