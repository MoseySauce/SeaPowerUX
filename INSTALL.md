# Installing SeaPowerUX

For players. If you want to build from source, see [CONTRIBUTING.md](CONTRIBUTING.md) instead.

## 1. Requirements

- **Sea Power**
- **BepInEx 5 (x64)** — the mod loader everything here runs on. SeaPowerUX does **not** bundle or
  install it.

### Install BepInEx first

1. Download **BepInEx 5** for **x64 (win_x64)** from the
   [BepInEx releases](https://github.com/BepInEx/BepInEx/releases) (a `BepInEx_win_x64_5.4.x.zip`).
2. Extract it into your Sea Power folder — the one containing `Sea Power.exe` — so you end up with
   a `BepInEx\` folder next to the game.
3. **Launch the game once and quit.** The first launch is what creates
   `BepInEx\plugins\` and `BepInEx\config\`. The installer needs those to exist.

On Steam, the game folder is usually:

| System | Path |
|---|---|
| Windows | `C:\Program Files (x86)\Steam\steamapps\common\Sea Power` |
| Linux | `~/.local/share/Steam/steamapps/common/Sea Power` |
| Steam Deck | `~/.local/share/Steam/steamapps/common/Sea Power` (or on the SD card) |
| Flatpak Steam | `~/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Sea Power` |

Right-click Sea Power in Steam → **Manage → Browse local files** opens it directly.

## 2. Install SeaPowerUX

Download the release zip and extract it anywhere. Inside you'll find `install.bat` / `install.ps1`
/ `install.sh` next to a `BepInEx\` folder. **Close Sea Power**, then run the installer for your
system. It finds the game automatically; if it can't, pass the path.

### Windows

Double-click **`install.bat`**.

Or, from PowerShell:

```powershell
.\install.ps1
# if the game is on another drive and isn't found automatically:
.\install.ps1 -GameDir "D:\Steam\steamapps\common\Sea Power"
```

### Linux / Steam Deck

On the Deck, switch to **Desktop Mode** first. Then from a terminal in the extracted folder:

```bash
chmod +x install.sh
./install.sh
# or point it at the game explicitly:
./install.sh --game-dir "$HOME/.local/share/Steam/steamapps/common/Sea Power"
```

The installer refuses to run while Sea Power is open, checks that BepInEx is present, copies the
files in, and verifies each plugin landed.

## 3. Check it worked

Launch Sea Power and load into a mission. You should see the SeaPowerUX toolbar with a toggle per
feature (Air Boss, PiP, …). If it isn't there, open `BepInEx\LogOutput.log` in the game folder and
search for `SeaPowerUX` — each plugin logs whether it loaded, and if it disabled itself it says
why.

## 4. Uninstalling

Run the same installer with the uninstall flag:

```
install.bat -Uninstall           (Windows, or  .\install.ps1 -Uninstall)
./install.sh --uninstall         (Linux / Deck)
```

That removes the pack's plugin folders. It **keeps** your saved window positions and settings
(`BepInEx\config\com.moseysauce.seapowerux.*.cfg`) so a reinstall picks up where you left off —
delete those files by hand if you want a clean slate.

To remove BepInEx entirely, delete the `BepInEx\` folder, `winhttp.dll`, and `doorstop_config.ini`
from the game directory.

## Troubleshooting

- **"BepInEx is not installed"** — you skipped step 1, or didn't launch the game once after
  extracting BepInEx so `BepInEx\plugins\` was never created.
- **"Could not find a Sea Power installation"** — pass the folder explicitly with `-GameDir`
  (Windows) or `--game-dir` (Linux). It's the folder containing `Sea Power.exe`.
- **"Sea Power is running"** — close the game and try again. Installing under the running process
  can crash it in confusing ways.
- **Toolbar doesn't appear** — confirm `BepInEx\plugins\SeaPowerUX.Core\SeaPowerUX.Core.dll` exists;
  every plugin needs Core, and it must come from the same release. Then check `LogOutput.log`.
- **A single feature is missing but others work** — that plugin likely disabled itself after a game
  update changed something it depends on. The log names what moved.
