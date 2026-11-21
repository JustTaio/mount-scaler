using System;
using System.Collections.Generic;
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
    public byte Race, Tribe, Sex, Slider; // as rendered
    public bool LookFromModel;            // read from the rendered model (vs the character's own data)
    public byte BaseRace, BaseSex;        // the character's own data, for comparison
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
    public float AimHeight;        // your CameraOffset.Y: the game's vanilla aim height
    public bool FirstPerson;
    public float CameraLiftTarget; // aim height x (factor - 1)
    public float CameraLift;       // eased value currently added
    public bool CameraFound;
    public float LookAtHeight;     // world camera look-at height (0x234) after our write
    public float LookAtHeightBase; // what the game/Cammy had there
    public float InterpolatedLookAtHeight; // 0x240, what the camera is gliding through
    public int OthersScaled;       // other players' mounts scaled this frame
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
/// Our lift on the world camera's look-at height. Added on top of whatever value the game or
/// another plugin (Cammy) set, so it can be undone. The pointer is only compared, never
/// dereferenced on its own.
/// </summary>
internal unsafe struct CameraOverride
{
    private float* target;
    private float baseValue;
    private float lastWritten;

    public float Base => baseValue;

    public void Apply(float* field, float lift)
    {
        var current = *field;
        if (field != target || MathF.Abs(current - lastWritten) > 1e-5f)
        {
            // First write, or someone else changed it since our last write: that's the new base.
            target = field;
            baseValue = current;
        }

        var value = baseValue + lift;
        *field = value;
        lastWritten = value;
    }

    public void Restore(float* field)
    {
        if (target != null && field == target && MathF.Abs(*field - lastWritten) <= 1e-5f)
            *field = baseValue;
        target = null;
    }

    public void Forget() => target = null;
}


/// <summary>Per-player state, kept while the player is drawn.</summary>
internal sealed unsafe class PlayerTrack
{
    /// <summary>Our compensation on this player's own model while they sit on a scaled mount.</summary>
    public ScaleOverride Self;

    // The skeleton scale is the rendered height, but it also picks up the rider scale we
    // write (a frame late), so reading it while we're compensating loops. Instead we learn
    // skeleton-height-per-CalculateHeight while we're not touching the player or their mount,
    // and use CalculateHeight (which our writes can't affect) times that while they ride.
    public int CleanFrames;
    public DrawObject* CalibratedFor; // only compared
    public float HeightPerCalculate;
}

/// <summary>Per-mount state, keyed by the driver, kept while they drive.</summary>
internal sealed unsafe class MountTrack
{
    public ScaleOverride Mount;

    // The game sizes a mount once, when it appears: a fixed per-mount size times the
    // MountCustomize value for the driver's race and gender at that moment. It doesn't follow
    // later race/gender changes (Glamourer) or height. We remember which value it used so we
    // can swap it for the current one. SummonDraw is only compared, never dereferenced.
    public DrawObject* SummonDraw;
    public Vector3 SummonScale;
    public float? SummonRaceScale;
}

/// <summary>
/// Scales every drawn player's mount (yours, and others' if enabled) by how much the driver's
/// rendered look and height differ from what the game sized the mount for, and keeps everyone
/// sitting on it at their true size. Also moves your camera with your own mount.
/// </summary>
public sealed unsafe class MountScaling : IDisposable
{
    private readonly Configuration config;

    private readonly Dictionary<uint, PlayerTrack> players = new();
    private readonly Dictionary<uint, MountTrack> mounts = new();

    // Reused every frame.
    private readonly Dictionary<uint, nint> visible = new();
    private readonly HashSet<uint> passengerIds = new();
    private readonly List<uint> drivers = new();
    private readonly Dictionary<uint, float> compensation = new();
    private readonly List<uint> stale = new();

    private const int CleanFramesNeeded = 3;

    private CameraOverride camera;
    private const float CameraEaseSeconds = 0.1f;
    private float cameraLift;

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

        var local = GetLocalPlayer();
        Debug.HasPlayer = local != null;
        if (local == null)
        {
            // Zoning: the objects are going away, so there's nothing safe to write to.
            foreach (var t in players.Values) t.Self.Forget();
            foreach (var t in mounts.Values) t.Mount.Forget();
            players.Clear();
            mounts.Clear();
            RestoreCamera(); // the camera outlives the player object
            return;
        }

        var localId = local->EntityId;
        Debug.Pillion = Plugin.Condition[ConditionFlag.RidingPillion];

        CollectVisible();
        FindDrivers(localId);

        // Calibrate from last frame's state, before we touch anything this frame.
        foreach (var (id, address) in visible)
        {
            var c = (Character*)address;
            var track = GetTrack(id);
            var mountActive = mounts.TryGetValue(id, out var m) && m.Mount.Target != null;
            Calibrate(c, track, mountActive);
        }

