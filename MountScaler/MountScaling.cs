using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace MountScaler;

public struct DebugState
{
    public bool HasPlayer;
    public bool Pillion;
    public bool HasMount;
    public uint MountId;
    public byte Race, Tribe, Sex, Slider;
    public bool LookFromModel;
    public byte BaseRace, BaseSex;
    public float Height;
    public float CalculateHeight;
    public float HeightPerCalculate;
    public float VanillaHeight;
    public float ModelScaleY;
    public float HeightRatio;
    public float? SummonRaceScale;
    public float? VisualRaceScale;
    public float RaceAdjust;
    public float Ratio;
    public float Factor;
    public Vector3 MountOriginal;
    public Vector3 MountApplied;
    public Vector3 RiderOriginal;
    public Vector3 RiderApplied;
    public Vector3 RiderSkeletonScale;
    public Vector3 MountSkeletonScale;
    public float AimHeight;
    public bool FirstPerson;
    public float CameraLiftTarget;
    public float CameraLift;
    public bool CameraFound;
    public float LookAtHeight;
    public float LookAtHeightBase;
    public float InterpolatedLookAtHeight;
    public int OthersScaled;
}

// Only dereferenced after checking it's still the live draw object.
internal unsafe struct ScaleOverride
{
    public DrawObject* Target;
    public Vector3 Original;
    public Vector3 LastApplied;

    // Re-captures the original when something else changed the scale since our last write.
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

    public void Restore(DrawObject* current)
    {
        if (Target != null && current == Target && NearlyEqual(current->Scale, LastApplied))
            current->Scale = Original;
        Target = null;
    }

    public void Forget() => Target = null;

    public static bool NearlyEqual(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < 1e-8f;
}

// Added on top of what the game or Cammy set, so it can be undone. The pointer is only compared.
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
            // Someone else changed it since our last write: that's the new base.
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

internal sealed unsafe class PlayerTrack
{
    public ScaleOverride Self;

    // The skeleton scale picks up our own rider write a frame late, so it's calibrated against
    // CalculateHeight while untouched, and CalculateHeight is used while riding.
    public int CleanFrames;
    public DrawObject* CalibratedFor;
    public float HeightPerCalculate;

    // After a redraw the skeleton briefly reports a default size; wait for a steady reading.
    public float LastReading;
    public int StableFrames;
}

public struct OtherDebug
{
    public string Name;
    public string Status;
    public float Ratio;
    public float Factor;
    public float HeightPerCalculate;
    public int StableFrames;
    public bool ModelVisible;
    public byte LoadState;
    public Vector3 MountSeen;
    public Vector3 MountOriginal;
    public Vector3 MountApplied;
    public int GameResets;
}

internal sealed unsafe class MountTrack
{
    public ScaleOverride Mount;

    // The game sizes a mount once, when it appears, from the driver's race and gender then.
    // Remember that value so later changes (Glamourer) can be swapped in.
    public DrawObject* SummonDraw;
    public Vector3 SummonScale;
    public float? SummonRaceScale;

    public int GameResets;
}

public sealed unsafe class MountScaling : IDisposable
{
    private readonly Configuration config;

    private readonly Dictionary<uint, PlayerTrack> players = new();
    private readonly Dictionary<uint, MountTrack> mounts = new();

    private readonly Dictionary<uint, nint> visible = new();
    private readonly HashSet<uint> passengerIds = new();
    private readonly List<uint> drivers = new();
    private readonly Dictionary<uint, float> compensation = new();
    private readonly List<uint> stale = new();

    private const int CleanFramesNeeded = 3;
    private const int StableFramesNeeded = 20; // ~1/3 s at 60 fps

    public bool CollectDetails;
    public readonly List<OtherDebug> Others = new();
    private readonly Dictionary<uint, string> names = new();

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
        Plugin.Framework.RunOnFrameworkThread(RestoreAll).Wait();
    }

