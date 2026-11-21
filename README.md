# Mount Scaler

A Dalamud plugin for Final Fantasy XIV that scales mounts to match their riders' height.

The game sizes each mount once, when it's summoned, for the rider's race and gender at an average height. If someone is taller or shorter (height slider, Penumbra racial scaling, Customize+ / Brio), or changes race or gender with Glamourer, their mount stays the same size. Mount Scaler fixes that by sizing the mount for how its driver actually looks.

- Your mount, and other players' mounts too (can be turned off). Others are scaled by the mods you see on them, e.g. through Lightless or Mare sync.
- Everyone sitting on a scaled mount, passengers included, stays their normal size.
- Client-side and visual only: only you see it, and hitboxes and nameplates are unchanged.
- Updates live, so toggling a height mod mid-ride takes effect straight away.
- Turning the plugin off or dismounting puts everything back to normal.

## Install

1. In game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this URL, click **+**, tick **Enabled**, then **Save and Close**:
   ```
   https://raw.githubusercontent.com/JustTaio/mount-scaler/main/repo.json
   ```
3. Type `/xlplugins`, search for **Mount Scaler** and install it.

Type `/mountscale` to open the settings.

## Settings

- **Strength**: 0% leaves mounts vanilla, 100% follows your height change fully.
- **Keep rider at normal size**: you're attached to the mount and would otherwise grow and shrink with it.
- **Scale other players' mounts** (on by default): sizes everyone's mount for its driver, not just yours.
- **Move camera with mount height** (off by default): aims the camera higher or lower as your seat moves with the mount's size, gliding when you mount and dismount. Zoom is left alone.
- **Include Customize+ / Brio scale**: also follow extra model scale from those plugins.
- **Debug**: shows your height, the vanilla height for your character and the scale being applied.
