using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace MountScaler;

/// <summary>Values from the last frame, shown in the config window's debug section.</summary>
public struct DebugState
{
    public bool HasPlayer;
    public bool Pillion;
    public bool HasMount;
    public uint MountId;
    public byte Race, Tribe, Sex, Slider;
    public float Height;          // rider skeleton scale: rendered height multiplier incl. Penumbra racial scaling
    public float CalculateHeight; // ModelContainer.CalculateHeight (height x ~1.75)
    public float HeightPerCalculate; // calibration taken while unmounted
    public float VanillaHeight;   // average (50% slider) member of your current clan and gender, unmodded
    public float ModelScaleY;     // rider draw object Scale.Y before our compensation (Customize+ / Brio)
    public float HeightRatio;     // your height / VanillaHeight (x model scale)
    public float? SummonRaceScale; // MountCustomize value the game used when the mount appeared
    public float? VisualRaceScale; // MountCustomize value for how you look now
    public float RaceAdjust;      // Visual / Summon
    public float Ratio;           // RaceAdjust x HeightRatio
    public float Factor;          // after strength
    public Vector3 MountOriginal;
    public Vector3 MountApplied;
    public Vector3 RiderOriginal;
    public Vector3 RiderApplied;
    public Vector3 RiderSkeletonScale;
    public Vector3 MountSkeletonScale;
}

/// <summary>
/// A scale we've written to a draw object, so it can be updated or undone.
/// The pointer is only dereferenced after checking it is still the live draw object.
/// </summary>
internal unsafe struct ScaleOverride
{
    public DrawObject* Target;
    public Vector3 Original;
    public Vector3 LastApplied;

    /// <summary>
    /// Picks up the draw object's own scale: on first sight, or if something else changed it
    /// since our last write. Returns true when <see cref="Original"/> was (re)captured.
    /// </summary>
    public bool Sync(DrawObject* current)
    {
        if (current != Target || !NearlyEqual(current->Scale, LastApplied))
        {
            Target = current;
            Original = current->Scale;
            LastApplied = Original;
            return true;
        }
        return false;
    }

    public void Apply(Vector3 scale)
    {
        if (!NearlyEqual(Target->Scale, scale))
            Target->Scale = scale;
        LastApplied = scale;
    }

    /// <summary>Puts the original scale back if <paramref name="current"/> is still the object we changed.</summary>
    public void Restore(DrawObject* current)
    {
        if (Target != null && current == Target && NearlyEqual(current->Scale, LastApplied))
            current->Scale = Original;
        Target = null;
    }

    public void Forget() => Target = null;

    public static bool NearlyEqual(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < 1e-8f;
}

/// <summary>
/// Scales the local player's own mount by how much their height differs from vanilla,
/// keeping the rider at their normal size. Race and the vanilla height slider are
/// already handled by the game.
/// </summary>
public sealed unsafe class MountScaling : IDisposable
{
    private readonly Configuration config;

    private ScaleOverride mount;
    private ScaleOverride rider;

    // The skeleton scale is the rendered height, but it also picks up the rider scale we
    // write (a frame late), so reading it while we're compensating loops. Instead we learn
    // skeleton-height-per-CalculateHeight while we're not touching the rider, and use
    // CalculateHeight (which our writes can't affect) times that while mounted.
    private const int CleanFramesNeeded = 3;
    private int cleanFrames;
    private DrawObject* calibratedFor;
    private float heightPerCalculate;

    // The game sizes a mount once, when it appears: a fixed per-mount size times the
    // MountCustomize value for the rider's race and gender at that moment. It doesn't
    // follow later race/gender changes (Glamourer) or height. We remember which race/gender
    // value it used so we can swap it for the current one. summonDraw is only compared,
    // never dereferenced.
    private DrawObject* summonDraw;
    private Vector3 summonScale;
    private float? summonRaceScale;

    public DebugState Debug;

    public MountScaling(Configuration config)
    {
        this.config = config;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        // Put everything back to normal size if we're unloaded mid-ride.
        Plugin.Framework.RunOnFrameworkThread(RestoreAll).Wait();
    }

    private void OnUpdate(IFramework framework)
    {
        Debug = default;

        var player = GetPlayer();
        Debug.HasPlayer = player != null;
        if (player == null)
        {
            mount.Forget();
            rider.Forget();
            return;
        }

        var playerDraw = player->DrawObject;
        Calibrate(player, playerDraw);

        var mountDraw = GetOwnMountDrawObject(player);
        if (mountDraw != null)
        {
            Debug.HasMount = true;
            Debug.MountId = player->Mount.MountId;
        }

        if (mountDraw == null || playerDraw == null || !config.Enabled || calibratedFor != playerDraw)
        {
            // Dismounted (the mount object is gone), riding pillion, disabled, or the rider's
            // model is new (plugin loaded mid-ride, Penumbra/Glamourer redraw): put everything
            // back for a few frames so Calibrate gets a clean reading.
            mount.Restore(mountDraw);
            rider.Restore(playerDraw);
            if (mountDraw == null)
                summonDraw = null; // next mount is a new summon
            return;
        }

        // Read the rider's own scale before we compensate it, so our write never feeds back in.
        rider.Sync(playerDraw);
        if (mount.Sync(mountDraw))
            RememberSummon(player, mountDraw, mount.Original);

        var ratio = ReadHeightRatio(player, rider.Original);
        if (ratio is not { } r)
            return;

        var factor = ComputeFactor(r);
        Debug.Factor = factor;

        mount.Apply(mount.Original * factor);

        // The rider is attached to a bone on the mount and inherits its scale; divide it back out.
        rider.Apply(config.KeepRiderSize ? rider.Original / factor : rider.Original);

        Debug.MountOriginal = mount.Original;
        Debug.MountApplied = mount.LastApplied;
        Debug.RiderOriginal = rider.Original;
        Debug.RiderApplied = rider.LastApplied;
        Debug.RiderSkeletonScale = SkeletonScale(playerDraw);
        Debug.MountSkeletonScale = SkeletonScale(mountDraw);
    }

