using CombatSimulator.Npcs;
using CombatSimulator.Simulation;

// Offline fight simulator for the combat numbers. It drives the production DamageCalculator,
// CombatBudgetModel, KitBudget, VirtualActionModel and NpcBehavior kits through whole fights, with the
// player and enemy action economies the engine uses (constants below mirror CombatEngine and
// NpcAiController), and checks the outcomes against the model's contract.

static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }

const float Dt = 1f / 60f;
const int PlayerMaxMp = 10000;
const float PlayerMpRegen = 67f;          // CombatEngine: natural regen per second
const int BasicAttackMpRestore = 300;     // CombatEngine: per landed basic attack in Action Mode
const int BasicAttackPotency = 120;       // Configuration.LightAttackPotency
const float BasicAttackInterval = 0.4f;   // Configuration.LightSwingInterval
const float ClassicAutoDelay = 2.56f;     // CombatEngine: player auto-attack
const float ClassicGcd = 2.5f;
const float AnimationLock = 0.6f;
const int EnemyMaxMp = 5000;

// --- skills, priced by the production VirtualActionModel ---
static ActionData Skill(uint id, float cast, float recast, float range, float animation = 0f)
{
    var data = new ActionData { ActionId = id, CastTime = cast, RecastTime = recast, Range = range, AnimationDuration = animation, Shape = AoeShape.Single };
    VirtualActionModel.Apply(data, BasicAttackPotency);
    return data;
}
var meleeSkill = Skill(1001, 0f, 2.5f, 3f);          // an ordinary weaponskill
var heavySkill = Skill(1002, 0f, 30f, 3f, 2.0f);     // a long, heavy strike
var spell = Skill(1003, 2.0f, 2.5f, 25f);            // a cast-time spell
var prices = new Dictionary<uint, int>
{
    [31] = 4000, [141] = 5000, [144] = 3000, [41] = 4500, [42] = 8000,   // NpcBehavior.Create kits
    [2001] = 2000, [2002] = 4000, [2003] = 6000, [2004] = 8000,          // a humanoid job kit
    [3001] = 2500, [3002] = 3500,                                        // a monster kit
};
int Price(uint action) => prices.TryGetValue(action, out var p) ? p : 0;

NpcBehavior AutoOnly() => new() { Skills = new() };
NpcBehavior JobKit() => new()
{
    Skills = new()
    {
        new NpcSkill { ActionId = 2001, Potency = 342, Cooldown = 2.5f },
        new NpcSkill { ActionId = 2002, Potency = 574, Cooldown = 15f },
        new NpcSkill { ActionId = 2003, Potency = 860, Cooldown = 30f },
        new NpcSkill { ActionId = 2004, Potency = 1200, Cooldown = 60f },
    },
};
NpcBehavior MonsterKit() => new()
{
    Skills = new()
    {
        new NpcSkill { ActionId = 3001, Potency = 180, Cooldown = 8f },
        new NpcSkill { ActionId = 3002, Potency = 300, Cooldown = 20f },
    },
};
var kits = new (string Name, Func<NpcBehavior> Make)[]
{
    ("auto-attack only", AutoOnly),
    ("basic melee", () => NpcBehavior.Create(NpcBehaviorType.BasicMelee)),
    ("basic ranged", () => NpcBehavior.Create(NpcBehaviorType.BasicRanged)),
    ("boss", () => NpcBehavior.Create(NpcBehaviorType.Boss)),
    ("humanoid job kit", JobKit),
    ("monster kit", MonsterKit),
};

