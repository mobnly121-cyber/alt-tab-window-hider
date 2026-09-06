# Vanish

Vanish is a small, portable Windows notification-area utility.

## Controls

- Press **Alt+Tab** to hide the currently active window and continue normal Windows app switching.
- Press **U** three separate times quickly to restore the most recently hidden window.
- Press **T** three separate times quickly to open a menu listing every hidden window, then choose what to recover.
- Double-click the Vanish notification-area icon to restore the most recently hidden window.
- Right-click the icon to restore all windows, pause hiding, or exit.

## Notes

- A hidden window disappears from the desktop, taskbar, and normal Alt+Tab list. This does not close the application: its process, open files, and current state remain intact, and it remains visible in Task Manager.
- The three U or T key presses are still typed into whichever application is active.
- Holding U or T does not count as three presses; the key must be released between presses.
- Exiting Vanish restores all windows it hid.
- Some administrator-level applications may require Vanish to be run as administrator before their windows can be controlled.
- This build is unsigned, so Windows may show a SmartScreen warning the first time it is opened.

## Build from source

Run `build.ps1` from Windows PowerShell. The build uses the .NET Framework compiler included with Windows and does not download dependencies.