        // Size each driver's mount; note how much everyone on it must be compensated.
        compensation.Clear();
        float? localFactor = null;
        var others = 0;
        foreach (var id in drivers)
        {
            var isLocal = id == localId;
            var factor = UpdateMount(id, (Character*)visible[id], isLocal);
            if (factor is not { } f)
                continue;

            compensation[id] = f;
            foreach (var p in ((Character*)visible[id])->Mount.MountedEntityIds)
                if (p != 0 && p != id && visible.ContainsKey(p))
                    compensation[p] = f;

            if (isLocal) localFactor = f;
            else others++;
        }
        Debug.OthersScaled = others;

        // Riders: keep everyone on a scaled mount at their true size; put everyone else back.
        foreach (var (id, address) in visible)
        {
            var draw = ((Character*)address)->DrawObject;
            var track = players[id];
            if (compensation.TryGetValue(id, out var f))
            {
                track.Self.Sync(draw);
                track.Self.Apply(config.KeepRiderSize ? track.Self.Original / f : track.Self.Original);
                if (id == localId)
                {
                    Debug.RiderOriginal = track.Self.Original;
                    Debug.RiderApplied = track.Self.LastApplied;
                    Debug.RiderSkeletonScale = SkeletonScale(draw);
                }
            }
            else
            {
                track.Self.Restore(draw);
            }
        }

        // Mounts whose driver stopped driving (dismounted, became a passenger) or vanished.
        stale.Clear();
        foreach (var (id, track) in mounts)
        {
            if (drivers.Contains(id))
                continue;
            if (visible.TryGetValue(id, out var address))
                track.Mount.Restore(GetMountDrawObject((Character*)address));
            else
                track.Mount.Forget();
            stale.Add(id); // the next mount is a new summon
        }
        foreach (var id in stale) mounts.Remove(id);

        // Players no longer drawn: their objects may be gone, so just forget them.
        stale.Clear();
        foreach (var (id, track) in players)
        {
            if (visible.ContainsKey(id))
                continue;
            track.Self.Forget();
            stale.Add(id);
        }
        foreach (var id in stale) players.Remove(id);

