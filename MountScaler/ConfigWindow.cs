using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MountScaler;

public sealed class ConfigWindow : Window
{
    private readonly Configuration config;
    private readonly MountScaling scaling;

    public ConfigWindow(Configuration config, MountScaling scaling)
        : base("Mount Scaler###MountScalerConfig", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.config = config;
        this.scaling = scaling;
    }

    public override void Draw()
    {
        var changed = false;

        changed |= ImGui.Checkbox("Enabled", ref config.Enabled);

        var strength = config.Strength * 100f;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderFloat("Strength", ref strength, 0f, 100f, "%.0f%%"))
        {
            config.Strength = strength / 100f;
            changed = true;
        }
        Hint("0% = vanilla mount size, 100% = follows your height change fully.");

        changed |= ImGui.Checkbox("Keep rider at normal size", ref config.KeepRiderSize);
        Hint("You sit on the mount and would otherwise grow and shrink with it.");

        changed |= ImGui.Checkbox("Scale other players' mounts", ref config.ScaleOthers);
        Hint("Sizes everyone's mount for its driver, using the mods you see on them.");

        changed |= ImGui.Checkbox("Move camera with mount height", ref config.MoveCamera);
        Hint("Aims the camera higher or lower as your seat moves. Zoom is left alone.");

        changed |= ImGui.Checkbox("Include Customize+ / Brio scale", ref config.IncludeModelScale);

        if (!VanillaBaseline.Loaded)
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), "Couldn't read the vanilla height table; scaling is off.");

        if (ImGui.CollapsingHeader("Debug"))
            DrawDebug();

        if (changed)
            config.Save();
    }

    private void DrawDebug()
    {
        var d = scaling.Debug;
        if (!d.HasPlayer)
        {
            ImGui.TextUnformatted("No local player.");
            return;
        }

        ImGui.TextUnformatted($"Other players' mounts scaled: {d.OthersScaled}");

        if (d.Pillion)
        {
            ImGui.TextUnformatted("Riding pillion: the driver's mount is scaled for them; you're kept at your size.");
            return;
        }
        if (!d.HasMount)
        {
            ImGui.TextUnformatted("Not on your own mount.");
            return;
        }

        ImGui.TextUnformatted($"Look: race {d.Race}, clan {d.Tribe}, {(d.Sex == 0 ? "male" : "female")}, height slider {d.Slider}%{(d.LookFromModel ? "" : " (character data)")}");
        if (d.BaseRace != d.Race || d.BaseSex != d.Sex)
            ImGui.TextUnformatted($"Character underneath: race {d.BaseRace}, {(d.BaseSex == 0 ? "male" : "female")} (Glamourer)");
        if (d.HeightPerCalculate <= 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), "Not scaling right now (disabled, or measuring your height).");
            return;
        }
        ImGui.TextUnformatted($"Your height: {d.Height:F4}");
        ImGui.TextUnformatted($"CalculateHeight: {d.CalculateHeight:F4} (x {d.HeightPerCalculate:F4})");
        ImGui.TextUnformatted($"Average height for your look (50%): {d.VanillaHeight:F4}");
        ImGui.TextUnformatted($"Model scale Y: {d.ModelScaleY:F4}");
        ImGui.TextUnformatted($"Height ratio: {d.HeightRatio:F4}");
        ImGui.Separator();
        ImGui.TextUnformatted($"Mount id: {d.MountId}");
        ImGui.TextUnformatted($"Mount race size when summoned: {Opt(d.SummonRaceScale)}, for your look now: {Opt(d.VisualRaceScale)}");
        ImGui.TextUnformatted($"Race adjust: {d.RaceAdjust:F4}");
        ImGui.TextUnformatted($"Ratio: {d.Ratio:F4}   Factor: {d.Factor:F3}x");
        ImGui.TextUnformatted($"Mount scale: {Fmt(d.MountOriginal)} -> {Fmt(d.MountApplied)}");
        ImGui.TextUnformatted($"Rider scale: {Fmt(d.RiderOriginal)} -> {Fmt(d.RiderApplied)}");
        ImGui.TextUnformatted($"Rider skeleton scale: {Fmt(d.RiderSkeletonScale)}");
        ImGui.TextUnformatted($"Mount skeleton scale: {Fmt(d.MountSkeletonScale)}");
        ImGui.Separator();
        ImGui.TextUnformatted($"Vanilla aim height: {d.AimHeight:F3}   Camera lift: {d.CameraLift:F3} (target {d.CameraLiftTarget:F3}){(d.FirstPerson ? "  [first person]" : "")}");
        if (d.CameraFound)
            ImGui.TextUnformatted($"Camera look-at height: {d.LookAtHeightBase:F3} -> {d.LookAtHeight:F3} (gliding: {d.InterpolatedLookAtHeight:F3})");
        else
            ImGui.TextUnformatted("Camera look-at height: not found");
    }

    private static void Hint(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1f));
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    private static string Opt(float? v) => v is { } x ? x.ToString("F2") : "n/a";

    private static string Fmt(Vector3 v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";
}
