using System.Numerics;
using System.Reflection;
using System.Text;
using CombatSimulator.ActionCombat;
using CombatSimulator.Ai;
using CombatSimulator.Animation;
using CombatSimulator.Battle;
using CombatSimulator.Combat;
using CombatSimulator.Companions;
using CombatSimulator.Npcs;
using CombatSimulator.Simulation;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

// The round level of docs/PLUGIN_ARCHITECTURE.md §8.1 (stage B4a): the two REAL combat loops,
// NpcAiController and CombatCompanionManager (production sources, linked), stepped frame by frame over a
// flat fake world through their seams (Combat/CombatLoopSeams.cs). Recorded as golden/loop-*.txt so that
// stage B4b (one shared combat step) can be compared byte for byte. See README.md for what is faked.

// ------------------------------------------------------------------ scenarios

sealed record LoopFighter(string Name, bool Ally, NpcBehavior Kit, Vector3 At, int Hp, int MaxMp, int Mp, float Radius = 0.5f, bool Tank = false, bool Seizer = false);

sealed record LoopCast(Vector3 PlayerAt, bool PlayerAlive, List<LoopFighter> Fighters);

/// <param name="AllyLevel">The allies' level factor (their HP is the player's 60000 times this).</param>
/// <param name="RecordRuleLog">
/// Also record the battle rules' own summary line (<c>Rescue:</c>, timed by the simulation clock) as <c>G</c>
/// lines. Off for the scenarios recorded before stage B5, whose files do not have them.
/// </param>
sealed record LoopScenario(string Name, string Description, Settings Settings, IBattleRuleSettings Rules, Func<LoopCast> Build,
    float AllyLevel = LoopScenarios.AllyLevelFactor, bool RecordRuleLog = false);

/// <summary>The battle-rule switches (IBattleRuleSettings), fixed per scenario.</summary>
sealed record RuleSettings(WarFallenRule FallenRule, bool Retreat) : IBattleRuleSettings
{
    public bool Hostages => false;
    public uint CaptiveEmoteId => 19;
    public bool ShowResultBanner => true;
    public bool EnableRagdoll => true;
}

/// <summary>
/// The battle-rule switches changed during the battle, as the window changes them between two frames: from
/// each entry's time on, its switches hold (<see cref="At"/> is called at the start of every frame).
/// </summary>
sealed class ScheduledRules((float From, WarFallenRule FallenRule, bool Retreat)[] schedule) : IBattleRuleSettings
{
    public WarFallenRule FallenRule { get; private set; }
    public bool Retreat { get; private set; }
    public bool Hostages => false;
    public uint CaptiveEmoteId => 19;
    public bool ShowResultBanner => true;
    public bool EnableRagdoll => true;

    /// <summary>The switches at this simulation time; true when they changed.</summary>
    public bool At(float time)
    {
        var (_, rule, retreat) = schedule[0];
        foreach (var entry in schedule)
            if (time >= entry.From)
                (_, rule, retreat) = entry;
        var changed = rule != FallenRule || retreat != Retreat;
        FallenRule = rule;
        Retreat = retreat;
        return changed;
    }
}

static class LoopScenarios
{
    // Allies join as the NPC spawner's allies do (CombatCompanionManager.AdoptNpc): their HP is the
    // player's (60000) times this level factor.
    public const float AllyLevelFactor = 0.5f;

    // Sensing visible players walks the game's object table: off, as there is none.
    static readonly Settings Base = new() { SensePartyMembers = false };

