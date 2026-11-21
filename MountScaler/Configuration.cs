using System;
using Dalamud.Configuration;

namespace MountScaler;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public bool Enabled = true;

    /// <summary>0 = vanilla mount size, 1 = fully follows your height change.</summary>
    public float Strength = 1f;

    /// <summary>Also follow extra model scale from Customize+ / Brio.</summary>
    public bool IncludeModelScale = true;

    /// <summary>Cancel out the mount's scale on the rider, who inherits it through the seat attachment.</summary>
    public bool KeepRiderSize = true;

    /// <summary>Also scale other players' mounts (by their own rendered look and height).</summary>
    public bool ScaleOthers = true;

    /// <summary>Raise/lower the camera's aim point by how far the scaled mount moved your seat.</summary>
    public bool MoveCamera = false;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
