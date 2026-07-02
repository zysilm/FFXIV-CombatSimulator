# Fighting Mode 缺陷修复 + 功能补完（一次性大 pass）

> 计划文件位置：`C:\Users\123\.claude\plans\bright-tickling-llama.md`
> 批准后第 0 步：用 `Copy-Item` 把本文件复制到 `C:\PROJECT\FFXIV-CombatSimulator\FIGHTING_MODE_PLAN.md` 并单独 commit（沿用分支的模糊 commit message 惯例），然后按阶段实施。

## Context

分支 `temp/fighting-mode-pass` 上的 Fighting Mode（2D 格斗游戏模式）已有：1v1 engage、lane 约束、2D 侧面相机、post-death session、Translate Cam、ActiveCam 优先级最小重构（未提交）。本 pass 一次性完成：全部已知缺陷修复、CameraModeCoordinator 统一相机所有权、武器 hitbox 判定、guard/telegraph/hitstop 从 ActionMode 解耦复用、专用 2D 输入+跳跃、基础格斗 AI、死后 KO 中点相机。

用户已确认的设计决策：
- **KO 相机**：死后若 MonsterMode 接管且开启怪物跟随，相机取"尸体+怪物"中点并随间距自动拉远；怪物跟随关闭时保持现有骨骼跟随 translate cam。
- **战斗判定**：武器骨骼线段扫过敌人 hurtbox（胶囊）才命中；伤害仍走 DamageCalculator；guard/hitstop/telegraph 解耦复用。
- **2D 输入**：engage 后接管移动——前/后=沿 lane 接近/远离，跳跃=自实现抛物线，左右忽略。
- **格斗 AI**：独立于 NpcAiController 的 1v1 状态机（spacing/接近/后撤/出招/恢复/受击后退），参数 UI 可调。

## 已核实的关键事实

- **Action 子系统复用**：`HitFeedbackController`、`PlayerHitboxResolver`、`TelegraphSystem`、`TelegraphedAttackExecutor`、`PlayerGuardController` 均模式无关可复用；唯一耦合点是 `CombatModeRouter.AttackExecutor` 仅在 `config.ActionMode` 时返回 telegraphed（CombatModeRouter.cs:21）。FightingMode 输入已有独立 `IFightingModeInputSink` 路径（UseActionHook.cs:126-130，优先于 ActionMode）。
- **Stage F 依赖的 API 已确认存在**：`AnimationController.ResolveActionAnimationDuration(uint)`（AnimationController.cs:463）、`PlayPlayerActionAnimationOnly(uint)`（:1570）、`CombatEngine.TrySpendPlayerActionMp`（CombatEngine.cs:1742/1745）、`ProcessNpcAction` 的 `suppressCasterActionEffect`（:602/693）、`BoneTransformService.TryGetSkeletonFromCharBase`（BoneTransformService.cs:71，明确支持武器 draw object 的独立骨骼）、`WeaponDropController.EstimateWeaponHalfLength`（WeaponDropController.cs:698，武器骨骼 AABB 估长的现成范例）。
- **移动**：`MovementBlockHook` approach 集合可屏蔽游戏对任意地址（含本地玩家）的 SetPosition/SetRotation；`SetApproachPosition/Rotation` 经 hook.Original 直写——MonsterMode 驱动怪物已验证此路径。
- **输入范式**：`GameFramework.KeyboardInputs.KeyState[key]`（ActionModeController.cs:141-162）+ `IGamepadState.LeftStick/Raw`（MonsterModeController.cs:293-390）+ 边沿检测。
- **动画**：`ActorVisualStateController.ApplyMoving`（run timeline 22，0.25s 重申）/`ApplyCombatIdle`/`ClearMovement`。
- **相机写入者全景**：DeathCamController（独立 CameraBase.Update vtable hook，22 处写）、ActiveCameraController（getCameraPosition hook 换 orbit center + MinDistance + DirV 锁）、FightingModeController（直写 DirH/V/Distance/InterpDistance/MaxDistance/InputDelta）。MonsterMode 不写相机，仅提供 orbit center + 借用 `SetActive`（待修）。
- **死亡流程顺序**（CombatEngine.ExecuteDeathAnimation ~1288-1356）：BeforePlayerDeath → PlayPlayerDeath → victory → glamour → SuppressDeathCam 检查 → deathCam.Activate → ragdoll.Activate → OnPlayerDeath（MonsterMode 接管点）。
- 全 repo 无跳跃/重力实现；地面 Y 可用 BGCollisionModule 向下 raycast（MonsterModeController.SnapToFloor 范例，:648）。