    public static readonly LoopScenario[] All =
    {
        new("field-8v8-melee", "the real loops: 8 allies (2 tanks) + the player vs 8 melee enemies, Stay down; defaults (NpcScale 0.99, pace 1.6, enhanced tactics)",
            Base, new RuleSettings(WarFallenRule.StayDown, false), () =>
            {
                var list = new List<LoopFighter>();
                for (var i = 0; i < 8; i++)
                    list.Add(new($"A{i + 1}", true, Kits.Melee(), new Vector3((i - 3.5f) * 2.5f, 0f, (i % 3) * 0.7f), 0, 10000, 10000, Tank: i < 2));
                for (var i = 0; i < 8; i++)
                    list.Add(new($"E{i + 1}", false, Kits.Melee(), new Vector3((i - 3.5f) * 2.5f, 0f, 24f + (i % 2) * 1.1f), 25000, 5000, 5000,
                        Radius: i % 4 == 3 ? 1.6f : 0.5f, Tank: i == 0));
                return new LoopCast(new Vector3(0f, 0f, -4f), true, list);
            }),
        new("mixed-ranged-magic", "the real loops: 6 allies (melee, ranged, magic) + the player vs 8 mixed enemies with low MP; range scale 1.25, skill rate limit 1/s",
            Base with { NpcAttackRangeScale = 1.25f, NpcSkillRateLimit = 1f }, new RuleSettings(WarFallenRule.StayDown, false), () =>
            {
                var list = new List<LoopFighter>();
                NpcBehavior[] allyKits = { Kits.Melee(), Kits.Melee(), Kits.Ranged(), Kits.Ranged(), Kits.Magic(), Kits.Magic() };
                for (var i = 0; i < allyKits.Length; i++)
                    list.Add(new($"A{i + 1}", true, allyKits[i], new Vector3((i - 2.5f) * 3f, 0f, i >= 2 ? -3f : 0f), 0, 10000, i >= 4 ? 3000 : 10000, Tank: i == 0));
                NpcBehavior[] enemyKits = { Kits.Melee(), Kits.Melee(), Kits.Melee(false), Kits.Ranged(), Kits.Ranged(), Kits.Magic(), Kits.Magic(), Kits.Magic() };
                for (var i = 0; i < enemyKits.Length; i++)
                    list.Add(new($"E{i + 1}", false, enemyKits[i], new Vector3((i - 3.5f) * 2.8f, 0f, 30f + (i >= 3 ? 5f : 0f)), 22000, 5000, i >= 5 ? 1500 : 5000,
                        Radius: i == 2 ? 2.4f : 0.5f));
                return new LoopCast(new Vector3(0f, 0f, -6f), true, list);
            }),
        new("guard-body", "the real loops: Warring States with retreat, the lord's body at the origin; 7 retainers (the nearest to the post prays), 8 enemies, 4 going for the body",
            Base, new RuleSettings(WarFallenRule.WarringStates, true), () =>
            {
                var list = new List<LoopFighter> { new("A1", true, Kits.Melee(false), new Vector3(0f, 0f, 1.3f), 0, 10000, 10000) };
                for (var i = 1; i < 7; i++)
                {
                    var angle = i * MathF.PI / 3f;
                    list.Add(new($"A{i + 1}", true, i % 3 == 0 ? Kits.Ranged() : Kits.Melee(), new Vector3(MathF.Sin(angle) * 3f, 0f, MathF.Cos(angle) * 3f), 0, 10000, 10000, Tank: i == 1));
                }
                for (var i = 0; i < 8; i++)
                    list.Add(new($"E{i + 1}", false, i % 4 == 3 ? Kits.Ranged() : Kits.Melee(), new Vector3((i - 3.5f) * 3f, 0f, 22f + (i % 3)), 25000, 5000, 5000, Seizer: i % 2 == 0));
                return new LoopCast(Vector3.Zero, false, list);
            }),
        // Stage B5: the rules the scenarios above do not reach, recorded from the code before the rules became parts.
        new("rescue", "the real loops: Spring and Autumn, the player fallen; 8 allies (2 tanks, 2 ranged, level 0.5) vs 7 enemies: triage, raising the lord and allies, rescuers falling",
            Base, new RuleSettings(WarFallenRule.SpringAndAutumn, false), () =>
            {
                var list = new List<LoopFighter>();
                for (var i = 0; i < 8; i++)
                    list.Add(new($"A{i + 1}", true, i % 4 == 3 ? Kits.Ranged() : Kits.Melee(), new Vector3((i - 3.5f) * 2.2f, 0f, (i % 3) * 0.8f), 0, 10000, 10000, Tank: i < 2));
                for (var i = 0; i < 7; i++)
                    list.Add(new($"E{i + 1}", false, i % 4 == 3 ? Kits.Ranged() : Kits.Melee(), new Vector3((i - 3.5f) * 2.5f, 0f, 16f + (i % 2) * 1.1f), 40000, 5000, 5000,
                        Radius: i % 4 == 1 ? 1.4f : 0.5f));
                return new LoopCast(new Vector3(1f, 0f, -3f), false, list);
            }, AllyLevel: 0.5f, RecordRuleLog: true),
        new("lord-captured", "the real loops: Warring States without retreat, the lord fallen: every retainer taken captive at once, the enemies on the captives",
            Base, new RuleSettings(WarFallenRule.WarringStates, false), () =>
            {
                var list = new List<LoopFighter>();
                for (var i = 0; i < 5; i++)
                    list.Add(new($"A{i + 1}", true, Kits.Melee(), new Vector3((i - 2f) * 2.5f, 0f, 1f), 0, 10000, 10000, Tank: i == 0));
                for (var i = 0; i < 6; i++)
                    list.Add(new($"E{i + 1}", false, i % 3 == 2 ? Kits.Ranged() : Kits.Melee(), new Vector3((i - 2.5f) * 2.5f, 0f, 14f), 25000, 5000, 5000));
                return new LoopCast(Vector3.Zero, false, list);
            }, RecordRuleLog: true),
        new("rule-switch", "the real loops: the rule changed mid-battle, the lord fallen: Spring and Autumn, at 6 s Warring States with retreat, at 13 s retreat off, at 17 s Spring and Autumn, at 23 s Warring States with retreat",
            Base, new ScheduledRules(new[]
            {
                (0f, WarFallenRule.SpringAndAutumn, false), (6f, WarFallenRule.WarringStates, true), (13f, WarFallenRule.WarringStates, false),
                (17f, WarFallenRule.SpringAndAutumn, false), (23f, WarFallenRule.WarringStates, true),
            }), () =>
            {
                var list = new List<LoopFighter> { new("A1", true, Kits.Melee(false), new Vector3(0f, 0f, 1.6f), 0, 10000, 10000) };
                for (var i = 1; i < 7; i++)
                {
                    var angle = i * MathF.PI / 3f;
                    list.Add(new($"A{i + 1}", true, i % 3 == 0 ? Kits.Ranged() : Kits.Melee(), new Vector3(MathF.Sin(angle) * 3.5f, 0f, MathF.Cos(angle) * 3.5f), 0, 10000, 10000, Tank: i == 1));
                }
                for (var i = 0; i < 6; i++)
                    list.Add(new($"E{i + 1}", false, i % 4 == 3 ? Kits.Ranged() : Kits.Melee(), new Vector3((i - 3.5f) * 3f, 0f, 18f + (i % 3)), 30000, 5000, 5000, Seizer: i % 2 == 0));
                return new LoopCast(Vector3.Zero, false, list);
            }, AllyLevel: 1f, RecordRuleLog: true),
    };
}

