using System;
using System.Numerics;
using Dalamud.Plugin.Services;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace CombatSimulator.Camera;

/// <summary>
/// Camera write authority, ordered by priority. Higher value wins the frame.
/// </summary>
public enum CameraOwner
{
    None = 0,
    /// <summary>Dynamic Camera combat framing (over-the-shoulder). Yields to every mode
    /// that owns player movement, including monster follow.</summary>
    DynamicCam = 15,
    /// <summary>Monster mode creature follow (orbit center only, user keeps rotation/zoom).</summary>
    MonsterFollow = 20,
    /// <summary>Dynamic Camera DEATH shot. Deliberately above MonsterFollow: the marquee use
    /// of monster mode after a death is the monster handling the corpse, and testing showed
    /// the follow centre stealing the camera away from the body the moment a grab started —
    /// the death shot is the one framing that scene and must keep the camera through it.</summary>
    DynamicDeath = 25,
    /// <summary>Fighting Mode side-view combat camera.</summary>
    Fighting2D = 30,
    /// <summary>Fighting Mode post-defeat camera (translate / bone follow / KO framing).</summary>
    FightingKO = 40,
    /// <summary>User-enabled Active Camera. Always wins.</summary>
    UserActiveCam = 50,
}

/// <summary>
/// One frame's worth of camera intent from a single owner. Null fields are left alone.
/// </summary>
public struct CameraRequest
{
    /// <summary>World position to orbit instead of the default target. Consumed by
    /// ActiveCameraController's getCameraPosition detour, not written here.</summary>
    public Vector3? OrbitCenter;
    public float? DirH;
    public float? DirV;
    /// <summary>Writes both Distance and InterpDistance.</summary>
    public float? Distance;
    /// <summary>Raise-only MaxDistance override; the coordinator saves the original
    /// once and restores it when no live request asks for a raise.</summary>
    public float? MaxDistanceAtLeast;
    /// <summary>Vertical field of view in radians. The coordinator widens the game's
    /// FoV limits while a request is live and restores them (and the original FoV)
    /// when none is.</summary>
    public float? Fov;
    public bool ClearInputH;
    public bool ClearInputV;
}

/// <summary>
/// Single per-frame authority over the game camera. Mode controllers submit requests
/// instead of writing camera fields directly; Apply picks the highest-priority live
/// request and performs the writes. This replaces the previous scheme where
/// FightingModeController, ActiveCameraController, and MonsterMode each wrote (or
/// toggled each other's active flags) independently and fought over the same fields.
///
/// Submissions live for two Apply passes: MonsterMode ticks on its own
/// Framework.Update handler whose order relative to the plugin's is
/// subscription-dependent, so a one-frame TTL could flicker.
/// </summary>
public sealed unsafe class CameraModeCoordinator
{
    private const int SlotTtl = 2;

    private struct Slot
    {
        public CameraRequest Request;
        public int Ttl;
    }

    private readonly Configuration config;
    private readonly IPluginLog log;

    // Indexed by owner; small fixed set, iterated high→low.
    private static readonly CameraOwner[] OwnersDescending =
    {
        CameraOwner.UserActiveCam,
        CameraOwner.FightingKO,
        CameraOwner.Fighting2D,
        CameraOwner.DynamicDeath,
        CameraOwner.MonsterFollow,
        CameraOwner.DynamicCam,
    };

    private readonly System.Collections.Generic.Dictionary<CameraOwner, Slot> slots = new();

    private float savedMaxDistance;
    private bool maxDistanceOverridden;
    private float savedFoV;
    private float savedMinFoV;
    private float savedMaxFoV;
    private bool fovOverridden;
    private CameraOwner lastLoggedOwner = CameraOwner.None;

    /// <summary>Winner of the last Apply pass (scalar write authority).</summary>
    public CameraOwner CurrentOwner { get; private set; } = CameraOwner.None;

    /// <summary>Highest-priority live orbit center, independent of the scalar winner:
    /// UserActiveCam submits no center, so e.g. a monster-follow center still applies
    /// while the user freely rotates/zooms — matching the pre-coordinator behavior.</summary>
    public Vector3? CurrentOrbitCenter { get; private set; }

    /// <summary>True when some mode supplies an orbit center this frame, i.e. the
    /// getCameraPosition hook must be live even if the user's Active Cam is off.</summary>
    public bool WantsOrbitHook => CurrentOrbitCenter.HasValue;

    public CameraModeCoordinator(Configuration config, IPluginLog log)
    {
        this.config = config;
        this.log = log;
    }

    public void Submit(CameraOwner owner, in CameraRequest request)
    {
        if (owner == CameraOwner.None)
            return;
        slots[owner] = new Slot { Request = request, Ttl = SlotTtl };
    }

    public void Release(CameraOwner owner) => slots.Remove(owner);

