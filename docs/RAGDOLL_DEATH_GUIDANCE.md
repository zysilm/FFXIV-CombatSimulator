# 死亡引导（Death guidance）

状态：库 AdvancedRagdoll 0.1.0-beta.5 实现（`PoseHold` 组件），插件 2026-11-22 接入设置、迁移和界面；**还没有在游戏里看过**（看什么见 §6）。默认值是 Limp（和以前一样的布娃娃），用户在游戏里认可之前不改默认。

要解决的问题（用户，2026-11-22）：死亡动画摆好的姿势，物理一接手就“化掉”——97 份录制里物理启动时 70 份侧躺、15 份站着，交接后身体平均还要动 3.4 s，至少 14 份从侧躺滚成俯卧或仰卧。

## 1. 界面与设置

- Ragdoll 页签 → 折叠栏 **Ragdoll Trigger** → 下拉 **Death guidance**（在 “Ragdoll starts” 和 “Activation Delay (s)” 下面）：Auto / Limp / Knee buckle / Hold the legs / Hold the pose / Slow slump。玩家、队友、敌人用同一个设置。
- 旧的折叠栏 “Guided Collapse”（勾选 “Knee buckle on death”、两条黄字提示、“Start at once” 按钮）已删除。“Ragdoll starts” 和延迟滑条不变。
- 设置 `Configuration.RagdollDeathGuidance`（枚举 `RagdollDeathGuidance`，存成数字，不带 `$type`；`CombatSimulator/Animation/RagdollDeathGuidance.cs`，下拉的顺序、标签、帮助文字也在这个文件）。默认 Limp；“Reset All to Defaults” 会把它设回 Limp。
- 迁移（`Configuration.MigrateRagdollDeathGuidance`，在清退役键之前跑）：旧对象 `GuidedCollapse` 的 `Enabled` 为 true → Knee buckle，false → Limp；之后 `GuidedCollapse` 进退役键列表被清掉。用户保存的值是 false → Limp。
- 交给库（`Animation/Hosting/ConfigRagdollSettings.cs`）：`GuidedCollapseOptions.TensionProfile` = 对应的 `RagdollTensionProfile`；`GuidedCollapseOptions.Enabled` 恒为 false。`Enabled` 是库里旧的膝盖跪下开关，激活时不看姿势就预约；Knee buckle 档自己在物理启动时启动同一套跪下，而且只在站着时启动，两个都开会启动两次。
- 库在物理启动那一刻读一次设置（设置每帧开头刷新）；物理已经启动的身体不受之后改下拉的影响。

## 2. 各档做什么

机制（库 `Ragdoll.PoseHold.cs`，细节见库的 `docs/ARCHITECTURE.md` §8 “Death guidance”）：每个关节本来就有一个目标角速度为 0、力矩有上限的马达。引导把被保持的关节的上限调高、软度调到 0，于是关节保持当前角度，超过上限就滑动并停在滑到的位置——是摩擦，不是弹簧，不会把姿势拉回去。上限的单位是“这个关节远端那一串肢体的重力矩”。

| 档 | 保持什么 | 什么时候起作用 |
|---|---|---|
| Limp | 什么都不保持，原来的布娃娃 | 任何时候 |
| Knee buckle | 原有的“膝盖先软、身体向前折”（入场调节 + 膝部失力），数值没动 | 只在物理启动时身体还站着；否则等同 Limp |
| Hold the legs | 髋、膝、踝保持角度（×1.5；残留值现在也是 ×1.5，即不衰减）；交接时贴着地面的小腿和脚再被“按”在原地 | 跪、蹲、坐、躺；站着时不起作用 |
| Hold the pose | 腿 + 脊柱保持角度（手臂、头、颈不保持） | 同上；站着时不起作用 |
| Slow slump | 全身先保持（×3），再按顺序放开：手臂和头 0.4 s、躯干 1.0 s、腿 1.8 s | 任何时候；保留交接时的速度 |
| Auto | 库按物理启动时的状态选：站着 → Knee buckle；正在倒（骨盆速度 > 0.5 m/s，或躯干已倒但髋还高）→ Slow slump；跪 / 蹲 / 躺 → Hold the legs；生成骨架（非人形）→ Hold the pose | — |

起始状态的判定（库 `RagdollStartState.Measure`，阈值与死亡镜头的姿势分类相同）：躯干与竖直夹角 53° 以内算直立；髋高于 0.60 m × 体型比例算“高”。