// ------------------------------------------------------------------ the fake world

/// <summary>A game object of the flat world: what the seams read and write.</summary>
sealed class Body
{
    public string Name = "";
    public nint Address;
    public uint EntityId;
    public Vector3 Position;
    public float Rotation;
    public float Radius = 0.5f;
    public uint TargetObjectId;
}

/// <summary>Collects what the run did, frame by frame; the fakes report into it.</summary>
sealed class LoopLog
{
    public readonly StringBuilder Out = new();
    public int Frame;
    // Every effectful call through the world seam, counted per frame (hashed into the frame line).
    public readonly int[] Calls = new int[(int)Call.Count];

    public void Line(string text) => Out.Append(text).Append('\n');
    public void Note(Call call) => Calls[(int)call]++;
}

enum Call
{
    SetTargetId, SetRotation, SetEntityId, SetNormalMode, AddApproach, RemoveApproach, ClearApproach, SetApproachPosition,
    SetApproachRotation, ApplyMoving, ApplyMovingTimeline, ApplyCombatIdle, ApplyActionLocked, ApplyDead, ClearMovement,
    SetBattleStance, ClearBattleStance, PlayDeathAnimation, ResetDeathAnimation, PlayEmote, HoldEmoteLoop, ResetEmote,
    TrackFootIk, ReportRootLift, CrowdDrive, CrowdObstacle, AreaMarker, Count,
}

/// <summary>
/// The flat world: bodies on a plane, read and written by address; the crowd walks each agent straight at its
/// goal at its speed (no navigation, no avoidance, no floor); the visual states only record their kind.
/// </summary>
sealed unsafe class FlatWorld : ICombatWorld, ICrowdAgents, ICastAreaMarkers
{
    private readonly Dictionary<nint, Body> bodies = new();
    private readonly LoopLog log;
    public Body Player = null!;

    public FlatWorld(LoopLog log) => this.log = log;

    public Body Add(Body body)
    {
        bodies.Add(body.Address, body);
        return body;
    }

    public Body At(nint address) => bodies[address];
    private Body Of(Combatant c) => bodies[c.Address];

    private sealed class View(Body body) : IGameObjectView
    {
        public nint Address => body.Address;
        public uint EntityId => body.EntityId;
        public Vector3 Position => body.Position;
        public float Rotation => body.Rotation;
    }

    public IGameObjectView? LocalPlayer => new View(Player);
    // The player has nothing selected.
    public IGameObjectView? PlayerTarget => null;

    public Vector3 PositionOf(Combatant c) => Of(c).Position;
    public Vector3 ReferencePositionOf(Combatant c) => throw new InvalidOperationException("no managed object references in the flat world");
    public float RotationOf(Combatant c) => Of(c).Rotation;
    // Combatant.HitboxRadius: the object's radius, 0.5 when there is none or it is not set.
    public float HitboxRadiusOf(Combatant c)
    {
        if (c.BattleChara == null) return 0.5f;
        var r = Of(c).Radius;
        return r > 0.01f ? r : 0.5f;
    }
    public float HitboxRadiusAt(nint address) => bodies[address].Radius;
    public uint EntityIdOf(Combatant c) => Of(c).EntityId;
    public GameObjectId GameObjectIdAt(nint address) => new() { ObjectId = bodies[address].EntityId };
    public uint TargetObjectIdOf(Combatant c) => Of(c).TargetObjectId;

    public void SetTargetId(Combatant c, GameObjectId id) { log.Note(Call.SetTargetId); Of(c).TargetObjectId = id.ObjectId; }
    public void SetRotation(Combatant c, float rotation) { log.Note(Call.SetRotation); Of(c).Rotation = rotation; }
    public void SetEntityId(Combatant c, uint entityId) { log.Note(Call.SetEntityId); Of(c).EntityId = entityId; }
    public void SetNormalMode(Combatant c) => log.Note(Call.SetNormalMode);

    public void AddApproachNpc(nint address) => log.Note(Call.AddApproach);
    public void RemoveApproachNpc(nint address) => log.Note(Call.RemoveApproach);
    public void ClearApproachNpcs() => log.Note(Call.ClearApproach);
    public void ClearApproachNpcsExcept(Func<nint, bool> keep) => log.Note(Call.ClearApproach);
    public void SetApproachPosition(Combatant c, float x, float y, float z) { log.Note(Call.SetApproachPosition); Of(c).Position = new Vector3(x, y, z); }
    public void SetApproachRotation(Combatant c, float rotation) { log.Note(Call.SetApproachRotation); Of(c).Rotation = rotation; }

    // ActorVisualStateController keeps only this bookkeeping on the plugin's side (Kind, the reassert timer);
    // the rest it writes to the game object.
    public void ApplyMoving(Combatant c, float deltaTime) { log.Note(Call.ApplyMoving); c.VisualState.Kind = ActorVisualStateKind.Moving; }
    public void ApplyMoving(Combatant c, float deltaTime, ushort timelineId) { log.Note(Call.ApplyMovingTimeline); c.VisualState.Kind = ActorVisualStateKind.Moving; }
    public void ApplyCombatIdle(Combatant c) { log.Note(Call.ApplyCombatIdle); c.VisualState.Kind = ActorVisualStateKind.CombatIdle; }
    public void ApplyActionLocked(Combatant c) { log.Note(Call.ApplyActionLocked); c.VisualState.Kind = ActorVisualStateKind.ActionLocked; }
    public void ApplyDead(Combatant c) { log.Note(Call.ApplyDead); c.VisualState.Kind = ActorVisualStateKind.Dead; }
    public void ClearMovement(nint address, ActorVisualState visual) => log.Note(Call.ClearMovement);