// --- one fight ---
Outcome Fight(Setup setup)
{
    var random = new Random(setup.Seed);
    var calculator = new DamageCalculator(random) { IsActionMode = () => setup.ActionMode };
    var pace = setup.ActionMode ? CombatBudgetModel.ActionModePace : 1f;

    var player = new SimulatedEntityState { IsPlayer = true, MaxHp = setup.PlayerMaxHp, CurrentHp = setup.PlayerMaxHp, MaxMp = PlayerMaxMp, CurrentMp = PlayerMaxMp };
    var enemies = new List<Enemy>();
    foreach (var (make, tier) in setup.Enemies)
    {
        var behavior = make();
        var state = new SimulatedEntityState
        {
            Tier = tier, MaxMp = EnemyMaxMp, CurrentMp = EnemyMaxMp,
            MaxHp = CombatBudgetModel.EnemyMaxHp(setup.PlayerMaxHp, tier, behavior.FightsAtRange, setup.HpMultiplier * setup.LevelFactor),
            LevelFactor = setup.LevelFactor,
        };
        state.CurrentHp = state.MaxHp;
        KitBudget.Measure(state, behavior, Price, usesMp: true);
        enemies.Add(new Enemy(behavior, state) { AutoTimer = (float)random.NextDouble() * behavior.AutoAttackDelay / pace });
    }
    var companions = new List<Enemy>();
    for (var i = 0; i < setup.Companions; i++)
    {
        var behavior = JobKit();
        var state = new SimulatedEntityState { IsCompanion = true, MaxHp = setup.PlayerMaxHp, CurrentHp = setup.PlayerMaxHp };
        KitBudget.Measure(state, behavior, Price, usesMp: false);
        companions.Add(new Enemy(behavior, state));
    }

    var outcome = new Outcome();
    float playerMp = PlayerMaxMp, mpCarry = 0f, swing = 0f, gcd = 0f, auto = ClassicAutoDelay, busy = 0f, time = 0f;
    var pendingSkill = (ActionData?)null;

    void PlayerHit(ActionData? action, int potency)
    {
        var target = enemies.FirstOrDefault(e => e.State.IsAlive);
        if (target == null) return;
        var damage = action != null
            ? calculator.Calculate(player, target.State, action, false, setup.Crits, setup.Crits, setup.DamageMultiplier).Damage
            : calculator.CalculateNpcAutoAttack(player, target.State, potency).Damage;
        outcome.PlayerPotency += action?.Potency ?? potency;
        outcome.LargestPlayerHit = Math.Max(outcome.LargestPlayerHit, damage);
        target.State.CurrentHp = Math.Max(0, target.State.CurrentHp - damage);
        if (!target.State.IsAlive) outcome.KillTimes.Add(time);
    }

    while (time < setup.MaxSeconds)
    {
        time += Dt;

        // --- player ---
        if (setup.PlayerAttacks && player.IsAlive)
        {
            mpCarry += PlayerMpRegen * Dt;
            if (mpCarry >= 1f) { playerMp = Math.Min(PlayerMaxMp, playerMp + (int)mpCarry); mpCarry -= (int)mpCarry; }
            busy -= Dt;
            if (busy <= 0f && pendingSkill != null) { PlayerHit(pendingSkill, 0); pendingSkill = null; }
            if (setup.ActionMode)
            {
                swing -= Dt;
                if (busy <= 0f)
                {
                    var skill = setup.PlayerSkill;
                    if (skill != null && playerMp >= skill.MpCost)
                    {
                        playerMp -= skill.MpCost;
                        if (skill.CastTime > 0f) { pendingSkill = skill; busy = skill.CastTime; }
                        else { PlayerHit(skill, 0); busy = AnimationLock; }
                    }
                    else if (swing <= 0f)
                    {
                        swing = BasicAttackInterval;
                        PlayerHit(null, BasicAttackPotency);
                        playerMp = Math.Min(PlayerMaxMp, playerMp + BasicAttackMpRestore);
                    }
                }
            }
            else
            {
                auto -= Dt;
                if (auto <= 0f) { auto = ClassicAutoDelay; PlayerHit(null, 110); }
                gcd -= Dt;
                var skill = setup.PlayerSkill;
                if (gcd <= 0f && busy <= 0f && skill != null && playerMp >= skill.MpCost)
                {
                    playerMp -= skill.MpCost;
                    gcd = ClassicGcd;
                    if (skill.CastTime > 0f) { pendingSkill = skill; busy = skill.CastTime; }
                    else PlayerHit(skill, 0);
                }
            }
        }

        // --- companions: skills by cooldown, otherwise auto-attack (CombatCompanionManager) ---
        foreach (var companion in companions)
        {
            var target = enemies.FirstOrDefault(e => e.State.IsAlive);
            if (target == null) break;
            companion.Lock -= Dt;
            foreach (var skill in companion.Behavior.Skills) skill.CooldownRemaining -= Dt;
            companion.AutoTimer -= Dt;
            if (companion.Lock > 0f) continue;
            var ready = companion.Behavior.Skills.OrderByDescending(s => s.Priority).FirstOrDefault(s => s.CooldownRemaining <= 0f);
            int potency;
            if (ready != null) { potency = ready.Potency; ready.CooldownRemaining = ready.Cooldown / pace; }
            else if (companion.AutoTimer <= 0f) { potency = companion.Behavior.AutoAttackPotency; companion.AutoTimer = companion.Behavior.AutoAttackDelay / pace; }
            else continue;
            companion.Lock = AnimationLock;
            var damage = calculator.CalculateNpcAutoAttack(companion.State, target.State, potency).Damage;
            target.State.CurrentHp = Math.Max(0, target.State.CurrentHp - damage);
            if (!target.State.IsAlive) outcome.KillTimes.Add(time);
        }

        // --- enemies (NpcAiController): a ready, affordable skill at random, otherwise auto-attack ---
        foreach (var enemy in enemies)
        {
            if (!enemy.State.IsAlive || !player.IsAlive) continue;
            enemy.MpCarry += CombatBudgetModel.EnemyMpRegenPerSecond * Dt;
            if (enemy.MpCarry >= 1f) { enemy.State.CurrentMp = Math.Min(EnemyMaxMp, enemy.State.CurrentMp + (int)enemy.MpCarry); enemy.MpCarry -= (int)enemy.MpCarry; }
            enemy.Lock -= Dt;
            foreach (var skill in enemy.Behavior.Skills) skill.CooldownRemaining -= Dt;

            void Land(int potency)
            {
                var damage = calculator.CalculateNpcAutoAttack(enemy.State, player, potency).Damage;
                outcome.LargestEnemyHitShare = MathF.Max(outcome.LargestEnemyHitShare, damage / (float)player.MaxHp);
                if (random.NextDouble() < setup.GuardRate) return; // guarded: no damage
                outcome.DamageTaken += damage;
                if (setup.PlayerCanDie) player.CurrentHp = Math.Max(0, player.CurrentHp - damage);
                if (!player.IsAlive) outcome.PlayerDeathTime = time;
            }

            if (enemy.Casting != null)
            {
                enemy.CastRemaining -= Dt;
                if (enemy.CastRemaining > 0f) continue;
                Land(enemy.Casting.Potency);
                enemy.Casting.CooldownRemaining = enemy.Casting.Cooldown / pace;
                enemy.Casting = null;
                enemy.Lock = AnimationLock;
                continue;
            }
            if (enemy.Lock > 0f) continue;

            var hpShare = enemy.State.CurrentHp / (float)enemy.State.MaxHp;
            var ready = enemy.Behavior.Skills.Where(s => s.CooldownRemaining <= 0f && hpShare <= s.HpThreshold &&
                enemy.State.CurrentMp >= CombatBudgetModel.EnemySkillMpCost(Price(s.ActionId), EnemyMaxMp)).ToList();
            if (ready.Count > 0)
            {
                var skill = ready[random.Next(ready.Count)];
                enemy.State.CurrentMp -= CombatBudgetModel.EnemySkillMpCost(Price(skill.ActionId), EnemyMaxMp);
                // Action Mode has no cast bar: the windup is the telegraph and the hit lands at its end.
                if (!setup.ActionMode && skill.CastTime > 0f) { enemy.Casting = skill; enemy.CastRemaining = skill.CastTime; }
                else { Land(skill.Potency); skill.CooldownRemaining = skill.Cooldown / pace; enemy.Lock = AnimationLock; }
                continue;
            }

            enemy.AutoTimer -= Dt;
            if (enemy.AutoTimer <= 0f)
            {
                enemy.AutoTimer = enemy.Behavior.AutoAttackDelay / pace;
                Land(enemy.Behavior.AutoAttackPotency);
                enemy.State.CurrentMp = Math.Min(EnemyMaxMp, enemy.State.CurrentMp + CombatBudgetModel.EnemyAutoAttackMpRestore);
                enemy.Lock = AnimationLock;
            }
        }

        if (enemies.All(e => !e.State.IsAlive) || (setup.PlayerCanDie && !player.IsAlive)) break;
    }

    outcome.Seconds = time;
    outcome.Won = enemies.All(e => !e.State.IsAlive);
    outcome.CostShare = outcome.DamageTaken / setup.PlayerMaxHp;
    return outcome;
}

