# DATABASE — none, by design

Android TV Desktop has no database. The data it keeps is small, owned by one user and read whole, so a
database would add a dependency and failure modes without solving any requirement.

| Data | Where | Format | Notes |
|---|---|---|---|
| Settings | `%APPDATA%\AndroidTVDesktop\settings.json` | JSON (`DataContractJsonSerializer`) | Validated on every load (invalid values → defaults, each fix logged). Atomic save (temp file + `File.Replace`). Unreadable file kept as `settings.json.bad`. |
| Restore record | `%LOCALAPPDATA%\AndroidTVDesktop\state.ini` | key=value | Exists only while the desktop is modified (fullscreen / taskbar hidden). Read by the app on next start and by `Restore-Desktop.ps1`. |
| Guest display marker | `%LOCALAPPDATA%\AndroidTVDesktop\guest-display-override.txt` | text | Records that *this app* applied `wm size/density`, so "AVD default" removes only its own override. |
| Logs | `%LOCALAPPDATA%\AndroidTVDesktop\logs\app.log` (+ `app.1..3.log`) | text | 1 MB rotation, no account or personal data. |
| Install manifest | `%LOCALAPPDATA%\Programs\Android TV Desktop\install-manifest.txt` | list of paths | Uninstall removes exactly these. |

The app never writes to the AVD folder, the Android SDK or the registry.
