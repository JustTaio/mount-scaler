using System;
using Dalamud.Configuration;

namespace MountScaler;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public bool Enabled = true;

    // 0 = vanilla size, 1 = fully follows your height.
    public float Strength = 1f;

    public bool IncludeModelScale = true;

    // The rider inherits the mount's scale through the seat; cancel it out.
    public bool KeepRiderSize = true;

    public bool ScaleOthers = true;

    public bool MoveCamera = false;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