Outcome Average(Setup setup, int seeds = 40)
{
    var total = new Outcome();
    var wins = 0;
    for (var seed = 1; seed <= seeds; seed++)
    {
        var o = Fight(setup with { Seed = seed });
        total.Seconds += o.Seconds / seeds;
        total.CostShare += o.CostShare / seeds;
        total.PlayerPotency += o.PlayerPotency / seeds;
        total.LargestEnemyHitShare = MathF.Max(total.LargestEnemyHitShare, o.LargestEnemyHitShare);
        total.LargestPlayerHit = Math.Max(total.LargestPlayerHit, o.LargestPlayerHit);
        total.PlayerDeathTime += (o.PlayerDeathTime > 0 ? o.PlayerDeathTime : o.Seconds) / seeds;
        if (o.Won) wins++;
    }
    total.WinRate = wins / (float)seeds;
    return total;
}

bool Within(float value, float target, float tolerance) => MathF.Abs(value - target) <= target * tolerance;
var tiers = Enum.GetValues<EnemyTier>();

var cases = new (string, Action)[]
{
    ("the player's sustained output is what the model assumes, in both play modes", () => {
        // A training dummy with endless HP: output is potency landed per second.
        foreach (var (mode, skill, label) in new (bool, ActionData?, string)[] { (true, null, "action, basic only"), (true, meleeSkill, "action, weaponskill"), (true, spell, "action, spell"), (true, heavySkill, "action, heavy strike") })
        {
            var o = Average(new Setup(180_000, mode, new[] { ((Func<NpcBehavior>)AutoOnly, EnemyTier.Boss) }) { PlayerSkill = skill, HpMultiplier = 1000f, MaxSeconds = 60f, PlayerCanDie = false });
            var output = o.PlayerPotency / o.Seconds;
            Console.WriteLine($"      {label,-22} {output,5:F0} potency/s (model {CombatBudgetModel.ActionModeOutput:F0})   skill: {skill?.Potency} potency for {skill?.MpCost} MP");
            Check(output >= CombatBudgetModel.ActionModeOutput * 0.9f && output <= CombatBudgetModel.ActionModeOutput * 1.35f, $"{label}: {output:F0} potency/s is outside what the reference allows for");
        }
        // Classic output depends on how long the fight is, because the opening MP pool is spent once.
        foreach (var seconds in new[] { 10f, 20f, 60f })
        {
            var o = Average(new Setup(180_000, false, new[] { ((Func<NpcBehavior>)AutoOnly, EnemyTier.Boss) }) { PlayerSkill = meleeSkill, HpMultiplier = 1000f, MaxSeconds = seconds, PlayerCanDie = false });
            Console.WriteLine($"      classic, first {seconds,2:F0} s        {o.PlayerPotency / o.Seconds,5:F0} potency/s (model {CombatBudgetModel.ClassicOutput:F0})");
        }
        var classic = Average(new Setup(180_000, false, new[] { ((Func<NpcBehavior>)AutoOnly, EnemyTier.Boss) }) { PlayerSkill = meleeSkill, HpMultiplier = 1000f, MaxSeconds = 20f, PlayerCanDie = false });
        Check(Within(classic.PlayerPotency / classic.Seconds, CombatBudgetModel.ClassicOutput, 0.30f), $"classic 20 s output {classic.PlayerPotency / classic.Seconds:F0}");
        Console.WriteLine("      (a bot that never misses beats the reference; classic output falls as the opening MP pool runs dry)");
    }),
    ("the fight is the same for any character: naked level 15 or best-in-slot level 100", () => {
        var reference = (Outcome?)null;
        foreach (var hp in new[] { 100, 450, 5_000, 60_000, 180_000, 300_000 })
            foreach (var mode in new[] { true, false })
            {
                if (!mode) continue;
                var o = Average(new Setup(hp, mode, new[] { ((Func<NpcBehavior>)JobKit, EnemyTier.Normal) }) { PlayerSkill = meleeSkill });
                reference ??= o;
                Console.WriteLine($"      max HP {hp,7}: killed in {o.Seconds,5:F1} s, costing {o.CostShare,5:P0} of HP, basic attack shows {120 * CombatBudgetModel.PlayerDamagePerPotency(hp, true),7:F0}");
                // Very small HP pools round every hit to whole points, so allow them more slack.
                Check(Within(o.Seconds, reference.Seconds, 0.08f) && MathF.Abs(o.CostShare - reference.CostShare) < 0.05f, $"max HP {hp} changed the fight");
            }
    }),
    ("every tier takes its intended time to kill and costs its intended share of HP", () => {
        foreach (var mode in new[] { true, false })
            foreach (var tier in tiers)
            {
                var ttk = Average(new Setup(180_000, mode, new[] { ((Func<NpcBehavior>)JobKit, tier) }) { PlayerSkill = meleeSkill, PlayerCanDie = false, MaxSeconds = 400f });
                var pressure = Average(new Setup(180_000, mode, new[] { ((Func<NpcBehavior>)JobKit, tier) }) { PlayerAttacks = false, PlayerCanDie = false, MaxSeconds = CombatBudgetModel.SecondsToKill(tier) });
                var expectedRate = CombatBudgetModel.FightCost(tier) * (mode ? CombatBudgetModel.ActionModeThreat : 1f) / CombatBudgetModel.SecondsToKill(tier);
                var rate = pressure.CostShare / pressure.Seconds;
                Console.WriteLine($"      {(mode ? "action " : "classic")} {tier,-6}: killed in {ttk.Seconds,5:F1} s (target {CombatBudgetModel.SecondsToKill(tier),2:F0}), unavoided cost of the fight {rate * CombatBudgetModel.SecondsToKill(tier),5:P0} (target {expectedRate * CombatBudgetModel.SecondsToKill(tier):P0})");
                // The bot never misses and opens with a full MP pool, so it may be up to twice as fast; it must not be slower.
                // Classic play runs out of MP in a long fight, so its long tiers take longer than the reference says.
                var slowest = CombatBudgetModel.SecondsToKill(tier) * (mode ? 1.1f : 1.8f);
                Check(ttk.Seconds <= slowest && ttk.Seconds >= CombatBudgetModel.SecondsToKill(tier) * 0.45f, $"{tier} time to kill {ttk.Seconds:F1}");
                Check(Within(rate, expectedRate, 0.25f), $"{tier} damage rate {rate:F4} vs {expectedRate:F4}");
            }
    }),
    ("every kind of kit delivers the same pressure for its tier", () => {
        foreach (var mode in new[] { false, true })
            foreach (var (name, make) in kits)
            {
                var o = Average(new Setup(180_000, mode, new[] { (make, EnemyTier.Normal) }) { PlayerAttacks = false, PlayerCanDie = false, MaxSeconds = CombatBudgetModel.SecondsToKill(EnemyTier.Normal) });
                var expected = CombatBudgetModel.FightCost(EnemyTier.Normal) * (mode ? CombatBudgetModel.ActionModeThreat : 1f) / CombatBudgetModel.SecondsToKill(EnemyTier.Normal);
                var rate = o.CostShare / o.Seconds;
                var state = new SimulatedEntityState { MaxMp = EnemyMaxMp, Tier = EnemyTier.Normal };
                KitBudget.Measure(state, make(), Price, true);
                Console.WriteLine($"      {(mode ? "action " : "classic")} {name,-17}: kit {state.KitOutput(mode),4:F0} potency/s, deals {rate / expected,5:P0} of its budget, largest hit {o.LargestEnemyHitShare:P0} of HP");
                Check(Within(rate, expected, 0.25f), $"{name} delivers {rate / expected:P0} of its budget");
            }
    }),
    ("one enemy is a fair fight; several at once are dangerous", () => {
        Outcome Pack(int count, EnemyTier tier, bool mode, float guard = 0f)
            => Average(new Setup(180_000, mode, Enumerable.Repeat(((Func<NpcBehavior>)JobKit, tier), count).ToArray()) { PlayerSkill = meleeSkill, GuardRate = guard, MaxSeconds = 300f });
        var one = Pack(1, EnemyTier.Normal, false); var two = Pack(2, EnemyTier.Normal, false); var three = Pack(3, EnemyTier.Normal, false);
        var weak = Pack(5, EnemyTier.Weak, false); var strong = Pack(1, EnemyTier.Strong, false); var elite = Pack(1, EnemyTier.Elite, false);
        Console.WriteLine($"      classic: 1 normal costs {one.CostShare:P0}; 2 cost {two.CostShare:P0} (won {two.WinRate:P0}); 3 cost {three.CostShare:P0} (won {three.WinRate:P0}); 5 weak cost {weak.CostShare:P0} (won {weak.WinRate:P0})");
        Console.WriteLine($"      classic: 1 strong costs {strong.CostShare:P0} (won {strong.WinRate:P0}); 1 elite costs {elite.CostShare:P0} (won {elite.WinRate:P0})");
        Check(one.WinRate == 1 && one.CostShare is > 0.10f and < 0.35f, "one normal enemy is not a fair fight");
        Check(two.WinRate > 0.9f && two.CostShare is > 0.4f and < 0.95f, "two normal enemies");
        Check(three.CostShare > 0.75f, "three normal enemies should be dangerous without help or guarding");
        Check(weak.WinRate > 0.9f, "five weak enemies should be survivable");
        Check(strong.WinRate > 0.9f && strong.CostShare is > 0.35f and < 0.75f, "a strong enemy");
        // Action Mode: unguarded hits cost double, guarding half of them brings the cost back.
        var open = Pack(1, EnemyTier.Normal, true); var guarded = Pack(1, EnemyTier.Normal, true, 0.5f); var crowd = Pack(3, EnemyTier.Normal, true, 0.6f);
        Console.WriteLine($"      action:  1 normal costs {open.CostShare:P0} unguarded, {guarded.CostShare:P0} guarding half; 3 normals guarding 60% cost {crowd.CostShare:P0} (won {crowd.WinRate:P0})");
        Check(open.WinRate == 1 && Within(open.CostShare, 2f * one.CostShare, 0.45f), "action mode unguarded cost");
        Check(guarded.CostShare < 0.65f * open.CostShare, "guarding does not pay off");
    }),
    ("a boss cannot be out-raced, only out-played, and never one-shots", () => {
        var raced = Average(new Setup(180_000, false, new[] { ((Func<NpcBehavior>)(() => NpcBehavior.Create(NpcBehaviorType.Boss)), EnemyTier.Boss) }) { PlayerSkill = meleeSkill, MaxSeconds = 400f });
        var actionOpen = Average(new Setup(180_000, true, new[] { ((Func<NpcBehavior>)(() => NpcBehavior.Create(NpcBehaviorType.Boss)), EnemyTier.Boss) }) { PlayerSkill = meleeSkill, MaxSeconds = 400f });
        var actionGuard = Average(new Setup(180_000, true, new[] { ((Func<NpcBehavior>)(() => NpcBehavior.Create(NpcBehaviorType.Boss)), EnemyTier.Boss) }) { PlayerSkill = meleeSkill, GuardRate = 0.85f, MaxSeconds = 400f });
        Console.WriteLine($"      classic: dead after {raced.PlayerDeathTime:F0} s (won {raced.WinRate:P0}); action unguarded: dead after {actionOpen.PlayerDeathTime:F0} s; action guarding 85%: won {actionGuard.WinRate:P0}, cost {actionGuard.CostShare:P0}; largest hit {MathF.Max(raced.LargestEnemyHitShare, actionOpen.LargestEnemyHitShare):P0} of HP");
        Check(raced.WinRate < 0.1f && raced.PlayerDeathTime is > 20f and < 60f, "a boss was out-raced, or kills too fast");
        Check(actionOpen.WinRate < 0.1f, "an unguarded boss fight was won");
        Check(actionGuard.WinRate > 0.8f, "a well guarded boss fight was lost");
        Check(MathF.Max(raced.LargestEnemyHitShare, actionOpen.LargestEnemyHitShare) <= CombatBudgetModel.BossMaxHitShare * 1.06f, "a boss hit exceeded the cap");
    }),
    ("a level gap scales the enemy both ways; equal levels change nothing, whatever the level", () => {
        Check(CombatBudgetModel.LevelFactor(15, 15) == 1f && CombatBudgetModel.LevelFactor(100, 100) == 1f, "equal levels must be neutral");
        Check(Within(CombatBudgetModel.LevelFactor(35, 15), 2f, 0.001f) && Within(CombatBudgetModel.LevelFactor(80, 100), 0.5f, 0.001f), "20 levels is a factor of two");
        Check(CombatBudgetModel.LevelFactor(100, 1) == CombatBudgetModel.MaxLevelFactor && CombatBudgetModel.LevelFactor(1, 100) == 1f / CombatBudgetModel.MaxLevelFactor, "level factor bounds");
        Outcome At(int enemyLevel, int playerLevel, EnemyTier tier = EnemyTier.Normal, bool canDie = false)
            => Average(new Setup(180_000, false, new[] { ((Func<NpcBehavior>)JobKit, tier) }) { PlayerSkill = meleeSkill, LevelFactor = CombatBudgetModel.LevelFactor(enemyLevel, playerLevel), PlayerCanDie = canDie, MaxSeconds = 400f });
        var even = At(15, 15); var above = At(35, 15); var below = At(80, 100); var far = At(100, 15, canDie: true);
        Console.WriteLine($"      classic normal enemy, 20 levels below {below.Seconds:F1} s / {below.CostShare:P0}; equal {even.Seconds:F1} s / {even.CostShare:P0}; 20 above {above.Seconds:F1} s / {above.CostShare:P0}; 85 above: won {far.WinRate:P0}, largest hit {far.LargestEnemyHitShare:P0}");
        Check(above.Seconds > 1.5f * even.Seconds && above.CostShare > 2.5f * even.CostShare, "a higher-level enemy must be clearly harder");
        Check(below.Seconds < 0.8f * even.Seconds && below.CostShare < 0.5f * even.CostShare, "a lower-level enemy must be clearly easier");
        Check(far.WinRate < 0.2f, "an enemy 85 levels above should not be beatable by trading blows");
        Check(far.LargestEnemyHitShare <= 0.9f, "even a far higher enemy never kills in one hit");
    }),
    ("companions help by their share; the multipliers do what they say", () => {
        Outcome Solo(int companions, float hp = 1f, float damage = 1f)
            => Average(new Setup(180_000, true, new[] { ((Func<NpcBehavior>)JobKit, EnemyTier.Strong) }) { PlayerSkill = meleeSkill, Companions = companions, HpMultiplier = hp, DamageMultiplier = damage, PlayerCanDie = false, MaxSeconds = 400f });
        var alone = Solo(0); var withOne = Solo(1); var withTwo = Solo(2);
        Console.WriteLine($"      strong enemy killed in {alone.Seconds:F1} s alone, {withOne.Seconds:F1} s with one companion, {withTwo.Seconds:F1} s with two");
        Check(Within(withOne.Seconds, alone.Seconds / (1f + CombatBudgetModel.CompanionShare), 0.3f), "one companion");
        Check(Within(withTwo.Seconds, alone.Seconds / (1f + 2f * CombatBudgetModel.CompanionShare), 0.3f), "two companions");
        Check(withTwo.Seconds < withOne.Seconds && withOne.Seconds < alone.Seconds, "companions do not help");
        // The opening MP pool is spent once, so doubling a fight does not exactly double its length.
        var doubledHp = Solo(0, hp: 2f).Seconds; var doubledDamage = Solo(0, damage: 2f).Seconds;
        Console.WriteLine($"      HP x2: {doubledHp:F1} s; damage x2: {doubledDamage:F1} s");
        Check(Within(doubledHp, 2f * alone.Seconds, 0.3f), "HP x2 did not double the time to kill");
        Check(Within(doubledDamage, alone.Seconds / 2f, 0.3f), "damage x2 did not halve the time to kill");
        var ranged = Average(new Setup(180_000, true, new[] { ((Func<NpcBehavior>)(() => NpcBehavior.Create(NpcBehaviorType.BasicRanged)), EnemyTier.Normal) }) { PlayerSkill = meleeSkill, PlayerCanDie = false });
        var melee = Average(new Setup(180_000, true, new[] { ((Func<NpcBehavior>)(() => NpcBehavior.Create(NpcBehaviorType.BasicMelee)), EnemyTier.Normal) }) { PlayerSkill = meleeSkill, PlayerCanDie = false });
        Console.WriteLine($"      normal melee enemy killed in {melee.Seconds:F1} s, normal ranged enemy in {ranged.Seconds:F1} s");
        Check(ranged.Seconds < melee.Seconds * 0.8f && ranged.Seconds > melee.Seconds * 0.35f, "a ranged enemy is not fragile");
    }),
    ("critical and direct hits change the spread, not the average", () => {
        var calculator = new DamageCalculator(new Random(3));
        var player = new SimulatedEntityState { IsPlayer = true, MaxHp = 180_000 };
        var enemy = new SimulatedEntityState { MaxHp = 1_000_000 };
        double Mean(bool crit, out int largest)
        {
            long sum = 0; largest = 0;
            for (var i = 0; i < 40_000; i++) { var d = calculator.Calculate(player, enemy, meleeSkill, false, crit, crit).Damage; sum += d; largest = Math.Max(largest, d); }
            return sum / 40_000.0;
        }
        var plain = Mean(false, out var plainMax); var rolled = Mean(true, out var rolledMax);
        Console.WriteLine($"      average hit {plain:F0} without, {rolled:F0} with; largest {plainMax} without, {rolledMax} with");
        Check(Math.Abs(rolled - plain) < plain * 0.02, "crits changed the average");
        Check(rolledMax > plainMax * 1.5, "crits are not visible");
        Check(calculator.Calculate(player, enemy, new ActionData { Potency = 0 }).Damage == 0, "a zero-potency action did damage");
        Check(calculator.CalculateNpcAutoAttack(new SimulatedEntityState { IsPlayer = true, MaxHp = 1 }, enemy, 1).Damage >= 1, "a landed hit did no damage");
    }),
    ("tiers, legacy levels and enemy MP prices map as documented", () => {
        Check(CombatBudgetModel.TierLabels.Length == tiers.Length, "tier labels");
        for (var i = 1; i < tiers.Length; i++)
        {
            Check(CombatBudgetModel.SecondsToKill(tiers[i]) > CombatBudgetModel.SecondsToKill(tiers[i - 1]), "tiers must get tougher");
            Check(CombatBudgetModel.FightCost(tiers[i]) > CombatBudgetModel.FightCost(tiers[i - 1]), "tiers must get more dangerous");
        }
        Check(CombatBudgetModel.ClampTier(-3) == EnemyTier.Weak && CombatBudgetModel.ClampTier(99) == EnemyTier.Boss, "tier clamp");
        Check(CombatBudgetModel.TierForChoice(0) == null && CombatBudgetModel.TierForChoice(1) == EnemyTier.Weak && CombatBudgetModel.TierForChoice(5) == EnemyTier.Boss && CombatBudgetModel.TierForChoice(42) == EnemyTier.Boss, "strength choices");
        Check(CombatBudgetModel.StrengthChoices.Length == CombatBudgetModel.TierLabels.Length + 1, "one choice per tier plus Auto");
        Check(CombatBudgetModel.TierForRank(0) == EnemyTier.Normal && CombatBudgetModel.TierForRank(1) == EnemyTier.Strong && CombatBudgetModel.TierForRank(2) == EnemyTier.Boss && CombatBudgetModel.TierForRank(4) == EnemyTier.Elite, "rank detection");
        Check(CombatBudgetModel.EnemySkillMpCost(1500, 5000) == 750 && CombatBudgetModel.EnemySkillMpCost(8500, 5000) == 2000 && CombatBudgetModel.EnemySkillMpCost(0, 5000) == 0, "enemy MP prices");
        Check(CombatBudgetModel.EnemyMaxHp(180_000, EnemyTier.Normal, false, 1f) == (int)MathF.Round(180_000 * 10f / 11f), "normal enemy HP");
        Check(CombatBudgetModel.EnemyMaxHp(1, EnemyTier.Weak, true, 0.0001f) >= 1, "enemy HP floor");
        // An idle kit (a training dummy) and an empty one never divide by zero.
        var dummy = new SimulatedEntityState { MaxMp = EnemyMaxMp };
        KitBudget.Measure(dummy, NpcBehavior.Create(NpcBehaviorType.TrainingDummy), Price, true);
        Check(dummy.KitOutputClassic > 0 && float.IsFinite(dummy.KitOutputAction), "training dummy kit output");
    }),
};