    public void SetBattleStance(SimulatedNpc npc) => log.Note(Call.SetBattleStance);
    public void ClearBattleStance(SimulatedNpc npc) => log.Note(Call.ClearBattleStance);
    public void PlayDeathAnimation(SimulatedNpc npc) { log.Note(Call.PlayDeathAnimation); log.Line($"V {log.Frame} {npc.Name} death animation"); }
    public void ResetDeathAnimation(Combatant c) => log.Note(Call.ResetDeathAnimation);
    public bool PlayEmoteOn(Combatant c, uint emoteId) { log.Note(Call.PlayEmote); log.Line($"V {log.Frame} {c.Name} emote {emoteId}"); return true; }
    public ushort EmoteLoopTimeline(uint emoteId) => (ushort)(1000 + emoteId);
    public void HoldEmoteLoop(Combatant c, ushort loop) => log.Note(Call.HoldEmoteLoop);
    public void ResetEmote(Combatant c) { log.Note(Call.ResetEmote); log.Line($"V {log.Frame} {c.Name} emote reset"); }

    // No corpses to stand on.
    public void TrackFootIk(nint address, float deltaTime) => log.Note(Call.TrackFootIk);
    public float FootIkPelvisOffset(nint address) => 0f;
    public void ReportFootIkRootLift(nint address, float lift) => log.Note(Call.ReportRootLift);

    public int Add(uint sourceSimId, uint actionId, in TelegraphShape shape, float duration, Func<bool>? stillValid = null)
    {
        log.Note(Call.AreaMarker);
        return 0;
    }

    // ---- the crowd: every agent walks straight at its goal, the frame after it is set ----

    public sealed class Agent
    {
        public Vector3 Position, Velocity;
        public Vector3? Goal;
        public float Speed, Radius;
        public int Frame;
        public bool Obstacle;
    }

    public readonly Dictionary<nint, Agent> Agents = new();
    private int crowdFrame;

    public bool Ready => true;

    public bool Drive(nint address, Vector3 position, float radius, float speed, Vector3? goal)
    {
        log.Note(Call.CrowdDrive);
        if (address == nint.Zero)
            return false;
        if (!Agents.TryGetValue(address, out var agent))
            Agents[address] = agent = new Agent { Position = position };
        agent.Speed = speed;
        agent.Radius = radius;
        agent.Frame = crowdFrame;
        agent.Goal = goal;
        return true;
    }

    public bool TrySnapToFloor(Vector3 point, out Vector3 floor)
    {
        floor = point;
        return true;
    }

    public void SetObstacle(nint address, Vector3 position, float radius)
    {
        log.Note(Call.CrowdObstacle);
        Agents.Remove(address);
        Agents[address] = new Agent { Position = position, Radius = radius, Frame = crowdFrame, Obstacle = true };
    }

    public bool TryGetMotion(nint address, out Vector3 position, out Vector3 velocity)
    {
        position = velocity = default;
        if (!Agents.TryGetValue(address, out var agent))
            return false;
        position = agent.Position;
        velocity = agent.Velocity;
        return true;
    }

    /// <summary>One step of the crowd (CrowdMover.Update): those not driven since the last step leave it.</summary>
    public void Update(float deltaTime)
    {
        foreach (var address in Agents.Where(a => a.Value.Frame != crowdFrame).Select(a => a.Key).ToList())
            Agents.Remove(address);
        var dt = MathF.Min(deltaTime, 0.1f);
        foreach (var agent in Agents.Values)
        {
            agent.Velocity = Vector3.Zero;
            if (agent.Obstacle || agent.Goal is not { } goal || dt <= 0f)
                continue;
            var to = new Vector3(goal.X - agent.Position.X, 0f, goal.Z - agent.Position.Z);
            var d = to.Length();
            if (d <= 1e-4f)
                continue;
            var move = to * (MathF.Min(agent.Speed * dt, d) / d);
            agent.Position += move;
            agent.Velocity = move / dt;
        }
        crowdFrame++;
    }
}

/// <summary>
/// The engine as the loops see it (ICombatLoopEngine): the real SimulationState and BattleRules; the
/// enemies' target from the companion manager (as Plugin.cs wires ResolveNpcTarget); positions from the flat
/// world (as CombatEngine.GetEntityPosition reads them); action data only for the MP prices of the kits.
/// </summary>
sealed class FakeEngine : ICombatLoopEngine
{
    private readonly FlatWorld world;
    private readonly LoopLog log;
    private readonly Dictionary<uint, ActionData> actions = new();
    public List<SimulatedNpc> Enemies = new();

    public FakeEngine(FlatWorld world, LoopLog log, IBattleRuleSettings rules)
    {
        this.world = world;
        this.log = log;
        Rules = new BattleRules(rules, () => IsActive, () => State.PlayerState.IsAlive);
        foreach (var (id, price) in Kits.Prices)
            actions[id] = new ActionData { ActionId = id, Name = $"s{id}", MpCost = price };
    }

    public SimulationState State { get; } = new();
    public bool IsActive => State.IsActive;
    public BattleRules Rules { get; }
    public Action? OnSimulationStarted { get; set; }
    public Action? OnSimulationReset { get; set; }
    public Func<SimulatedNpc, SimulatedEntityState?>? ResolveNpcTarget { get; set; }
    public Func<uint, nint?>? ResolveExternalEntityAddress { get; set; }
    public Func<bool>? HasLivingCompanions { get; set; }
    public bool IsEnemyInitiationSuppressed => false;

