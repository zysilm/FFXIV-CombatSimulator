# 战争生成器（War Generator）

状态：需求已定（2026-11-13）。三步均已实现，待游戏内验证。本文件是需求与方案的唯一依据，会话压缩后以此为准。

## 1. 背景

插件玩法单调的原因不是缺机制，而是没有东西把现有机制串成一场有起伏的战斗：

- 野外：很难同时凑到理想的地图、友军和敌人；会被真实小怪袭击；行为怪异可能被举报。
- Hyperborea 地图：只能 1 对多，地图空荡，没有队友。
- 死亡方式：击倒即结束，没有处刑之类的特殊事件。

## 2. 需求（用户原话整理，已逐条确认）

### 2.1 战争生成器

- 把现有“虚拟敌人”tab 扩展为战争生成器。基本 UI 沿用。
- 可以生成**队友**或**对手**。等级等参数照现在的方式可调。
- 队友**不限人形**，任何生物都可以当队友。
- 对手生成位置用下拉菜单选模式：现有的“绕圈”“前方”，新增“地图”（在地图上散开）。
- **巡逻**是开关，生成敌人的那一刻决定该敌人是否巡逻。
- 地图散开依赖 vnavmesh：没装或该地图没有导航网格时，该选项不显示，不做降级方案。
- **Battle profile 全面由战争生成器接管**；战争生成器兼容“地图上的队友”（周围可见玩家）和“地图敌人感知”；用 JSON 定义。
- **选人界面不能更难用，也不能少功能。** UI 要精妙、极简。

### 2.2 高级战斗模式（放在战争生成器里新增的区域）

**a) 人质模式**（开关，仅适用我方成员，含玩家和队友）。**2026-11-13 改为战国专属**：只在选了战国时出现、生效（用户：春秋下开不开人质只是演出差别，太麻烦）。

- 我方成员 HP 被打空时强制留 1 HP，进入“待戮”状态：播放选定的 emote，不能动。
- 敌人可以攻击这个状态的我方成员；被攻击致死时以布娃娃 0 延迟死亡。
- 待戮成员仇恨很低：只有附近没有其他可打目标的敌人才走过去处刑，处刑前有一小段走近和起手时间。

**b) 复活规则**（下拉：无 / 春秋模式 / 战国模式，二者互斥；每项附气泡说明）

- **春秋模式**（可复活）
  - 队友可以拯救死亡的我方成员；玩家死了，队友会抢过来复活玩家。
  - 复活约 8 秒；复活期间**施救者**受到的伤害 ×4。
  - ~~待戮（人质）成员也可以被队友解救~~（人质改为战国专属后，春秋下不会出现人质）。
- **战国模式**（主公模式，更残酷）
  - 玩家是主公。主公和队友都**不可复活**（待戮成员也不可解救——这一条是我的推断，用户说的是“不可复活”）。
  - 主公死亡后分两条分支，由“撤退模式”开关决定：
    - 撤退模式**关**：所有队友自动进入待戮状态。
    - 撤退模式**开**（守尸）：一名队友寻路到主公正前方，使用 pray emote；他不能复活主公、不能攻击敌人、受到伤害 ×4；其他队友守护祈祷者直到击退敌人。
      - 祈祷者死亡：由下一名队友接替，直到没人为止。
      - 主公尸体会被敌人抢夺：敌人抢到后直接启动 Enemy Control 的 grab（使用当前 grab 设置），并立即判定**战败**。

### 2.3 状态与显示

两类状态可叠加：

- 结果：**战胜**（全歼敌人）／**战败**（我方被全歼，或主公尸体被夺）／**收尸成功**（战国模式下主公死亡，但护住了尸体并全歼敌人）。
- 个人：**战斗中**／**战死**／**复活他人中**／**守护主公中**／**待戮**。

显示方式：

- 血条前缀显示个人状态，固定文字，由状态自动决定。
- 结束时用 banner 显示结果。
- 删除自定义 Sim Label 和 Death text 两个输入框。

### 2.4 实施顺序

1. 战争生成器：阵营选择、非人形队友、地图散开、巡逻开关、battle profile 接管（JSON）。
2. 人质模式 + 春秋模式（拯救）。
3. 战国模式 + 撤退守尸 + 状态前缀与结果 banner。

每一步做完可单独进游戏验证。

## 3. 约束

- 不做程序化姿势；待戮、祈祷都用游戏现成 emote。
- UI 极简；调试/内部参数不进界面；选择用下拉。
- `CombatSimulator/Dev/Experimental` 是独立私有仓库：不改、不破坏其编译。
- 纯客户端表现，不发包。

