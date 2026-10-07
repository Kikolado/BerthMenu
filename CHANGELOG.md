# What's new in StartDock

<!-- Shown inside StartDock (tray → What's new, Settings → What's new) and used as
     the GitHub release notes. Each version is a "## <version>" heading followed by
     "- " lines; add the newest version at the top. -->

## 0.9.6

- Undo: removing a tile, folder or category shows "Removed … · Undo" at the bottom of the dock for a few seconds. Click Undo (or press Ctrl+Z) to put it all back, custom names and icons included.
- Recent files on right-click: right-click an app (Notepad, Paint, Word, VS Code and others) to see its recent files under Recent, like the taskbar's list.
- Pinned folders open inside the dock: click a pinned folder like Downloads to see what's in it, newest first or by name. Click a folder inside it to go in, Back or Backspace to go up, and the folder button to open it in File Explorer. Shift+click a pinned folder to go straight to File Explorer, or turn this off in Settings → Layout.
- Smoother opening: the dock now appears already moving instead of sitting still for a moment before it slides in, its slide eases out like the Windows Start menu's, and the refreshing it does when it opens (recent sections, running apps, the app list) waits until the animation has played.
- The slide now comes in from the screen edge the dock sits against: up from behind the taskbar at the bottom (Bottom left used to just appear), down from the top, and in from the side for Middle left and Middle right. It slides within the dock's own space, so it no longer shows on a monitor next to it, and it no longer stutters. Middle center rises a short way, or fades if its monitor has no room below it.
- New: Settings → Layout → Animation chooses Slide, Fade or None. A change takes effect as soon as you save, closing included.
- Fixed: with Frosted glass on, the fade showed a light gray box while fading in and out, and the thin outline Windows draws around the dock showed up before the rest of it.
- Fixed: Settings and other StartDock windows flashed white for a moment as they opened.
- Search settings: type in the box at the top of Settings (or press Ctrl+F) to find a setting. Matching settings light up and the rest fade, so "border" or "blur" takes you right to them.

## 0.9.5.2

- Fixed: with Border → Remove, Windows still drew a soft shadow around the dock. It's gone now (unless Frosted glass is on, which needs it for rounded corners), so a see-through dock really is just your icons.

## 0.9.5.1

- Fixed: Border → Remove left a faint outline around the dock on Windows 11. It's gone now, so a see-through dock shows just your icons.

## 0.9.5

- Welcome screen the first time StartDock runs: choose how to open it, where it opens, its look, the icon direction and alignment, and start with your most used apps already pinned (or an empty dock).
- Export and import your setup: Settings → Startup → Export… saves your settings and pinned apps, with their icons, to one file. Import… brings them back, on this PC or another one.
- Settings → Startup → Reset… starts over like a new install (your current setup is backed up first).
- Hide from search: right-click a search result you never want to see. Settings → Search → Manage… brings it back.
- Search keywords: right-click a tile → Properties... → Search keywords, so a short word like "ps" finds it.
- Fixed: some apps showed a blank page icon in search, Recently added and the Add window, and Refresh icon didn't fix it there (only after pinning). Icons are now read the way Windows expects, and Refresh icon works on search results.
- New Settings → Look → Border color: Match theme, White or Black, for the thin border as well as Bold (the menu bar divider follows it).
- "Pin to StartDock" in File Explorer's right-click menu, for files and folders. Off by default: turn it on in Settings → Startup. (On Windows 11 it's under "Show more options".) The dock opens with the new tile selected.
- Right-click an empty part of the dock → Remove duplicate tiles… removes exact copies of a tile in one go (the first copy of each stays).
- Hover over a tile whose name is cut off to see the whole name.
- Keyboard: select a tile with the arrow keys, then F2 renames it and Delete removes it.
- Settings now has "Report a problem" and "GitHub" links next to Tips. Reports include StartDock's recent errors (with your user name taken out), so problems are easier to fix.

## 0.9.4.1