## 缺陷清单（Stage B 全修）

1. **双 Tick**：Plugin.cs:626 与 :659 每帧调 `fightingModeController.Tick` 两次 → translateElapsed 2x（时长减半）、平滑 2x。
2. **相机三真相源**：`config.EnableActiveCamera` vs `userActive` vs MonsterMode 调 `SetActive(true)` 污染 user 通道（Stage C 根治）。
3. **OnPlayerAction 静默改 config**（FightingModeController.cs:107-108）：无 Save、不恢复 → 挪到 GUI 互斥。
4. **Disengage 状态残留**：`activeCameraWasSuppressing`/`hasLastSuppressedCamera`/`suppressDeathCameraThisDeath` 不清。
5. ActiveCameraController.cs:236 注释与代码优先级相反（mode override 先于 monster override）。

---

## 实施阶段（每阶段独立可 build/commit/游戏内验证）

构建命令：`dotnet build CombatSimulator/CombatSimulator.csproj -c Release`（须在有/无 DEV_EXPERIMENTAL 下都过）。
Commit message 沿用分支模糊惯例（如 "Refine camera flow"），不用日期规则。

### Stage A — 提交现有未提交的相机修复（原样）

1. 先提交 submodule `CombatSimulator/Dev/Experimental`（branch main）：MonsterModeController.cs 一行改动（`prevActiveCamState = activeCamera.IsUserActive`）。不要把 submodule 目录里的垃圾文件（`Microsoft.NET.Workload_*.log`、`NuGetScratch/`、`k05eiflj.fdw`、`sdblbne0.obq`）加进去。
2. 回主仓库：提交 4 个修改文件（ActiveCameraController/Configuration/FightingModeController/MainWindow）+ submodule gitlink。
3. Build 验证。

### Stage B — 缺陷修复（无新功能）

改：`Plugin.cs`、`Fighting/FightingModeController.cs`、`Gui/MainWindow.cs`、`Camera/ActiveCameraController.cs`。

1. **双 Tick**：保留 Plugin.cs:626 的完整 `Tick(deltaTime)`；:659 改调新方法 `public void ReapplyLane()`——只重跑 `ApplyLane`（守卫 `engaged && laneInitialized` + 地址有效），无 dt 累积、无 UpdateCamera。
2. **config 静默改写**：删掉 OnPlayerAction 里的两行；MainWindow 的 FightingMode 勾选处已有互斥+Save（:1419-1424），补反向互斥：ActionMode / Custom Targeting 勾选启用时置 `config.FightingMode = false` + Save。
3. **Disengage 清理**：补清 `activeCameraWasSuppressing`、`hasLastSuppressedCamera`、`suppressDeathCameraThisDeath`、`translateElapsed`（安全：SuppressDeathCam 在死亡时刻求值，postDeathEngaged 阻止死亡会话中 Disengage）。
4. **注释纠正**：ActiveCameraController detour 注释按实际优先级改写（Stage C 会重构，本阶段先保真）。

验证：translate 时长与滑条一致；disengage 后再 engage 无残留压制状态。

### Stage C — CameraModeCoordinator（相机写入单一权威）