**什么时候放开**（各档相同）：
- 被抓起、或被站立支撑接管：立刻全部结束。
- 被打、被踢、被摇、被人踩过：0.3 s 内全部放开，之后是普通布娃娃。
- 某个关节一直在滑：这个关节自己放开；骨盆在起始过渡后还快速移动（整具身体像一块板一样倒）：全部放开。
- 落地冲击：相关关节照旧短暂放松，然后回到引导的值（不是回到原始马达）。
- 没人碰它，Hold the legs / Hold the pose 的残留值和地面保持一直留着。

## 3. 与原设计的四处偏离（库实现时按离线结果改的）

1. **保持到被碰为止**，不是按时间放完：保持撤掉时，其实没躺稳的身体会再塌一次，成了死后很久的第二次动作。所以残留值和地面保持不撤，只在被碰时放开。
2. **地面保持用有上限的速度马达**（小腿和脚各一个线速度、一个角速度马达，目标 0，力的上限 0.5 × 体重）：同样是对世界的摩擦，不是把小腿钉死的约束。设计里“先只保持关节角度（a），离线不够再加小腿固定（b）”——离线显示只有（a）不够，加的是这种带上限的（b）。
3. **“支撑侧取较重一边”这个单位关掉了**（`PoseHoldTuning.SupportingSideHeavier = false`）：开着时蹲着的身体腿撑着躯干，像雕像一样一直站着。
4. **两个 Hold 档在站着启动时不起作用**：站着的身体不该被扶住，同样会变成雕像。站着只有 Knee buckle 和 Slow slump 会做事。

## 4. 离线结果（2026-11-22，库 0.1.0-beta.5 的 nuget 包）

工具：`Tests/RagdollReplay --what-if <档> --start-at <0|50|100>`（从录制里取角色和死亡动画，在动画的 0 / 50 / 100 % 处启动物理，跑 12 s）+ `Tests/DeathShots guidance`（按起始状态分组出表）。97 份录制 × 3 个启动点 × 6 档 = 1746 次，全部跑完无失败。起始状态分布：0 % 处 74 站 / 23 跪；50 % 处 22 站 / 17 正在倒 / 8 躺 / 50 跪；100 %（录制自己的启动点）处 17 站 / 4 正在倒 / 75 躺 / 1 跪。库的起始状态判定与工具一致 285 / 291。

门槛（“抖”的度量）：动能尖峰 ≥ 5 次的次数、以及静止后又动起来的次数，都不能比 Limp 多。

| 起始状态（每档次数） | 档 | 结果 | 门槛 |
|---|---|---|---|
| 躺（83） | Hold the legs | 腿部关节平均转动 11.5°（Limp 19.9°）；33 次腿型保持到最后（Limp 0）；从侧躺滚成俯 / 仰 7 次（Limp 11）；全身漂移 p90 0.28 m（Limp 0.46 m）；4 s 后的最大动能 0.53 J（Limp 16.6 J）；尖峰 ≥ 5 次的 2 次（Limp 2） | 过 |
| 躺（83） | Hold the pose | 脊柱转动 2.5°（Limp 12.2°）；滚成俯 / 仰 7 次；但 4 s 后仍有动能尖峰的 30 次（Limp 2）、漂移 p90 0.47 m | 过（按门槛；后期小抖多） |
| 躺（83） | Slow slump | 与 Limp 接近（滚成俯 / 仰 10 次） | 过 |
| 跪 / 蹲（74） | Hold the legs | 小腿留在原地 41 次（Limp 5）、小腿转动 9°（Limp 48°）；但尖峰 ≥ 5 次的 12 次（Limp 2）、静止后又动 2 次；结束时俯卧 37 次（Limp 8）——身体向前折过去 | **不过** |
| 跪 / 蹲（74） | Hold the pose | 小腿留不住（3 次）；尖峰 ≥ 5 次的 6 次、又动 3 次 | **不过** |
| 跪 / 蹲（74） | Slow slump | 尖峰 ≥ 5 次的 0 次（Limp 2）；结束时仍是跪姿 15 次（Limp 40） | 过 |
| 正在倒（21） | Hold the legs | 尖峰均值 0.24（Limp 0.90）；倒向与交接速度夹角 34°（Limp 21°，方向保持得差一些） | 过 |
| 正在倒（21） | Slow slump | 倒向夹角 19.5°（Limp 21.1°，保住了方向）；尖峰 ≥ 5 次的 1 次（Limp 0）、1 次没静止 | **不过**（差 1 次） |
| 站（113） | Hold the legs / Hold the pose | 与 Limp 相同（不起作用） | 过 |
| 站（113） | Knee buckle | 结束时俯卧 43 次（Limp 7，确实向前折）；尖峰 ≥ 5 次的 14 次（Limp 4），单次最多 76 个尖峰；穿地最深 36.6 cm（Limp 22.6 cm） | **不过**（数值没动，与以前一样抖） |
| 站（113） | Slow slump | 0.5 s 时髋关节转动 25°（Limp 69°，确实慢）；尖峰 ≥ 5 次的 7 次（Limp 4） | **不过** |
| 各状态 | Auto | 等于它选的那一档：躺过，站（Knee buckle）、正在倒（Slow slump）、跪（Hold the legs）不过 | 4 类里 3 类不过 |

