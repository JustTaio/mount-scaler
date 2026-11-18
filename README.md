# Mount Scaler

A Dalamud plugin for Final Fantasy XIV that scales your mount to match your character's height.

The game already sizes every mount for your race and gender, but it uses the vanilla height table. If you change your height with Penumbra racial scaling (or Customize+ / Brio), your mount stays its normal size. Mount Scaler fixes that by scaling the mount by how much taller or shorter you are than vanilla for your clan, gender and height slider. You stay your normal size on it.

- Only your own mount, and only while you're driving it (not when riding pillion).
- Client-side and visual only: only you see it, and hitboxes, nameplates and the camera are unchanged.
- Updates live, so toggling your height mod mid-ride takes effect straight away.
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
- **Include Customize+ / Brio scale**: also follow extra model scale from those plugins.
- **Debug**: shows your height, the vanilla height for your character and the scale being applied.