新文件：`Camera/CameraModeCoordinator.cs`。改：`ActiveCameraController.cs`、`FightingModeController.cs`、`DeathCamController.cs`、`Plugin.cs`、`Dev/IDevExperimental.cs`、`Dev/DevExperimentalStub.cs`、submodule `DevExperimentalModule.cs` + `MonsterModeController.cs`、`MainWindow.cs`。

```csharp
public enum CameraOwner { None=0, DeathCam=10, MonsterFollow=20, Fighting2D=30, FightingKO=40, UserActiveCam=50 }

public struct CameraRequest
{
    public Vector3? OrbitCenter;      // 由 ActiveCameraController 的 getCameraPosition detour 消费
    public float? DirH, DirV;
    public float? Distance;           // 同写 Distance + InterpDistance
    public float? MaxDistanceAtLeast; // 只升不降；coordinator 负责 save/restore
    public bool ClearInputH, ClearInputV;
}

public sealed unsafe class CameraModeCoordinator
{
    public void Submit(CameraOwner owner, in CameraRequest request); // 槽位 2 帧 TTL
    public void Release(CameraOwner owner);
    public void Apply(float dt);            // 每帧一次，取最高优先级活槽写相机
    public CameraOwner CurrentOwner { get; }
    public Vector3? CurrentOrbitCenter { get; }
    public bool WantsOrbitHook { get; }
}
```

- 2 帧 TTL 吸收 MonsterMode 自有 framework.Update 的顺序不确定性；`UserActiveCam` 在 `config.EnableActiveCamera` 时由 coordinator 内部自动提交空请求（ActiveCameraController 作为 winner 时继续自己的骨骼 orbit/MinDistance/DirV 锁）。
- MaxDistance：coordinator 统一 save/restore（替换 FightingModeController 的 savedMaxDistance/maxDistanceOverridden/RestoreCameraMaxDistance）。

迁移：
- **ActiveCameraController**：删 `GetOrbitCenterOverride`/`GetModeOrbitCenterOverride`，加 `Func<Vector3?>? CoordinatorOrbitCenter`；detour：original → !IsActive return → coordinator center 有值则写入 return → 否则（user winner）现有骨骼跟随。Tick 的 MinDistance/DirV 锁门控改为 `CurrentOwner == UserActiveCam`（注入 `Func<CameraOwner>`）。Plugin 在 `coordinator.Apply(dt)` 后驱动 `SetModeActive(coordinator.WantsOrbitHook)`；`userActive` 回归纯 GUI 所有。
- **FightingModeController**：UpdateCamera/UpdateTranslateCamera 保留全部平滑数学，但不再写 `gameCam->*`，改构建 CameraRequest → `Submit(Fighting2D/FightingKO, ...)`；删 `CameraCenterOverride`、MaxDistance save/restore、Engage/Disengage 里的 SetModeActive；`config.EnableActiveCamera` 压制检查删除（优先级机制取代），保留 `CaptureSuppressedCameraState()`（当 `CurrentOwner == UserActiveCam` 时捕获，供 translate 交接起点）。
- **MonsterMode（submodule）**：删除所有 `activeCamera.SetActive(...)`/`GetOrbitCenterOverride`/`prevActiveCamState`；IDevExperimental 加 `void SetCameraCoordinator(CameraModeCoordinator coordinator)`（stub 空实现）；MonsterModeController 每帧 `if (IsActive && config.MonsterCameraFollowsMonster) coordinator.Submit(MonsterFollow, new { OrbitCenter = CameraCenter() })`，Despawn 时 `Release`；ToggleCamera 只改 config+Save。
- **DeathCamController**：保留独立 vtable hook 与 SuppressDeathCam 门控；`state != Inactive` 时每帧 `Submit(DeathCam, default)` 声明所有权，且注入 `Func<CameraOwner>`，当 `CurrentOwner > DeathCam` 时跳过自身写入（软仲裁，本轮不做 hook 统一）。
- **Plugin.cs**：coordinator 在 ~152 行附近构建；tick 顺序：fightingModeController.Tick → deathCamController.Tick → `coordinator.Apply(deltaTime)` → activeCameraController.Tick。

