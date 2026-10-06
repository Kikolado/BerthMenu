# What's new in StartDock

<!-- Shown inside StartDock (tray → What's new, Settings → What's new) and used as
     the GitHub release notes. Each version is a "## <version>" heading followed by
     "- " lines; add the newest version at the top. -->

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
