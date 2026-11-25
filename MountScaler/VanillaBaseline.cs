using System;

namespace MountScaler;

// Vanilla racial scaling from human.cmp. Lumina reads the game's own files, so Penumbra mods don't affect it.
public static class VanillaBaseline
{
    private const string CmpPath = "chara/xls/charamake/human.cmp";

    // 56-byte entries in Penumbra's layout, ten per race, the race's two clans first.
    // 0x2C800 is the current layout, 0x2A800 the older one.
    private static readonly int[] RacialScalingStarts = [0x2C800, 0x2A800];
    private const int EntrySize = 56;
    private const int ClanCount = 16;

    private static int EntryIndex(int clan) => (clan - 1) / 2 * 10 + (clan - 1) % 2;

    private static float[]? table; // [clan * 4 + 0..3] = male min, male max, female min, female max

    public static bool Loaded => table != null;

    public static void Load()
    {
        try
        {
            var file = Plugin.DataManager.GetFile(CmpPath);
            if (file == null)
            {
                Plugin.Log.Warning($"Couldn't read {CmpPath}.");
                return;
            }

            foreach (var start in RacialScalingStarts)
            {
                if (file.Data.Length < start + EntrySize * (EntryIndex(ClanCount) + 1))
                    continue;

                var values = new float[ClanCount * 4];
                var ok = true;
                for (var clan = 0; clan < ClanCount && ok; clan++)
                {
                    var o = start + EntryIndex(clan + 1) * EntrySize;
                    values[clan * 4 + 0] = BitConverter.ToSingle(file.Data, o);
                    values[clan * 4 + 1] = BitConverter.ToSingle(file.Data, o + 4);
                    values[clan * 4 + 2] = BitConverter.ToSingle(file.Data, o + 16);
                    values[clan * 4 + 3] = BitConverter.ToSingle(file.Data, o + 20);
                    for (var k = 0; k < 4; k++)
                        ok &= values[clan * 4 + k] is > 0.5f and < 1.5f && float.IsFinite(values[clan * 4 + k]);
                }

                if (!ok)
                    continue;

                table = values;
                Plugin.Log.Information($"Read vanilla racial scaling table at 0x{start:X}.");
                return;
            }

            Plugin.Log.Warning($"Racial scaling table not found in {CmpPath}.");
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, $"Failed to read {CmpPath}.");
        }
    }

    // tribe 1-16, sex 0 male / 1 female, slider 0-100
    public static float? Height(byte tribe, byte sex, byte slider)
    {
        if (table == null || tribe is < 1 or > ClanCount || sex > 1)
            return null;

        var i = (tribe - 1) * 4 + sex * 2;
        var t = Math.Clamp(slider, (byte)0, (byte)100) / 100f;
        return table[i] + (table[i + 1] - table[i]) * t;
    }
}