验证：fighting 相机行为与之前一致；用户 ActiveCam 压过 fighting；死后怪物跟随可用；disengage 恢复 MaxDistance。

### Stage D — 2D 输入接管 + 跳跃

新文件：`Fighting/FightingPlayerMotor.cs`。改：`FightingModeController.cs`、`Configuration.cs`、`Plugin.cs`、`MainWindow.cs`。

```csharp
public sealed unsafe class FightingPlayerMotor
{
    public void Begin(nint playerAddress, Vector3 startPos); // AddApproachNpc(player)，播种 along/groundY
    public void End();                                       // RemoveApproachNpc(player)，清动画覆盖
    public bool IsAirborne { get; }
    public float AlongPos { get; }   // FightingModeController 据此写位置
    public float PosY { get; }
    public void Tick(float dt, float enemyAlong, Character* playerCharacter);
}
```

- 移动屏蔽：Engage 时 `AddApproachNpc(player.Address)`（approach 集合对任意地址屏蔽游戏写入，MonsterMode 已验证）；Disengage 已有移除逻辑。
- 输入：键盘走 GameFramework KeyState + `ImGui.GetIO().WantCaptureKeyboard` 守卫；手柄左摇杆 Y（0.15 死区）。前进符号 = `Sign(enemyAlong - alongPos)`；横向输入忽略。
- 跳跃：着地时边沿触发 → `velocityY = FightingModeJumpVelocity`；每帧 `posY += velocityY*dt; velocityY -= FightingModeGravity*dt`；地面 Y 用 BGCollisionModule 向下 raycast（SnapToFloor 范例）+ lane 捕获时 Y 兜底；`posY <= groundY` 落地。ConstrainToLane 本就透传 Y。
- 动画：地面移动 `ApplyMoving`，停止 `ClearMovement`；跳跃动画藏在 `FightingModeJumpAnimation`（默认 false，待游戏内验证合适 timeline id）。
- **ApplyLane 重构**：玩家 along/Y 来自 motor；敌人保持现状（Stage G 接手）。只保留 **min** separation（钳制移动方），删掉 max-separation 反向平移（其值仅作相机取景输入）——彻底根除"动敌人拽玩家"一类 bug。

新 config（默认值）：`FightingModeMoveSpeed=4.0f`、`FightingModeJumpVelocity=5.5f`、`FightingModeGravity=16f`、`FightingModeForwardKey=0x57(W)`、`FightingModeBackKey=0x53(S)`、`FightingModeJumpKey=0x20(Space)`、`FightingModeJumpGamepadButton=South`、`FightingModeJumpAnimation=false`。

验证：engage 中 WASD 不再自由移动；W/S 沿线滑动；Space 干净抛物线落地。

### Stage E — guard/telegraph 与 ActionMode 解耦

新文件：`Fighting/FightingCombatController.cs`（本阶段先做 guard 输入；攻击在 Stage F）。改：`Action/CombatModeRouter.cs`、`Action/ActionModeController.cs`、`Plugin.cs`、`Configuration.cs`、`MainWindow.cs`。

1. **Router 解耦**（唯一耦合点）：
   ```csharp
   public IAttackExecutor AttackExecutor =>
       config.ActionMode || forceTelegraphed?.Invoke() == true ? telegraphed : instant;
   ```
   Plugin 传 `() => fightingModeController?.IsEngaged == true`（延迟捕获，构造顺序无碍：router 在 :177，fighting 在 :232）。