        UpdateCamera(local, localFactor, (float)framework.UpdateDelta.TotalSeconds);
    }

    /// <summary>Every player the game is drawing, by entity id.</summary>
    private void CollectVisible()
    {
        visible.Clear();
        foreach (var p in Plugin.ObjectTable.PlayerObjects)
        {
            var c = (Character*)p.Address;
            if (c == null || c->DrawObject == null || c->EntityId == 0)
                continue;
            visible[c->EntityId] = (nint)c;
        }
    }

    /// <summary>Players riding their own mount: has a mount object, and isn't in someone else's seats.</summary>
    private void FindDrivers(uint localId)
    {
        passengerIds.Clear();
        drivers.Clear();

        foreach (var (id, address) in visible)
        {
            var c = (Character*)address;
            if (GetMountDrawObject(c) == null)
                continue;
            foreach (var p in c->Mount.MountedEntityIds)
                if (p != 0 && p != id)
                    passengerIds.Add(p);
        }

        foreach (var (id, address) in visible)
        {
            if (passengerIds.Contains(id) || GetMountDrawObject((Character*)address) == null)
                continue;
            if (id == localId && Debug.Pillion)
                continue;
            if (id != localId && !config.ScaleOthers)
                continue;
            drivers.Add(id);
        }
    }

    /// <summary>Scales one driver's mount. Returns the factor applied, or null if it isn't being scaled.</summary>
    private float? UpdateMount(uint id, Character* driver, bool isLocal)
    {
        var draw = driver->DrawObject;
        var mountDraw = GetMountDrawObject(driver);
        var player = GetTrack(id);
        if (!mounts.TryGetValue(id, out var track))
            mounts[id] = track = new MountTrack();

        if (isLocal)
        {
            Debug.HasMount = true;
            Debug.MountId = driver->Mount.MountId;
        }

        if (!config.Enabled || player.CalibratedFor != draw)
        {
            // Disabled, or this player's model is new (just appeared, plugin loaded mid-ride,
            // Penumbra/Glamourer redraw): leave the mount alone for a few frames so their
            // height can be measured cleanly.
            track.Mount.Restore(mountDraw);
            return null;
        }

        // Read the rider's own scale before we compensate it, so our write never feeds back in.
        player.Self.Sync(draw);
        if (track.Mount.Sync(mountDraw))
            RememberSummon(track, driver, mountDraw, track.Mount.Original);

        var ratio = ReadHeightRatio(driver, player, track, player.Self.Original, isLocal);
        if (ratio is not { } r)
        {
            track.Mount.Restore(mountDraw);
            return null;
        }

        var factor = ComputeFactor(r);
        track.Mount.Apply(track.Mount.Original * factor);

        if (isLocal)
        {
            Debug.Factor = factor;
            Debug.MountOriginal = track.Mount.Original;
            Debug.MountApplied = track.Mount.LastApplied;
            Debug.MountSkeletonScale = SkeletonScale(mountDraw);
        }
        return factor;
    }

    // World camera fields, from Cammy's GameCamera layout (not in ClientStructs yet; checked
    // working with Cammy on this patch 2026-11). 0x240 is the eased copy the game glides toward 0x234.
    private const int CameraModeOffset = 0x180;            // 0 = first person
    private const int CameraLookAtHeightOffset = 0x234;
    private const int CameraInterpolatedLookAtHeightOffset = 0x240;

    /// <summary>The world camera's look-at height field, or null if there's no camera or it looks wrong.</summary>
    private static float* GetLookAtField(out byte* cam)
    {
        var manager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Instance();
        cam = manager != null ? (byte*)manager->Camera : null;
        if (cam == null)
            return null;

        var field = (float*)(cam + CameraLookAtHeightOffset);
        // Layout changed under us after a patch: don't touch it.
        return float.IsFinite(*field) && MathF.Abs(*field) < 1000f ? field : null;
    }

    private void RestoreCamera()
    {
        cameraLift = 0f;
        var field = GetLookAtField(out _);
        if (field != null)
            camera.Restore(field);
        else
            camera.Forget();
    }

    /// <summary>
    /// Scales the camera's aim height with your own mount: the game aims at your character's
    /// CameraOffset height (e.g. 1.50 on a vanilla mount), so we add that × (factor − 1) to the
    /// world camera's look-at height, on top of whatever the game or Cammy set, eased in and out.
    /// Zoom is left alone, and nothing changes in first person.
    /// </summary>
    private void UpdateCamera(Character* player, float? factor, float dt)
    {
        var field = GetLookAtField(out var cam);
        var firstPerson = cam != null && *(int*)(cam + CameraModeOffset) == 0;

        var aimHeight = player->CameraOffset.Y;
        var desired = 0f;
        if (config.MoveCamera && field != null && !firstPerson && factor is { } f && IsSane(f) && float.IsFinite(aimHeight))
            desired = aimHeight * (f - 1f);

        // Glide: close ~95% of the gap in about a third of a second (the game eases on top).
        var k = 1f - MathF.Exp(-Math.Max(dt, 0f) / CameraEaseSeconds);
        cameraLift += (desired - cameraLift) * k;
        if (desired == 0f && MathF.Abs(cameraLift) < 0.001f)
            cameraLift = 0f;

        Debug.AimHeight = aimHeight;
        Debug.FirstPerson = firstPerson;
        Debug.CameraLift = cameraLift;
        Debug.CameraLiftTarget = desired;

        if (field == null)
        {
            camera.Forget();
            return;
        }

        if (cameraLift == 0f)
            camera.Restore(field);
        else
            camera.Apply(field, cameraLift);

        Debug.CameraFound = true;
        Debug.LookAtHeight = *field;
        Debug.LookAtHeightBase = camera.Base;
        Debug.InterpolatedLookAtHeight = *(float*)(cam + CameraInterpolatedLookAtHeightOffset);
    }

    /// <summary>
    /// Records the race/gender size the game just used for this mount. Not on our own
    /// restore/re-capture of the same mount (same object, same scale): the driver may have
    /// changed look since it was summoned.
    /// </summary>
    private static void RememberSummon(MountTrack track, Character* driver, DrawObject* mountDraw, Vector3 scale)
    {
        if (mountDraw == track.SummonDraw && ScaleOverride.NearlyEqual(scale, track.SummonScale))
            return;

        var c = GetLook(driver, out _);
        track.SummonDraw = mountDraw;
        track.SummonScale = scale;
        track.SummonRaceScale = MountSizes.RaceScale(driver->Mount.MountId, c.Race, c.Tribe, c.Sex);
    }

    // No size limits; ReadHeightRatio only rejects garbage reads.
    private float ComputeFactor(float ratio) => 1f + config.Strength * (ratio - 1f);

    /// <summary>
    /// How much bigger the mount should be than the game made it:
    /// (race/gender size for how the driver looks now ÷ the one the game used when it appeared)
    /// × (their rendered height ÷ an average, 50%-slider member of their current clan and gender).
    /// </summary>
    private float? ReadHeightRatio(Character* driver, PlayerTrack player, MountTrack track, Vector3 riderScale, bool isLocal)
    {
        var customize = GetLook(driver, out var fromModel);

        // Rendered height in racial-scaling-table units (checked in game: 1.42 with a 1.42 mod,
        // 1.24 vanilla), from CalculateHeight and the calibration taken while unmounted.
        var calculate = driver->ModelContainer.CalculateHeight();
        var height = calculate * player.HeightPerCalculate;
        var model = riderScale.Y;

        // The game's per-race mount sizes are for an average member, so compare against 50%.
        var vanilla = VanillaBaseline.Height(customize.Tribe, customize.Sex, 50);

        var visualRaceScale = MountSizes.RaceScale(driver->Mount.MountId, customize.Race, customize.Tribe, customize.Sex);
        // Mounts without a MountCustomize entry don't vary by race; leave that part at 1.
        var raceAdjust = visualRaceScale is { } vr && track.SummonRaceScale is { } sr && IsSane(vr) && IsSane(sr) ? vr / sr : 1f;

        float? ratio = null;
        var heightRatio = 0f;
        if (vanilla is { } v && IsSane(v) && IsSane(height))
        {
            heightRatio = height / v;
            if (config.IncludeModelScale && IsSane(model))
                heightRatio *= model;
            ratio = raceAdjust * heightRatio;
        }

        if (isLocal)
        {
            var baseCustomize = driver->DrawData.CustomizeData;
            Debug.LookFromModel = fromModel;
            Debug.BaseRace = baseCustomize.Race;
            Debug.BaseSex = baseCustomize.Sex;
            Debug.Race = customize.Race;
            Debug.Tribe = customize.Tribe;
            Debug.Sex = customize.Sex;
            Debug.Slider = customize.Height;
            Debug.CalculateHeight = calculate;
            Debug.HeightPerCalculate = player.HeightPerCalculate;
            Debug.Height = height;
            Debug.ModelScaleY = model;
            Debug.VanillaHeight = vanilla ?? 0;
            Debug.HeightRatio = heightRatio;
            Debug.SummonRaceScale = track.SummonRaceScale;
            Debug.VisualRaceScale = visualRaceScale;
            Debug.RaceAdjust = raceAdjust;
            Debug.Ratio = ratio ?? 0;
        }

        return ratio;
    }

    /// <summary>
    /// While we aren't touching this player or their mount, learn how their skeleton's scale
    /// (the rendered height, times the draw object scale) relates to CalculateHeight.
    /// </summary>
    private static void Calibrate(Character* c, PlayerTrack track, bool mountActive)
    {
        var draw = c->DrawObject;
        // A scaled mount also leaks into the rider's skeleton, so both must be untouched.
        if (track.Self.Target != null || mountActive || draw == null)
        {
            track.CleanFrames = 0;
            return;
        }

        // Our last restore may still be in the skeleton for a frame or two.
        if (++track.CleanFrames < CleanFramesNeeded)
            return;

        var calculate = c->ModelContainer.CalculateHeight();
        var skeleton = SkeletonScale(draw).Y;
        var model = draw->Scale.Y;
        if (!IsSane(calculate) || !IsSane(skeleton) || !IsSane(model))
            return;

        track.HeightPerCalculate = skeleton / model / calculate;
        track.CalibratedFor = draw;
    }

    private PlayerTrack GetTrack(uint id)
    {
        if (!players.TryGetValue(id, out var track))
            players[id] = track = new PlayerTrack();
        return track;
    }

    /// <summary>
    /// Race, clan, gender and height as rendered. Glamourer changes the rendered model's copy
    /// and leaves the character's own data alone, and the game sizes mounts from the
    /// rendered one, so prefer it.
    /// </summary>
    private static CustomizeData GetLook(Character* c, out bool fromModel)
    {
        var draw = c->DrawObject;
        if (draw != null
            && draw->GetObjectType() == ObjectType.CharacterBase
            && ((CharacterBase*)draw)->GetModelType() == CharacterBase.ModelType.Human)
        {
            fromModel = true;
            return ((Human*)draw)->Customize;
        }

        fromModel = false;
        return c->DrawData.CustomizeData;
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
        if (GetLocalPlayer() != null)
        {
            CollectVisible();
            foreach (var (id, track) in players)
                track.Self.Restore(visible.TryGetValue(id, out var a) ? ((Character*)a)->DrawObject : null);
            foreach (var (id, track) in mounts)
                track.Mount.Restore(visible.TryGetValue(id, out var a) ? GetMountDrawObject((Character*)a) : null);
        }
        players.Clear();
        mounts.Clear();
        RestoreCamera();
    }

    private static Character* GetLocalPlayer()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        return player == null ? null : (Character*)player.Address;
    }

    /// <summary>The draw object of the mount this character owns, or null.</summary>
    private static DrawObject* GetMountDrawObject(Character* c)
    {
        var mountObject = c->Mount.MountObject;
        if (mountObject == null || c->Mount.MountId == 0)
            return null;
        return mountObject->DrawObject;
    }

    // Only rejects garbage reads (zero, negative, NaN, absurd); giant characters (height 20+) are fine.
    private static bool IsSane(float v) => float.IsFinite(v) && v is > 0.001f and < 10000f;
}