    public SimulatedEntityState GetNpcTarget(SimulatedNpc npc) => ResolveNpcTarget?.Invoke(npc) ?? State.PlayerState;

    public Vector3 GetSimulatedEntityPosition(SimulatedEntityState entity)
    {
        if (entity.IsPlayer)
            return world.Player.Position;
        if (entity.IsCompanion)
        {
            var addr = ResolveExternalEntityAddress?.Invoke(entity.EntityId);
            if (addr.HasValue && addr.Value != nint.Zero)
                return world.At(addr.Value).Position;
        }
        else
        {
            foreach (var npc in Enemies)
                if (npc.SimulatedEntityId == entity.EntityId && npc.Address != nint.Zero)
                    return world.At(npc.Address).Position;
        }
        return Vector3.Zero;
    }

    public ActionData? GetActionData(uint actionId) => actions.GetValueOrDefault(actionId);
    public void AddLogEntry(string message, CombatLogType type) => log.Line($"L {log.Frame} {type} {message}");
    public void EnsurePlayerInitialized() { }
    public void RegisterCompanionEntity(CombatCompanion companion) => State.Entities[companion.SimulatedEntityId] = companion.State;
    public void UnregisterNpcEntity(uint entityId) => State.Entities.Remove(entityId);
    public void TriggerEnemyVictoryIfPartyDefeated() => log.Line($"L {log.Frame} victory check");
    // The state changes as CombatEngine makes them (stage B5: the rescue and lord scenarios reach them); no
    // emotes, executions or movement block.
    public void TakeCaptive(SimulatedEntityState friend)
    {
        log.Line($"L {log.Frame} take captive {friend.Name}");
        if (friend.IsCaptive || !friend.IsAlive)
            return;
        friend.IsCaptive = true;
        friend.CurrentHp = Math.Min(friend.CurrentHp, 1);
        friend.IsCasting = false;
        friend.CastTimeElapsed = 0;
        friend.CastActionId = 0;
    }

    public void ReleaseCaptive(SimulatedEntityState friend, float hpShare)
    {
        log.Line($"L {log.Frame} release captive {friend.Name}");
        if (!friend.IsCaptive)
            return;
        friend.IsCaptive = false;
        friend.CurrentHp = Math.Clamp((int)MathF.Ceiling(friend.MaxHp * hpShare), 1, Math.Max(1, friend.MaxHp));
    }

    public void RevivePlayerInPlace(float hpShare, bool refillMp)
    {
        log.Line($"L {log.Frame} revive player");
        var ps = State.PlayerState;
        ps.IsCaptive = false;
        ps.Executed = false;
        ps.IncomingDamageScale = 1f;
        ps.CurrentHp = Math.Clamp((int)MathF.Ceiling(ps.MaxHp * hpShare), 1, Math.Max(1, ps.MaxHp));
        ps.CurrentMp = refillMp ? ps.MaxMp : 0;
        ps.AnimationLock = 0;
    }

    public void PlaySupportVfx(SimulatedEntityState source, SimulatedEntityState target, uint actionId)
        => log.Line($"L {log.Frame} support vfx {actionId} {source.Name} -> {target.Name}");

    /// <summary>CombatEngine.Tick, as far as the loops see it: the simulation clock.</summary>
    public void Tick(float deltaTime) => State.SimulationTime += deltaTime;
}

/// <summary>
/// Where attacks land (IAttackSink). An enemy's attack is a telegraph that lands its full time later
/// (TelegraphedAttackExecutor's windup, here a fixed <see cref="Windup"/>; the enemy is busy until then);
/// a companion's lands at once, as CombatEngine.ProcessCompanionAction lands it. Damage is the production
/// DamageCalculator's, threat the production TargetingService's (CombatEngine.OnHit -> RegisterHit).
/// </summary>
sealed class FakeAttacks : IAttackSink
{
    public const float Windup = 0.8f;
    private readonly FakeEngine engine;
    private readonly DamageCalculator damage;
    private readonly LoopLog log;
    private readonly List<(SimulatedNpc Source, NpcAttackRequest Request, float LandAt)> pending = new();
    public CombatCompanionManager Companions = null!;

    public FakeAttacks(FakeEngine engine, DamageCalculator damage, LoopLog log)
    {
        this.engine = engine;
        this.damage = damage;
        this.log = log;
    }

    private static string What(uint actionId, bool auto) => auto ? "auto" : $"skill {actionId}";
    private string NameOf(ulong id) => engine.State.GetEntity(id)?.Name is { Length: > 0 } n ? n : "-";

    public bool IsBusy(uint enemyId) => pending.Exists(p => p.Source.SimulatedEntityId == enemyId);

    public bool ExecuteEnemyAttack(SimulatedNpc npc, NpcAttackRequest request)
    {
        pending.Add((npc, request, engine.State.SimulationTime + request.CastTime + Windup));
        log.Line($"A {log.Frame} {npc.Name} {What(request.ActionId, request.IsAutoAttack)} -> {NameOf(request.TargetId)} mp {npc.State.CurrentMp}");
        return true;
    }

    public TelegraphShape SnapshotShape(SimulatedNpc npc, NpcAttackRequest request, SimulatedEntityState target) => default;