2. **共享 ticking**：Plugin.cs:162 的局部 `playerGuardController` 提为字段；把 `guard.Tick(dt)` 和 `telegraphSystem.Tick(dt)` 从 ActionModeController.Tick（:103、:116-117）上提到 Plugin.OnFrameworkUpdate（actionModeController.Tick 之前；空转无副作用，顺序保持）。ActionModeController 保留自身 reset/OnModeExit。
3. **Fighting guard 输入**：FightingCombatController.Tick（每帧，自门控 `config.FightingMode && IsEngaged && player alive`）读 `FightingModeGuardKey`（默认 17=Ctrl）+ `FightingModeGuardGamepadButton`（默认 East），边沿触发 `playerGuardController.TryGuard()`。NotifyPerfectGuard/RestorePlayerGuardMp 布线不动。
4. **Overlay 门控**：Plugin.DrawUI（~:555-562）的 telegraphOverlay/osuParryOverlay 从 ActionMode-only 扩为 `config.ActionMode || fightingModeController.IsEngaged`。

由于 NpcAiController 所有攻击已走 `combatModeRouter.AttackExecutor.Execute`（:515/:566/:588），本阶段结束时敌人在 Fighting Mode 下即自动 telegraph 化、guard 可用——独立可验证的中间态。

新 config：`FightingModeGuardKey=17`、`FightingModeGuardGamepadButton=East`。

### Stage F — 武器骨骼 hitbox 玩家战斗

新文件：`Fighting/WeaponHitboxService.cs`；扩展 `FightingCombatController.cs`。改：`Animation/BoneTransformService.cs`、`Simulation/CombatEngine.cs`、`FightingModeController.cs`、`Configuration.cs`、`MainWindow.cs`、新增 debug overlay。

武器几何（可行性已核实）：
```csharp
public unsafe sealed class WeaponHitboxService
{
    public readonly record struct WeaponSegment(Vector3 Base, Vector3 Tip);
    public WeaponSegment? GetMainHandSegment(nint characterAddress);
}
```
1. 主路径：`character->DrawData.Weapon(MainHand).DrawObject` 存在 → base=武器骨骼世界根（`TryGetSkeletonFromCharBase`），方向=武器根旋转 × 武器骨 AABB 主轴（取远离手骨那端），长度=`clamp(longestAxis*1.1, 0.4, 3.0) * FightingModeWeaponLengthScale`（复用 EstimateWeaponHalfLength 思路）。单骨武器：配置轴（默认局部 +Y）× `FightingModeWeaponLength`。
2. 兜底（空手/无 draw object）：`BoneTransformService` 新增 `GetBoneWorldTransform(nint, string) → (Vector3 Pos, Quaternion Rot)?`（GetBoneWorldPos 同数学 + 旋转合成）；用 `n_buki_r` 否则 `j_te_r`，tip = pos + 面向 × FightingModeWeaponLength(1.2f)。
3. Debug：`FightingModeDebugDraw` 开关画实时线段+敌 hurtbox（WorldToScreen，RagdollDebugOverlay 范式）——调参必备。

挥击状态机（FightingCombatController）：
- `enum SwingPhase { Idle, Windup, Active, Recovery }`；每挥 `hitLanded` 去重 + `prevSegment`。
- `FightingModeController.OnPlayerAction` 不再 `EnqueuePlayerAction`，Engage 后改调 `fightingCombat.OnAttackInput(actionId, npc)`。
- OnAttackInput：非 Idle 忽略；`TrySpendPlayerActionMp`；`duration = ResolveActionAnimationDuration(actionId)`；`PlayPlayerActionAnimationOnly(actionId)`；active 窗口 = `[duration*ActiveStartPct, duration*ActiveEndPct]`（默认 0.25/0.70）。
- Active 中每帧扫掠：prevSegment→当前段插值采样 3 段，对敌 hurtbox 胶囊（敌位置竖直线段，高 `FightingModeHurtboxHeight=2.0`，半径 `npc.HitboxRadius * HurtboxRadiusScale`）做线段-线段最小距离，`< hurtRadius + FightingModeWeaponRadius(0.15)` 判中。
- **命中（每挥一次）**：`ApplyPlayerActionMode(actionId, target, 0, duration, suppressCasterActionEffect: true)`——给 ApplyPlayerActionMode 加该可选参（镜像 ProcessNpcAction :602/:693 的现成模式），配套新 `TriggerManualPlayerHitFeedback(hits)`（仿 TriggerManualNpcHitFeedback），避免接触瞬间重播挥砍动画；随后 `hitFeedbackController.TriggerHit(target)`（hitstop/震屏/火花，已模式无关）+（Stage G）`fightingAi.NotifyPlayerHitLanded()`。
- Disengage/玩家死亡/sim reset 时 `Reset()`。

