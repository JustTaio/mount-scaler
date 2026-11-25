using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace MountScaler;

public static class MountSizes
{
    private static readonly Dictionary<(uint, byte, byte, byte), float?> Cache = new();

    public static float? RaceScale(uint mountId, byte race, byte tribe, byte sex)
    {
        var key = (mountId, race, tribe, sex);
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        float? result = null;
        var row = Plugin.DataManager.GetExcelSheet<Mount>().GetRowOrDefault(mountId);
        if (row?.MountCustomize.ValueNullable is { } c)
        {
            var male = sex == 0;
            ushort? value = race switch
            {
                1 when tribe == 2 => male ? c.HyurHighlanderMaleScale : c.HyurHighlanderFemaleScale,
                1 => male ? c.HyurMidlanderMaleScale : c.HyurMidlanderFemaleScale,
                2 => male ? c.ElezenMaleScale : c.ElezenFemaleScale,
                3 => male ? c.LalaMaleScale : c.LalaFemaleScale,
                4 => male ? c.MiqoMaleScale : c.MiqoFemaleScale,
                5 => male ? c.RoeMaleScale : c.RoeFemaleScale,
                6 => male ? c.AuRaMaleScale : c.AuRaFemaleScale,
                7 => male ? c.HrothgarMaleScale : c.HrothgarFemaleScale,
                8 => male ? c.VieraMaleScale : c.VieraFemaleScale,
                _ => null,
            };
            if (value is { } v)
                result = v / 100f;
        }

        Cache[key] = result;
        return result;
    }
}