## 4. 设计

### 4.1 代码现状（调研结论）

- tab 名原为 "Virtual Enemies"（`Gui/MainWindow.cs` `DrawVirtualEnemiesTab`）。位置原来只是一个 "Front" 勾选（关 = 环绕），一次生成一只，没有数量。
- 敌人：`NpcSpawner` 造角色 → `OnNpcSpawnComplete` → 注册为敌人（`NpcSelector` + `CombatEngine.RegisterNpcEntity`）。阵营只由 `SimulatedEntityState.IsCompanion / IsPlayer` 决定。
- 队友：`CombatCompanionManager`，只能克隆玩家模型（可见玩家 / 自己）。队友 AI、选目标、移动、伤害都在这里。伤害按自身 MaxHp 折算，所以 HP 同时决定了耐打和输出。
- 敌人选目标：`CombatCompanionManager.ScoreEnemyTargetCandidate`（唯一可加“低优先级”权重的地方）。
- 敌人待机：`NpcAiState.Idle` 只站着；队友模式下每帧强制全员 `EngageNpc`；没有巡逻、没有感知范围。
- battle profile 在代码里叫 Combat Recipe：`Recipes/CombatRecipe*.cs`，只有内置的 `Resources/CombatRecipes/default.json`，不能保存用户自己的。
- 死亡：玩家 `CombatEngine.ExecuteDeathAnimation`；队友在 `TickCompanion` 里；敌方胜利 `TriggerEnemyVictoryIfPartyDefeated`；`OnOutcomeDecided(bool)`。
- 0 延迟布娃娃：玩家 `ForcePlayerInstantDeath()`；NPC/队友需绕过排队直接 `Activate(addr, 0f)`。
- 表情：`AnimationController.EmotePlayer.PlayLoopedEmote / ResetEmote`，对任意角色可用。Pray = emote 58（时间线 742）。
- grab：`EnemyControlController.ControlKiller(npc)` 再 `OnAttackInput()`；前提是玩家布娃娃已激活且配置了 grab pair。
- 玩家输入：只有 `MovementBlockHook.IsBlocking`（丢弃本地玩家的位置/朝向写入）和 `UseActionHook`（技能）。
- 标签：`Gui/HpBarOverlay.BuildPlayerNameLabel`；没有现成的大字 banner（可参照 `OsuParryOverlay` 的缩放文字）。
- 时间线：3 = idle，13 = walk，22 = run。

### 4.2 第一步：战争生成器

**任意生物当队友**：`NpcSpawnRequest.Ally`。`NpcSpawner` 照常造角色，画出来之后不注册为敌人，而是把槽位所有权交给 `CombatCompanionManager.AdoptNpc`，包装成 `CombatCompanion`（`IsCatalogNpc = true`，跳过换装/Glamourer 这些只对人形克隆有意义的步骤）。它用自己目录条目的技能表和按强度/等级算出的 HP，之后完全走现有队友 AI。队友总是生成在玩家身边。

**位置模式**：`SpawnPlacement { Surround, Front, Map }`，随请求记录。`Map`：一次生成（Count 只）是一个小队，小队中心在玩家周围 `MapSpawnRadius` 内随机取点并吸附到导航网格，成员围绕中心散开。重置时地图小队回到原岗位。

**哨兵与巡逻**：地图生成或开了巡逻的敌人是“哨兵”（`SimulatedNpc.IsSentry`）：
- 不参与“全员自动开战”，待在 `Idle`；
- 我方任何人进入感知范围，或附近有同伴已开战，才 `EngageNpc`；被打也会开战（已有逻辑）；
- 目标死了回到 `Idle` 后继续当哨兵；
- `Patrols`：待机时用走路动作（时间线 13）在岗位附近的随机点之间来回，点与点之间停几秒；
- 队友不主动挑没开战的哨兵；玩家无目标出招也不会自动打到没开战的哨兵。

**battle profile 接管**：`CombatRecipe` 就是战争生成器的 JSON 定义，扩展字段：`allies`（目录条目组）、敌人组的 `placement / patrol / strength / level`，以及后续的 `modes`。旧字段原样兼容（`companions` 的可见玩家/自己克隆、`mapEnemies` 的地图感知）。用户 profile 存为配置目录下 `BattleProfiles/<name>.json`，与内置的一起列出。战争生成器顶部：选 profile、Start/Stop、把“当前场上的阵容”存为 profile、删除自己存的。

**UI**（tab 改名 "War Generator"，选人部分原样保留）：选人列表下面依次是 Side（Opponent/Ally）、Count、Placement（Around me / In front / Across the map，仅对手）、Patrol（仅对手）、强度、等级、Spawn。已生成列表里同时列出队友和对手。