新 config：`FightingModeWeaponLength=1.2f`、`WeaponLengthScale=1.1f`、`WeaponAxis=1(Y)`、`WeaponRadius=0.15f`、`HurtboxHeight=2.0f`、`HurtboxRadiusScale=1.0f`、`AttackActiveStartPct=0.25f`、`AttackActiveEndPct=0.70f`、`FightingModeDebugDraw=false`。

### Stage G — 格斗 AI

新文件：`Fighting/FightingAiController.cs`。改：`FightingModeController.cs`、`Plugin.cs`、`Configuration.cs`、`MainWindow.cs`。

```csharp
public sealed class FightingAiController
{
    public void Begin(SimulatedNpc npc);
    public void End();
    public float DesiredAlong { get; }
    public bool IsActive { get; }
    public void Tick(float dt, float npcAlong, float playerAlong);
    public void NotifyPlayerHitLanded(float pushDirection);
}
```
- `enum FightingAiState { Neutral, Approach, Retreat, Committing, Recover, Hitstun }`。
- Neutral：距离 vs 区间 `[FightingAiRangeMin, FightingAiRangeMax]` → Approach/Retreat；区间内且冷却到 → 按 NpcAiController.TickCombat 同款构建 `NpcAttackRequest`（auto 或 off-cooldown skill）→ `router.AttackExecutor.Execute(npc, req)`——engage 中恒 telegraphed（Stage E），**guard 对 AI 所有攻击天然生效**。Committing 至 `!telegraphs.IsWindingUp(...)` → Recover（`FightingAiRecoverTime`）→ 冷却加 jitter + 后撤掷骰（`FightingAiRetreatChance`）。
- Hitstun：`NotifyPlayerHitLanded` 仅在 `!IsWindingUp`（保留 super-armor）→ Hitstun `FightingAiHitstunDuration`，`FightingAiHitstunPushback` 远离玩家衰减位移。
- **位置写入者仍是 FightingModeController**：ApplyLane 把 `ai.DesiredAlong` 作为敌人目标 lane 坐标（min-sep 钳制 + lane 投影 + 地面 Y + 朝向），AI 有移动意图时对敌人 ApplyMoving/ClearMovement。
- **NpcAiController 跳过**：FightingModeController 加 `public bool ControlsEnemy(nint addr) => engaged && targetAddress == addr;`；Plugin.cs:212 的 lambda 改为 `addr => devExperimental.ControlsNpc(addr) || fightingModeController.ControlsEnemy(addr)`（延迟捕获）。
- 生命周期：Engage → ai.Begin；Disengage/HandlePlayerDeath → ai.End。

新 config（全 UI 滑条）：`FightingAiMoveSpeed=3.0f`、`RangeMin=1.8f`、`RangeMax=3.2f`、`AttackCooldown=2.5f`、`CooldownJitter=1.0f`、`RetreatChance=0.35f`、`RetreatDuration=0.8f`、`RecoverTime=0.6f`、`HitstunDuration=0.35f`、`HitstunPushback=1.5f`。

### Stage H — KO 相机（尸体+怪物中点）

改：`Dev/IDevExperimental.cs`、`Dev/DevExperimentalStub.cs`、submodule `DevExperimentalModule.cs` + `MonsterModeController.cs`、`FightingModeController.cs`、`Plugin.cs`、`Configuration.cs`、`MainWindow.cs`。