    /// <summary>The telegraphs whose time is up land (TelegraphSystem.Tick), in the order they began.</summary>
    public void Tick()
    {
        var now = engine.State.SimulationTime;
        for (var i = 0; i < pending.Count;)
        {
            var (source, request, landAt) = pending[i];
            if (landAt > now)
            {
                i++;
                continue;
            }
            pending.RemoveAt(i);
            var target = engine.State.GetEntity(request.TargetId);
            if (target == null || !target.IsAlive || !source.State.IsAlive)
            {
                log.Line($"H {log.Frame} {source.Name} -> {NameOf(request.TargetId)} lapsed");
                continue;
            }
            var dmg = damage.CalculateNpcAutoAttack(source.State, target, request.Potency).Damage;
            target.CurrentHp = Math.Max(0, target.CurrentHp - dmg);
            Companions.RegisterHit(source.State, target, dmg);
            log.Line($"H {log.Frame} {source.Name} -> {target.Name} dmg {dmg} hp {target.CurrentHp}");
        }
    }

    public SimulatedActionResult ProcessCompanionAction(CombatCompanion companion, SimulatedNpc target, uint actionId,
        int potency = 0, NpcAttackStyle attackStyle = NpcAttackStyle.Auto, float radius = 0)
    {
        var result = new SimulatedActionResult { ActionId = actionId, SourceId = companion.SimulatedEntityId, TargetId = target.SimulatedEntityId };
        if (!companion.State.IsAlive || !target.State.IsAlive)
        {
            result.FailReason = "Invalid target or source dead.";
            log.Line($"A {log.Frame} {companion.Name} {What(actionId, actionId is 7 or 8)} -> {target.Name} failed");
            return result;
        }
        var dmg = potency > 0
            ? damage.CalculateNpcAutoAttack(companion.State, target.State, potency)
            : damage.CalculateNpcAutoAttack(companion.State, target.State);
        target.State.CurrentHp = Math.Max(0, target.State.CurrentHp - dmg.Damage);
        result.Success = true;
        result.Damage = dmg.Damage;
        log.Line($"A {log.Frame} {companion.Name} {What(actionId, actionId is 7 or 8)} -> {target.Name} dmg {dmg.Damage} hp {target.State.CurrentHp} mp {companion.State.CurrentMp}");
        return result;
    }
}

/// <summary>IPluginLog that drops everything (the loops' summary lines are wall-clock timed, not part of a run).</summary>
public class NullLogProxy : DispatchProxy
{
    /// <summary>
    /// Where the battle rules' summary lines go (those starting <c>Rescue:</c>, timed by the simulation clock),
    /// or null: dropped with the rest.
    /// </summary>
    public Action<string>? RuleLines;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (RuleLines != null && targetMethod?.Name == "Info" && args is { Length: > 0 } && args[0] is string text &&
            text.StartsWith("Rescue:", StringComparison.Ordinal))
            RuleLines(text);
        return targetMethod != null && targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void)
            ? Activator.CreateInstance(targetMethod.ReturnType)
            : null;
    }
}

// ------------------------------------------------------------------ the run

/// <summary>
/// One run, in the plugin's frame order (Plugin.OnFrameworkUpdate, docs/PLUGIN_ARCHITECTURE.md §6.4):
/// CombatCompanionManager.Tick; telegraphs land; the crowd steps; CombatEngine.Tick (the clock); the war
/// (seizers of the lord's body); NpcAiController.Tick. Seeds as §8.1: NpcAiController.RandomSource
/// Random(s), CombatCompanionManager.RandomSource Random(s+1), AggroSeedSource Random(s+2), the
/// DamageCalculator Random(s+3).
/// </summary>
sealed unsafe class LoopReplay
{
    private const float Duration = 30f;
    private const float SnapshotEvery = 2f;

    private readonly LoopScenario scenario;
    private readonly int seed;
    private readonly (string Name, Func<int, float> Dt) steps;
    private readonly LoopLog log = new();
    private readonly FlatWorld world;
    private readonly FakeEngine engine;
    private readonly FakeAttacks attacks;
    private readonly CombatCompanionManager companions;
    private readonly NpcAiController enemiesAi;
    private readonly TargetingService targeting;
    private readonly List<SimulatedNpc> enemies = new();
    private readonly List<SimulatedNpc> seizers = new();
    private readonly Dictionary<uint, string> names = new();
    private readonly Dictionary<string, string> last = new();