    /// <summary>
    /// Records the race/gender size the game just used for this mount. Not on our own
    /// restore/re-capture of the same mount (same object, same scale): the rider may have
    /// changed look since it was summoned.
    /// </summary>
    private void RememberSummon(Character* player, DrawObject* mountDraw, Vector3 scale)
    {
        if (mountDraw == summonDraw && ScaleOverride.NearlyEqual(scale, summonScale))
            return;

        var c = player->DrawData.CustomizeData;
        summonDraw = mountDraw;
        summonScale = scale;
        summonRaceScale = MountSizes.RaceScale(player->Mount.MountId, c.Race, c.Tribe, c.Sex);
    }

    // No size limits; ReadHeightRatio only rejects garbage reads.
    private float ComputeFactor(float ratio) => 1f + config.Strength * (ratio - 1f);

    /// <summary>
    /// How much bigger the mount should be than the game made it:
    /// (race/gender size for how you look now ÷ the one the game used when it appeared)
    /// × (your rendered height ÷ an average, 50%-slider member of your current clan and gender).
    /// </summary>
    private float? ReadHeightRatio(Character* player, Vector3 riderScale)
    {
        var customize = player->DrawData.CustomizeData;
        Debug.Race = customize.Race;
        Debug.Tribe = customize.Tribe;
        Debug.Sex = customize.Sex;
        Debug.Slider = customize.Height;

        // Rendered height in racial-scaling-table units (checked in game: 1.42 with a 1.42 mod,
        // 1.24 vanilla), from CalculateHeight and the calibration taken while unmounted.
        var calculate = player->ModelContainer.CalculateHeight();
        Debug.CalculateHeight = calculate;
        Debug.HeightPerCalculate = heightPerCalculate;

        var height = calculate * heightPerCalculate;
        var model = riderScale.Y;
        Debug.Height = height;
        Debug.ModelScaleY = model;

        // The game's per-race mount sizes are for an average member, so compare against 50%.
        var vanilla = VanillaBaseline.Height(customize.Tribe, customize.Sex, 50);
        Debug.VanillaHeight = vanilla ?? 0;

        if (vanilla is not { } v || !IsSane(v) || !IsSane(height))
            return null;

        var heightRatio = height / v;
        if (config.IncludeModelScale && IsSane(model))
            heightRatio *= model;

        // Mounts without a MountCustomize entry don't vary by race; leave that part at 1.
        var visualRaceScale = MountSizes.RaceScale(player->Mount.MountId, customize.Race, customize.Tribe, customize.Sex);
        var raceAdjust = visualRaceScale is { } vr && summonRaceScale is { } sr && IsSane(vr) && IsSane(sr) ? vr / sr : 1f;

        Debug.HeightRatio = heightRatio;
        Debug.SummonRaceScale = summonRaceScale;
        Debug.VisualRaceScale = visualRaceScale;
        Debug.RaceAdjust = raceAdjust;

        var ratio = raceAdjust * heightRatio;
        Debug.Ratio = ratio;
        return ratio;
    }

    /// <summary>
    /// While we aren't scaling the rider, learn how the skeleton's scale (the rendered height,
    /// times the draw object scale) relates to CalculateHeight.
    /// </summary>
    private void Calibrate(Character* player, DrawObject* playerDraw)
    {
        // A scaled mount also leaks into the rider's skeleton, so both must be untouched.
        if (rider.Target != null || mount.Target != null || playerDraw == null)
        {
            cleanFrames = 0;
            return;
        }

        // Our last restore may still be in the skeleton for a frame or two.
        if (++cleanFrames < CleanFramesNeeded)
            return;

        var calculate = player->ModelContainer.CalculateHeight();
        var skeleton = SkeletonScale(playerDraw).Y;
        var model = playerDraw->Scale.Y;
        if (!IsSane(calculate) || !IsSane(skeleton) || !IsSane(model))
            return;

        heightPerCalculate = skeleton / model / calculate;
        calibratedFor = playerDraw;
    }

    private static Vector3 SkeletonScale(DrawObject* draw)
    {
        if (draw == null || draw->GetObjectType() != ObjectType.CharacterBase)
            return Vector3.Zero;
        var skeleton = ((CharacterBase*)draw)->Skeleton;
        return skeleton == null ? Vector3.Zero : skeleton->Transform.Scale;
    }

    private void RestoreAll()
    {
        var player = GetPlayer();
        mount.Restore(player != null ? GetOwnMountDrawObject(player) : null);
        rider.Restore(player != null ? player->DrawObject : null);
    }

    private static Character* GetPlayer()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        return player == null ? null : (Character*)player.Address;
    }

    /// <summary>The draw object of the mount the player owns and drives, or null.</summary>
    private DrawObject* GetOwnMountDrawObject(Character* player)
    {
        Debug.Pillion = Plugin.Condition[ConditionFlag.RidingPillion];
        if (Debug.Pillion)
            return null;

        var mountObject = player->Mount.MountObject;
        if (mountObject == null || player->Mount.MountId == 0)
            return null;

        return mountObject->DrawObject;
    }

    // Only rejects garbage reads (zero, negative, NaN, absurd); giant characters (height 20+) are fine.
    private static bool IsSane(float v) => float.IsFinite(v) && v is > 0.001f and < 10000f;
}