1. Seam 新增：`Vector3? ControlledMonsterCenter { get; }`（stub 返回 null；DevExperimentalModule → Monster.FollowCenter；MonsterModeController：`IsActive && config.MonsterCameraFollowsMonster ? CameraCenter() : null`）。
2. FightingModeController 注入 `Func<Vector3?> getMonsterFollowCenter`；死后相机分支：
   - 有值 → **KO 相机**：`center = midpoint(GetTranslateTargetCenter(corpse), monsterCenter) + KoCameraHeight`；`distance = clamp(horizSep * KoCameraMargin + KoCameraBase, KoMin, KoMax)`；沿用现有指数平滑；`Submit(FightingKO, { OrbitCenter, Distance, MaxDistanceAtLeast, DirH = 侧视垂直角(KoLockAngle 时) })`。
   - 无值 → 现有骨骼跟随 translate cam（Stage C 起已走 FightingKO 提交）。
3. 优先级自动仲裁：MonsterMode 继续提交 MonsterFollow，但 fighting 死亡会话存续期间 FightingKO 更高；会话结束 MonsterFollow 自然接管；用户 ActiveCam 恒最高。

新 config：`FightingModeKoCameraMargin=0.8f`、`KoCameraBase=3.0f`、`KoCameraMinDistance=3f`、`KoCameraMaxDistance=14f`、`KoCameraHeight=0.8f`、`KoLockAngle=true`。

### Stage I — GUI 归整

MainWindow 的 Fighting Mode 段落分组（全部 change→Save + HelpMarker）：**Input**（移动/跳跃/guard 键位 + 手柄键）、**Combat**（武器长度/轴/半径、hurtbox、active 窗口、debug draw）、**Fighting AI**（Stage G 全滑条）、**KO Camera**（Stage H 滑条）。可选 "Reset Fighting defaults" 按钮（仿 ResetActionModeDefaults）。

---

## 风险与开放问题

1. **武器骨骼形态异构**（单骨/弓/书/球）——debug overlay + 轴/长度配置兜底；定位为 melee-first，其余走手骨兜底。
2. **本地玩家的 ApplyMoving**——run timeline 覆盖在 NPC/怪物上已验证，本地玩家因 approach-block（游戏视为静止）理论可行，需游戏内验证；失败则平移无跑动动画兜底。
3. **跳跃 vs telegraph**——`IsInside` 仅 XZ 圆，跳跃躲不掉；如要跳跃闪避需给 inside 测试加垂直容差（小改动，延后决策）。
4. **MonsterFollow 提交帧序**——2 帧 TTL 吸收，despawn 有显式 Release，最坏 1 帧陈旧 orbit center。
5. **guard 默认键 Ctrl 与游戏键位冲突**——可配置，且仅 engage 中消费。
6. **DeathCam 本轮软仲裁**——仍走自有 hook，coordinator 只做压制 + SuppressDeathCam 门控不变；hook 统一留待后续。

## 验证（每阶段游戏内）

- A/B：translate 时长=滑条值；disengage→再 engage 无残留。
- C：fighting 相机不回归；ActiveCam 开/关切换平滑；死后 ActiveCam 关闭能 translate 回死亡相机；怪物跟随可用。
- D：engage 中自由移动被接管，W/S 沿线，Space 抛物线；disengage 恢复正常操控。
- E：Fighting 中敌人攻击出现 telegraph 圈，Ctrl guard 成功触发 perfect guard/MP 返还；ActionMode 单独开时行为不变。
- F：开 debug draw 调参；武器扫过才掉血，挥空不掉血；命中有 hitstop/震屏；每挥最多一次伤害。
- G：敌人保持距离带、进退、telegraph 出招、被打有 hitstun 后退；NpcAiController 不再干扰 engaged 敌人。
- H：死后 MonsterMode 接管+跟随开 → 相机框住尸体与怪物中点并随距离缩放；跟随关 → 骨骼跟随；ActiveCam 恒压制。
- 每阶段 `dotnet build -c Release` 通过后按惯例模糊 commit（submodule 先行，主仓库跟进 gitlink）。