    public LoopReplay(LoopScenario scenario, int seed, (string Name, Func<int, float> Dt) steps)
    {
        this.scenario = scenario;
        this.seed = seed;
        this.steps = steps;
        var settings = scenario.Settings;
        var cast = scenario.Build();
        var pluginLog = DispatchProxy.Create<IPluginLog, NullLogProxy>();
        if (scenario.RecordRuleLog)
            ((NullLogProxy)(object)pluginLog).RuleLines = text => log.Line($"G {log.Frame} {text}");
        (scenario.Rules as ScheduledRules)?.At(0f);

        world = new FlatWorld(log);
        engine = new FakeEngine(world, log, scenario.Rules);
        var damage = new DamageCalculator(new Random(seed + 3)) { IsActionMode = () => settings.ActionMode, PlayerMaxHp = () => 60000f };
        attacks = new FakeAttacks(engine, damage, log);

        // The player: a body and the engine's player state.
        const uint playerId = 0x10000001;
        world.Player = world.Add(new Body { Name = "P", Address = 0x100000, EntityId = playerId, Position = cast.PlayerAt });
        engine.State.PlayerState = new SimulatedEntityState
        {
            EntityId = playerId, Name = "P", IsPlayer = true, CurrentHp = cast.PlayerAlive ? 60000 : 0, MaxHp = 60000, MaxMp = 10000, CurrentMp = 10000,
        };
        names[playerId] = "P";

        // As Plugin.cs builds and wires them (the manager first, then the enemy AI).
        companions = new CombatCompanionManager(null!, null!, settings, engine, world, attacks, pluginLog);
        engine.ResolveNpcTarget = companions.SelectEnemyTarget;
        engine.ResolveExternalEntityAddress = companions.ResolveAddress;
        engine.HasLivingCompanions = () => companions.HasLivingCompanions;
        attacks.Companions = companions;
        enemiesAi = new NpcAiController(engine, world, attacks, settings, pluginLog, null, attacks.IsBusy);
        enemiesAi.Crowd = world;
        companions.Crowd = world;
        enemiesAi.CollectPartyPositions = companions.CollectPartyPositions;
        enemiesAi.RandomSource = new Random(seed);
        companions.RandomSource = new Random(seed + 1);
        companions.AggroSeedSource = new Random(seed + 2);
        targeting = (TargetingService)typeof(CombatCompanionManager)
            .GetField("targeting", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(companions)!;

        // Enemies as the NPC spawner leaves them (client-controlled, spawned, idle), registered with the engine.
        var enemyIndex = 0;
        var allyIndex = 0;
        foreach (var f in cast.Fighters.Where(f => !f.Ally))
        {
            var id = 0xF0000001u + (uint)enemyIndex;
            var address = (nint)(0x200000 + enemyIndex++ * 0x1000);
            world.Add(new Body { Name = f.Name, Address = address, EntityId = id, Position = f.At, Rotation = MathF.PI, Radius = f.Radius });
            var npc = new SimulatedNpc
            {
                SimulatedEntityId = id, Name = f.Name, BattleChara = (BattleChara*)address, Behavior = Kits.Clone(f.Kit), SpawnPosition = f.At,
                IsSpawned = true, IsClientControlled = true,
                State = new SimulatedEntityState { EntityId = id, Name = f.Name, CurrentHp = f.Hp, MaxHp = f.Hp, MaxMp = f.MaxMp, CurrentMp = f.Mp, IsTank = f.Tank },
            };
            engine.State.Entities[id] = npc.State;
            enemies.Add(npc);
            if (f.Seizer) seizers.Add(npc);
            names[id] = f.Name;
        }
        engine.Enemies = enemies;

        // Allies as the NPC spawner hands them over (CombatCompanionManager.AdoptNpc).
        foreach (var f in cast.Fighters.Where(f => f.Ally))
        {
            var address = (nint)(0x300000 + allyIndex++ * 0x1000);
            world.Add(new Body { Name = f.Name, Address = address, Position = f.At, Radius = f.Radius });
            var npc = new SimulatedNpc
            {
                Name = f.Name, BattleChara = (BattleChara*)address, Behavior = Kits.Clone(f.Kit), SpawnPosition = f.At, IsSpawned = true, IsClientControlled = true,
                State = new SimulatedEntityState { Name = f.Name, MaxMp = f.MaxMp, CurrentMp = f.Mp, IsTank = f.Tank, LevelFactor = scenario.AllyLevel },
            };
            if (!companions.AdoptNpc(npc, new NpcSpawnRequest { DisplayName = f.Name }))
                throw new InvalidOperationException($"{f.Name} was not adopted");
            names[world.At(address).EntityId] = f.Name;
        }

        engine.State.IsActive = true;
        engine.OnSimulationStarted?.Invoke();
    }

    private string Name(uint id) => id == 0 ? "-" : names.GetValueOrDefault(id, id.ToString("X"));

    public string Run()
    {
        var o = log.Out;
        o.Append($"## seed {seed} (enemy AI {seed}, allies {seed + 1}, aggro {seed + 2}, damage {seed + 3}), dt {steps.Name}, {Duration:0} s\n");
        var nextSnapshot = 0f;
        var frame = 0;
        while (engine.State.SimulationTime < Duration &&
               companions.Companions.Any(c => c.State.IsAlive) && enemies.Any(e => e.State.IsAlive))
        {
            var dt = steps.Dt(frame);
            log.Frame = frame;
            Array.Clear(log.Calls);
            // The window changes a switch between two frames.
            if ((scenario.Rules as ScheduledRules)?.At(engine.State.SimulationTime) == true)
                log.Line($"L {log.Frame} rules {scenario.Rules.FallenRule} retreat {scenario.Rules.Retreat}");
            if (engine.State.SimulationTime >= nextSnapshot)
            {
                Snapshot();
                nextSnapshot += SnapshotEvery;
            }

            companions.Tick(dt, enemies);
            attacks.Tick();
            world.Update(dt);
            engine.Tick(dt);
            TickWar();
            enemiesAi.Tick(dt, enemies);

            Changes();
            FrameLine();
            frame++;
        }
        log.Frame = frame;
        Snapshot();
        o.Append($"end frame {frame} time {Fmt.H(engine.State.SimulationTime)} allies {companions.Companions.Count(c => c.State.IsAlive)} enemies {enemies.Count(e => e.State.IsAlive)}\n");
        return o.ToString();
    }

    /// <summary>
    /// The war's part of the frame (Plugin.TickWar -> Battle.CorpseSeizure), reduced to what the loops read:
    /// while the lord's body is guarded, the seizers go for it (SeizeGoal), held while a retainer stands within
    /// the contest radius (SeizeHeld), and the manager knows them (LordSeizers). No seizure progress or capture.
    /// </summary>
    private void TickWar()
    {
        if (seizers.Count == 0)
            return;
        companions.LordSeizers.Clear();
        var guarded = engine.Rules.LordBodyIsGuarded;
        var body = world.Player.Position;
        var held = guarded && companions.RetainersAtBody(body, CombatCompanionManager.LordContestRadius, out _, out _) > 0;
        foreach (var npc in seizers)
        {
            if (!guarded || !npc.State.IsAlive)
            {
                npc.SeizeGoal = null;
                npc.SeizeHeld = false;
                continue;
            }
            companions.LordSeizers.Add(npc.SimulatedEntityId);
            npc.SeizeGoal = body;
            npc.SeizeHeld = held;
        }
    }

    // ---- recording ----

    private IEnumerable<Combatant> Fighters()
    {
        foreach (var c in companions.Companions) yield return c;
        foreach (var e in enemies) yield return e;
    }

    private string TargetOf(Combatant c) => c is CombatCompanion a ? Name(a.CurrentTargetId) : Name(targeting.EnemyTargetOf(c.SimulatedEntityId));
    private static string StateOf(Combatant c) => c is CombatCompanion a ? a.AiState.ToString() : ((SimulatedNpc)c).AiState.ToString();

    /// <summary>What changed this frame: a target, the state, in reach, a death.</summary>
    private void Changes()
    {
        foreach (var c in Fighters())
        {
            var key = $"{TargetOf(c)}|{StateOf(c)}|{(c.InReach ? 1 : 0)}|{(c.State.IsAlive ? 1 : 0)}";
            last.TryGetValue(c.Name, out var before);
            if (before == key)
                continue;
            last[c.Name] = key;
            var gap = "";
            var target = c is CombatCompanion a ? a.CurrentTargetId : targeting.EnemyTargetOf(c.SimulatedEntityId);
            if (target != 0 && engine.State.GetEntity(target) is { } t)
            {
                var at = engine.GetSimulatedEntityPosition(t);
                var p = world.At(c.Address).Position;
                gap = $" dist {Fmt.H(CombatantMath.FlatDistance(p, at))}";
            }
            log.Line($"C {log.Frame} {c.Name} tgt {TargetOf(c)} state {StateOf(c)} in {(c.InReach ? 1 : 0)} alive {(c.State.IsAlive ? 1 : 0)}{gap}");
        }
    }

    /// <summary>One line a frame: a hash of everything every fighter and the crowd hold, and of the effectful calls made.</summary>
    private void FrameLine()
    {
        var h = new Fnv();
        foreach (var c in Fighters())
        {
            var b = world.At(c.Address);
            h.Add(c.SimulatedEntityId); h.Add(b.Position); h.Add(b.Rotation); h.Add(b.TargetObjectId);
            h.Add(c.State.CurrentHp); h.Add(c.State.CurrentMp); h.Add(c.State.AnimationLock); h.Add(c.State.IncomingDamageScale);
            h.Add(c.State.IsCasting); h.Add(c.State.CastTimeElapsed); h.Add(c.AutoAttackTimer); h.Add(c.EngageDelayTimer); h.Add(c.InReach);
            h.Add((int)c.VisualState.Kind);
            foreach (var s in c.Behavior.Skills) h.Add(s.CooldownRemaining);
            if (c is CombatCompanion a)
            {
                h.Add((int)a.AiState); h.Add(a.CurrentTargetId); h.Add(a.NextTargetSelectAt); h.Add(a.RecentDps); h.Add(a.ReturningToLord);
                h.Add(a.DeathAnimationPlayed);
            }
            else
            {
                var e = (SimulatedNpc)c;
                h.Add((int)e.AiState); h.Add(targeting.EnemyTargetOf(e.SimulatedEntityId)); h.Add(e.CrowdDriven); h.Add(e.WalkPace);
                h.Add(e.IsAttackingPlayer); h.Add(e.DeadTimer);
            }
            if (world.Agents.TryGetValue(c.Address, out var agent))
            {
                h.Add(agent.Position); h.Add(agent.Velocity); h.Add(agent.Speed); h.Add(agent.Radius);
                h.Add(agent.Goal.HasValue); if (agent.Goal is { } g) h.Add(g);
            }
            else
                h.Add(-1);
        }
        foreach (var n in log.Calls) h.Add(n);
        log.Line($"F {log.Frame} {h.Value:X16}");
    }

    private void Snapshot()
    {
        foreach (var c in Fighters())
        {
            if (!c.State.IsAlive) continue;
            var b = world.At(c.Address);
            log.Line($"S {log.Frame} {c.Name} {StateOf(c)} hp {c.State.CurrentHp} mp {c.State.CurrentMp} pos {Fmt.H(b.Position)} rot {Fmt.H(b.Rotation)} " +
                     $"in {(c.InReach ? 1 : 0)} tgt {TargetOf(c)} auto {Fmt.H(c.AutoAttackTimer)} lock {Fmt.H(c.State.AnimationLock)} " +
                     $"cd {string.Join(",", c.Behavior.Skills.Select(s => Fmt.H(s.CooldownRemaining)))} look {c.VisualState.Kind}");
        }
    }

    private sealed class Fnv
    {
        public ulong Value = 14695981039346656037UL;
        private void Byte(byte b) { Value ^= b; Value *= 1099511628211UL; }
        public void Add(uint v) { for (var i = 0; i < 4; i++) Byte((byte)(v >> (8 * i))); }
        public void Add(int v) => Add((uint)v);
        public void Add(bool v) => Byte(v ? (byte)1 : (byte)0);
        public void Add(float v) => Add((uint)BitConverter.SingleToInt32Bits(float.IsNaN(v) ? float.NaN : v));
        public void Add(Vector3 v) { Add(v.X); Add(v.Y); Add(v.Z); }
    }
}