    private void OnUpdate(IFramework framework)
    {
        Debug = default;
        Others.Clear();

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

        if (CollectDetails)
        {
            foreach (var id in passengerIds)
                if (id != localId && visible.ContainsKey(id))
                    Others.Add(new OtherDebug { Name = NameOf(id), Status = compensation.ContainsKey(id) ? "passenger, kept at size" : "passenger" });
        }

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

        // Their objects may be gone; just forget them.
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

    private void CollectVisible()
    {
        visible.Clear();
        names.Clear();
        foreach (var p in Plugin.ObjectTable.PlayerObjects)
        {
            var c = (Character*)p.Address;
            if (c == null || c->DrawObject == null || c->EntityId == 0)
                continue;
            visible[c->EntityId] = (nint)c;
            if (CollectDetails)
                names[c->EntityId] = p.Name.TextValue;
        }
    }

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

        var seen = mountDraw->Scale;
        var detail = new OtherDebug
        {
            Name = isLocal ? "" : NameOf(id),
            MountSeen = seen,
            HeightPerCalculate = player.HeightPerCalculate,
            StableFrames = player.StableFrames,
            ModelVisible = draw->IsVisible,
            LoadState = draw->LoadState,
        };

        if (!config.Enabled || player.CalibratedFor != draw)
        {
            // Wait until a new model's height has been measured cleanly.
            track.Mount.Restore(mountDraw);
            detail.Status = !config.Enabled ? "plugin off" : $"measuring height ({Math.Min(player.StableFrames, StableFramesNeeded)}/{StableFramesNeeded})";
            AddDetail(isLocal, detail);
            return null;
        }

        // Read the rider's own scale before compensating, so our write never feeds back in.
        player.Self.Sync(draw);
        var sameMount = track.Mount.Target == mountDraw;
        if (track.Mount.Sync(mountDraw))
        {
            if (sameMount)
                track.GameResets++;
            RememberSummon(track, driver, mountDraw, track.Mount.Original);
        }

        var ratio = ReadHeightRatio(driver, player, track, player.Self.Original, isLocal);
        if (ratio is not { } r)
        {
            track.Mount.Restore(mountDraw);
            detail.Status = "bad height reading";
            AddDetail(isLocal, detail);
            return null;
        }

        var factor = ComputeFactor(r);
        track.Mount.Apply(track.Mount.Original * factor);

        detail.Status = "scaled";
        detail.Ratio = r;
        detail.Factor = factor;
        detail.MountOriginal = track.Mount.Original;
        detail.MountApplied = track.Mount.LastApplied;
        detail.GameResets = track.GameResets;
        AddDetail(isLocal, detail);

        if (isLocal)
        {
            Debug.Factor = factor;
            Debug.MountOriginal = track.Mount.Original;
            Debug.MountApplied = track.Mount.LastApplied;
            Debug.MountSkeletonScale = SkeletonScale(mountDraw);
        }
        return factor;
    }

    // World camera fields from Cammy's GameCamera layout (not in ClientStructs). 0x240 eases toward 0x234.
    private const int CameraModeOffset = 0x180;            // 0 = first person
    private const int CameraLookAtHeightOffset = 0x234;
    private const int CameraInterpolatedLookAtHeightOffset = 0x240;

    private static float* GetLookAtField(out byte* cam)
    {
        var manager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Instance();
        cam = manager != null ? (byte*)manager->Camera : null;
        if (cam == null)
            return null;

        var field = (float*)(cam + CameraLookAtHeightOffset);
        // Layout changed after a patch: don't touch it.
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

    // Adds CameraOffset.Y x (factor - 1) to the look-at height, eased. Nothing in first person.
    private void UpdateCamera(Character* player, float? factor, float dt)
    {
        var field = GetLookAtField(out var cam);
        var firstPerson = cam != null && *(int*)(cam + CameraModeOffset) == 0;

        var aimHeight = player->CameraOffset.Y;
        var desired = 0f;
        if (config.MoveCamera && field != null && !firstPerson && factor is { } f && IsSane(f) && float.IsFinite(aimHeight))
            desired = aimHeight * (f - 1f);

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

    // Not on our own restore of the same mount: the driver may have changed look since it appeared.
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

    // (race size for the current look / the one used at summon) x (height / 50%-slider clan average)
    private float? ReadHeightRatio(Character* driver, PlayerTrack player, MountTrack track, Vector3 riderScale, bool isLocal)
    {
        var customize = GetLook(driver, out var fromModel);

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

        // A model still loading after a redraw can report a default-sized skeleton.
        if (!draw->IsVisible)
        {
            track.StableFrames = 0;
            return;
        }

        var calculate = c->ModelContainer.CalculateHeight();
        var skeleton = SkeletonScale(draw).Y;
        var model = draw->Scale.Y;
        if (!IsSane(calculate) || !IsSane(skeleton) || !IsSane(model))
        {
            track.StableFrames = 0;
            return;
        }

        var reading = skeleton / model / calculate;
        if (MathF.Abs(reading - track.LastReading) <= reading * 1e-4f)
            track.StableFrames++;
        else
            track.StableFrames = 0;
        track.LastReading = reading;

        if (track.StableFrames < StableFramesNeeded)
            return;

        track.HeightPerCalculate = reading;
        track.CalibratedFor = draw;
    }

    private void AddDetail(bool isLocal, OtherDebug detail)
    {
        if (CollectDetails && !isLocal)
            Others.Add(detail);
    }

    private string NameOf(uint id) => names.TryGetValue(id, out var n) ? n : $"#{id:X}";

    private PlayerTrack GetTrack(uint id)
    {
        if (!players.TryGetValue(id, out var track))
            players[id] = track = new PlayerTrack();
        return track;
    }

    // Prefer the rendered model's look: Glamourer changes that copy, and mounts are sized from it.
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

    private static DrawObject* GetMountDrawObject(Character* c)
    {
        var mountObject = c->Mount.MountObject;
        if (mountObject == null || c->Mount.MountId == 0)
            return null;
        return mountObject->DrawObject;
    }

    // Only rejects garbage reads; giant characters are fine.
    private static bool IsSane(float v) => float.IsFinite(v) && v is > 0.001f and < 10000f;
}