### 4.3 第二、三步（已实现）

UI：War Generator tab → 折叠标题 "Battle Rules" → 卡片 "The fallen"：下拉（Stay down / Spring and Autumn / Warring States，问号气泡解释）；选 Warring States 时出现 Hostages 勾选、Captive emote 下拉（默认 Kneel，主公阵亡后被扣的队友也用它）和 Retreat 勾选。

- **待戮**（`SimulatedEntityState.IsCaptive`）：战国且勾选 Hostages 时，`CombatEngine.ApplyNpcHit` 把我方的致死一击截到 1 HP 并 `TakeCaptive`。玩家：`MovementBlockHook.IsBlocking` 锁移动，技能/自动攻击被拒，播放所选表情；队友：`CombatCompanionManager.TickCaptive`。敌人选目标时，只要还有非待戮目标就把待戮者剔除（`SelectEnemyTargetInternal`）。处刑前敌人第一次出手只起手（`ExecutionReady`，1.5 秒）；溅射伤害不会处刑。处刑一击置 `Executed` → 玩家走 0 延迟布娃娃，队友在 `Plugin.OnCompanionDeathRagdoll` 用 0 延迟。全歼敌人时释放所有待戮者。表情只对人形有效（非人形生物只是站着）。
- **春秋**（`CombatCompanionManager.Rescue.cs`）：每帧给倒下/待戮的我方成员（玩家优先）分配最近的空闲队友；跑过去、跪地（Kneel）8 秒，期间 `IncomingDamageScale = 4`；离开或被打倒则中断、清零。完成后：死亡者 50% HP 复活（玩家 `RevivePlayerInPlace(0.5)`，队友 `ReviveCompanion` 并移除其布娃娃/掉落武器）；待戮者 25% HP 释放。
- **战国**（`CombatCompanionManager.Lord.cs`、`Plugin.War.cs`）：没有任何复活。主公（玩家）死亡：Retreat 关 → 所有队友 `TakeCaptive`（不论 Hostages 是否勾选）；Retreat 开 → 最近的队友跑到主公身体正前方 1.3 m（按玩家朝向），播放 Pray（58），不攻击，受伤 ×4；死了由下一个接替。守尸（2026-11-13 起，原来“其他队友正常战斗即守护”在游戏里等于没人管主公）：其他队友的指挥锚点改为尸体、范围 10 m，空闲时站到尸体周围 3 m 的一圈；选目标时去抢尸的敌人 +400、离尸体越近的敌人越优先（12 m 内最多 +200）。夺尸：存活敌人中离尸体最近的一半（至少 1 个）组成抢尸队（`SimulatedNpc.SeizeGoal`，闲着的哨兵也会被叫醒），一直往尸体走，射程内有守卫就停下打（优先打祈祷者，其次离尸体近的守卫）；死了由下一个补上。只要还有能战斗的队友站在尸体 3.5 m 内，就抢不走；尸体位置取玩家布娃娃的骨盆（没开布娃娃才用死亡位置）。没人守住时，只要任一抢尸队员身体边缘离尸体 ≤1.5 m，共用的夺尸进度就累积，离开或被守住则以一半速度回落（不清零，挤来挤去不会前功尽弃），累积满 2 秒即夺尸；dalamud.log 每 2 秒一行 `War:` 记录最近的抢尸者距离、守住者及进度：剩余队友全部待戮，`ForceEnemyVictory`，并由 Enemy Control 接管该敌人、按当前设置 grab（需布娃娃已激活）。此模式下玩家死亡时不再触发 Enemy Control 的 "control killer / spawn on death"。
- **显示**：`Gui/WarBanner.cs` 结果大字 6 秒：VICTORY / DEFEAT / BODY RECOVERED（战国且主公已死时取胜）。第一个结果为准。HP 条名字前缀固定为状态（`CombatCompanionManager.StatusOf`）：Fighting / Raising / Guarding / Praying / Captive / Fallen。Sim Label、Dead Label 输入框已删除（配置字段保留以兼容旧配置迁移，不再显示）。

### 4.4 已知限制 / 待游戏内确认

- 玩家待戮时只锁了位置写入和技能，按方向键可能仍会播放跑步动作。
- 夺尸距离与时间（1 m / 2 s）、处刑起手（1.5 s）、祈祷站位（1.3 m）都是初值。
- 地图小队岗位离玩家 20–200 yalms（默认 60），远处的哨兵不进地形高度网格，只靠 vnavmesh 贴地。
