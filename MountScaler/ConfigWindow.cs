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

        if (d.Pillion)
        {
            ImGui.TextUnformatted("Riding pillion: leaving the mount alone.");
            return;
        }
        if (!d.HasMount)
        {
            ImGui.TextUnformatted("Not on your own mount.");
            return;
        }

        ImGui.TextUnformatted($"Race {d.Race}, clan {d.Tribe}, {(d.Sex == 0 ? "male" : "female")}, height slider {d.Slider}%");
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