- Settings: "Save" is now "Save & Close". Closing Settings any other way with changes you haven't saved asks whether to save them or close without saving.
- Fixed: the dock flickered the first time it opened after StartDock started, and could show a frame in the wrong place before sliding in.
- Settings → Startup: the update status is shorter ("Up to date"), so it's no longer cut off. Hover over it for the details.
- A category's fold arrow now sits right beside its name (before it when names are aligned right), so the name no longer shifts over.
- Fixed: right-click → Open file location did nothing for apps pinned from the app list, and the File Explorer window it opened could end up hidden behind the dock. It now shows the app's program (or the program a shortcut points to) and closes the dock. It's no longer offered for Store apps, websites or Settings pages, which have no file to show.

## 0.9.4

- Dark theme now covers right-click menus, Settings dropdowns, buttons and tooltips.
- "What's new" after an update: click the update notification, or find it in the tray menu and the Settings footer.
- New Tips page with shortcuts and features that are easy to miss: in the tray menu, the Settings footer, and the What's new window.
- Search now finds classic Windows tools that weren't in the app list, like "Turn Windows features on or off", Device Manager, Disk Management, Control Panel and System Properties, plus more Settings pages.
- Run commands from search, like the Windows Run box: type a command (cmd, notepad, control, ms-settings:…) or a path (C:\Users, %AppData%, \\server\share) and press Enter. Can be turned off in Settings → Search.
- Optional running-app indicator: a small bar under pinned apps that are open. Turn it on in Settings → Look.
- Add a website to the dock: Add → "Add a website...". It gets the site's icon when it can.
- Remote Desktop connections: pin saved .rdp files (Add → Browse lists them now), and right-click one for "Edit connection".
- Drag files, folders or a browser link onto the dock to pin them. To get the dock open mid-drag, press the Windows key or hold the drag over the Start button.
- Alt+1 to Alt+9 open the first nine tiles (or search results). Hold Alt to see the numbers.
- Click a category's name to fold it down to just the name, and again to open it. Renaming a category is now on its right-click menu.
- Remove Category now works on categories that still have apps in them (it asks first). Removing a tile asks first too when it has its own name, icon or Properties settings.
- Right-click a tile → Properties...: change its name, what it opens, its arguments and "Start in" folder, and "Always run as administrator".
- "Report a problem..." in the tray menu opens a GitHub issue with your StartDock and Windows versions filled in.
- Automatic settings backups: your settings and pinned apps are backed up once a day when something changes (the last 10 are kept). Settings → Startup → "Restore a backup…" takes you back to one.

## 0.9.3

- Pin scripts (.cmd, .bat) and folders, not just programs.
- Right-click an empty part of the dock for "Add App or Folder".
- The Add window's checkboxes and list now follow the dark theme.
- Fixed scrollbars that didn't work in the dock's menus.
- Snapping the dock width now leaves the same gap on both sides of a row.
- New setting: Layout → "Snap dock width" (off for a smooth, free width).
- Scrollbars float over the icons and only show while scrolling (and for 2 seconds after).

## 0.9.2

- New Calculator button for the menu bar.
- Layout → "Position" renamed to "Dock Position".

## 0.9.1

- Search puts the apps you open most first (typing "Power" now finds PowerShell first if that's what you use).

## 0.9

- Automatic updates from GitHub: Settings shows "Update x available" next to the version, and Startup → "Update automatically" installs it on its own.
- Menu bar can sit on the left, right, top or bottom of the dock, with an option to flip the icon order.
- Menu bar settings now live in their own Menu Bar card.
- Separate Row alignment and Column alignment dropdowns.
- Animated GIFs as icons and backgrounds.
- Category spacing sliders.
- New Item Size card: icon size, category names and icon names.
- Icon names can wrap onto a second line.
- Category names move under their icons when the dock is set to columns with bottom alignment.
- Fixed: the Windows key sometimes opened the Windows Start menu or got stuck, so D or M hid every window.
- Fixed: a crash after closing the dock with Alt+F4.
- Fixed: typing sometimes didn't reach the dock right after opening it.
- Fixed: Backspace in the dock opened another screen.

## 0.8

- Columns layout: icons can flow down in columns instead of across in rows, scrolling sideways.
- Recent sections reworked: recently added apps, recently used apps and recently used files, each with its own count (up to 12), shown separately or combined.
- Recent settings moved into their own card.
- Fixed: "Recently added" filling up with help files and links instead of apps.
- Icons no longer shift up when their name takes two lines.

## 0.7

- First version with an installer.