var failures = 0;
foreach (var (name, test) in cases)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
return failures == 0 ? 0 : 1;

sealed record Setup(int PlayerMaxHp, bool ActionMode, (Func<NpcBehavior> Make, EnemyTier Tier)[] Enemies)
{
    public ActionData? PlayerSkill { get; init; }
    public bool PlayerAttacks { get; init; } = true;
    public bool PlayerCanDie { get; init; } = true;
    public float GuardRate { get; init; }
    public int Companions { get; init; }
    public float HpMultiplier { get; init; } = 1f; // test-only: a dummy that outlives the measurement
    public float LevelFactor { get; init; } = 1f;
    public float DamageMultiplier { get; init; } = 1f;
    public bool Crits { get; init; } = true;
    public float MaxSeconds { get; init; } = 200f;
    public int Seed { get; init; } = 1;
}

sealed class Enemy(NpcBehavior behavior, SimulatedEntityState state)
{
    public NpcBehavior Behavior { get; } = behavior;
    public SimulatedEntityState State { get; } = state;
    public float AutoTimer, Lock, MpCarry, CastRemaining;
    public NpcSkill? Casting;
}

sealed class Outcome
{
    public float Seconds, CostShare, DamageTaken, PlayerPotency, LargestEnemyHitShare, PlayerDeathTime, WinRate;
    public int LargestPlayerHit;
    public bool Won;
    public List<float> KillTimes { get; } = new();
}