完整的表：按上面两条命令重跑即可（约 15 分钟）；本次的输出在 `%TEMP%\dg-p2\guidance-table.txt`（临时目录，不保证留着）。

**限度**：
- 这是同条件下各档的对照，不是对游戏的复现：重新模拟不还原别的角色的碰撞体、抓取、头发、网格体积；与录制本身相比骨盆终点中位误差 9 cm（`Tests/RagdollReplay/README.md`）。
- 离线指标证明机制在工作、哪里在抖，**证明不了好不好看**（地面贴合那次五轮指标全过、游戏里被否决）。所以默认是 Limp，Auto 没有当默认。
- 数值（`PoseHoldTuning`）还是起点，P3 用同一套工具调；跪姿的抖和前折、Knee buckle 的抖是已知要调的。

## 5. 代码落点

- 插件：`Animation/RagdollDeathGuidance.cs`（枚举、下拉、帮助文字、到库枚举的映射、旧开关的映射）；`Configuration.cs`（属性、`MigrateRagdollDeathGuidance`、退役键 `GuidedCollapse`）；`Animation/Hosting/ConfigRagdollSettings.cs`（交给库）；`Gui/MainWindow.cs` `DrawRagdollSection`（下拉）。
- 库（`C:\PROJECT\AdvancedRagdoll`）：`Ragdoll/Ragdoll.PoseHold.cs`（`PoseHold`、`PoseHoldTuning`、`RagdollStartState`、`BeginConfiguredPoseHold`）；`Hosting/HostSettings.cs`（`GuidedCollapseOptions.TensionProfile`、`RagdollTensionProfile`）。
- 录制：`TensionProfile` 为 None 时不写进录制的设置，所以旧录制照常逐位回放；选了别的档的新录制要用 beta.5 以上回放。
- 离线：`Tests/RagdollReplay` 的 `--what-if`（`WhatIf.cs`）和 `--config-migration`（`ConfigMigrationCheck.cs`：用 Dalamud 的读法读真实的 `Configuration`，核对迁移、重置、交给库的值；可再给一个保存的配置文件，只读）；`Tests/DeathShots` 的 `guidance` / `guidance-sheets`。

## 6. 游戏里怎么判断

日志（`dalamud.log`）：选了 Limp 以外的档，每具身体物理启动时库写一行

```
Ragdoll: guidance <设置> -> <实际的档> (start <Standing|Kneeling|Falling|Lying>, hips 0.15 m, scale 1.38, pelvis 0.00 m/s): 8 joint(s) held, 4 body(ies) held to the ground
```

实际的档是 `None` 表示这次没做事（Hold 档遇到站着的身体、Knee buckle 遇到没站着的身体）；Knee buckle 的行没有冒号后面那段。选 Limp 时没有这一行。

用户现在的设置是固定延迟 3.7 s，物理启动时多数已经躺下，所以先看 **Hold the legs**：
- 躺下的尸体：腿型是不是保住了、是不是不再慢慢滚成俯卧 / 仰卧；有没有比 Limp 更抖。
- 死亡动画以跪 / 蹲结束的（或把 “Ragdoll starts” 调早）：小腿是否留在原地；身体向前折、抖得是否难看（离线在这里不过门槛）。
- 走过去踩、打、踢：0.3 s 内松开，之后和普通布娃娃一样；Enemy Control 抓起：立刻松开，抓取手感和以前一样。
- 很久以后有没有“第二次塌下去”。
- Auto：看日志里它给每具身体选了什么；延迟调到 0 时站着的身体走 Knee buckle。
- Knee buckle：延迟 0 才有意义（与以前的 “Knee buckle on death” + “Start at once” 相同）；延迟 3.7 s 时日志是 `-> None`。
- 敌人和队友的尸体同样生效（他人尸体默认是轻量物理）；多具尸体时看 `FrameCost:` 有没有变化。