    /// <summary>
    /// Resolve and write the frame's camera state. Call exactly once per framework
    /// update, after every submitter has ticked.
    /// </summary>
    public void Apply(float dt)
    {
        // The user's Active Cam is represented as an implicit, always-live request
        // while enabled; ActiveCameraController does its own bone-orbit work when
        // it is the winner.
        if (config.EnableActiveCamera)
            slots[CameraOwner.UserActiveCam] = new Slot { Request = default, Ttl = SlotTtl };

        var winner = CameraOwner.None;
        CameraRequest winning = default;
        Vector3? orbitCenter = null;
        var anyMaxRaise = false;
        float maxRaise = 0f;

        foreach (var owner in OwnersDescending)
        {
            if (!slots.TryGetValue(owner, out var slot) || slot.Ttl <= 0)
                continue;

            if (winner == CameraOwner.None)
            {
                winner = owner;
                winning = slot.Request;
            }

            orbitCenter ??= slot.Request.OrbitCenter;

            if (slot.Request.MaxDistanceAtLeast.HasValue)
            {
                anyMaxRaise = true;
                maxRaise = MathF.Max(maxRaise, slot.Request.MaxDistanceAtLeast.Value);
            }
        }

        CurrentOwner = winner;
        CurrentOrbitCenter = orbitCenter;

        if (winner != lastLoggedOwner)
        {
            log.Debug($"CameraCoordinator: owner {lastLoggedOwner} -> {winner}");
            lastLoggedOwner = winner;
        }

        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr != null && camMgr->Camera != null)
            {
                var gameCam = camMgr->Camera;

                if (anyMaxRaise)
                {
                    if (!maxDistanceOverridden)
                    {
                        savedMaxDistance = gameCam->MaxDistance;
                        maxDistanceOverridden = true;
                    }
                    gameCam->MaxDistance = MathF.Max(savedMaxDistance, maxRaise);
                }
                else if (maxDistanceOverridden)
                {
                    gameCam->MaxDistance = savedMaxDistance;
                    maxDistanceOverridden = false;
                }

                // FoV: widen the game's limits so our value is not clamped, and keep the
                // original around so releasing the camera restores the player's lens.
                if (winning.Fov.HasValue)
                {
                    if (!fovOverridden)
                    {
                        savedFoV = gameCam->FoV;
                        savedMinFoV = gameCam->MinFoV;
                        savedMaxFoV = gameCam->MaxFoV;
                        fovOverridden = true;
                    }
                    gameCam->MinFoV = 0.01f;
                    gameCam->MaxFoV = 3.0f;
                    gameCam->FoV = winning.Fov.Value;
                }
                else if (fovOverridden)
                {
                    gameCam->FoV = savedFoV;
                    gameCam->MinFoV = savedMinFoV;
                    gameCam->MaxFoV = savedMaxFoV;
                    fovOverridden = false;
                }

                if (winning.DirH.HasValue)
                    gameCam->DirH = winning.DirH.Value;
                if (winning.DirV.HasValue)
                    gameCam->DirV = winning.DirV.Value;
                if (winning.Distance.HasValue)
                {
                    gameCam->Distance = winning.Distance.Value;
                    gameCam->InterpDistance = winning.Distance.Value;
                }
                if (winning.ClearInputH)
                {
                    gameCam->InputDeltaH = 0;
                    gameCam->InputDeltaHAdjusted = 0;
                }
                if (winning.ClearInputV)
                {
                    gameCam->InputDeltaV = 0;
                    gameCam->InputDeltaVAdjusted = 0;
                }
            }
        }
        catch
        {
            // Camera may be gone during logout/zone transitions; requests simply lapse.
        }

        // Age out slots so a submitter that stops (or crashes) releases the camera.
        foreach (var owner in OwnersDescending)
        {
            if (slots.TryGetValue(owner, out var slot))
            {
                slot.Ttl--;
                if (slot.Ttl <= 0)
                    slots.Remove(owner);
                else
                    slots[owner] = slot;
            }
        }
    }

    /// <summary>Drop all requests and restore MaxDistance / FoV (plugin unload / hard reset).</summary>
    public void Reset()
    {
        slots.Clear();
        CurrentOwner = CameraOwner.None;
        CurrentOrbitCenter = null;

        if (!maxDistanceOverridden && !fovOverridden)
            return;
        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr != null && camMgr->Camera != null)
            {
                var gameCam = camMgr->Camera;
                if (maxDistanceOverridden)
                    gameCam->MaxDistance = savedMaxDistance;
                if (fovOverridden)
                {
                    gameCam->FoV = savedFoV;
                    gameCam->MinFoV = savedMinFoV;
                    gameCam->MaxFoV = savedMaxFoV;
                }
            }
        }
        catch { }
        maxDistanceOverridden = false;
        fovOverridden = false;
    }
}
