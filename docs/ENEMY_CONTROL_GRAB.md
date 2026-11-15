# Enemy Control 抓取（grab）

状态：抓取本体已在用；“精确抓取点”（§3）2026-11-15 实现，待游戏内验证。本文件是抓取系统的唯一依据：怎么工作、代码在哪、界面和日志。Enemy Control 本身（接管谁、什么时候接管）见 [PROJECT_LOG.md](PROJECT_LOG.md) §2“敌人行为层 / Enemy Control”。

## 1. 它做什么

战败后玩家附身一个敌人（凶手或抢尸者）。按抓取键（Enemy Control 窗口的 Grab key，默认 Y；手柄 Cross）切换抓取：敌人用一个或多个“抓取对”抓住玩家的布娃娃尸体，走动时拖着走；再按一次放开。战国规则下抢尸者抢到尸体时也走这条路（`ICorpseGrab` → `ControlKiller` + `OnAttackInput`）。

一个**抓取对**（`EnemyControlGrabPairConfig`）= 敌人身上抓人的骨骼（`NpcBone`）+ 玩家身上被抓的骨骼（`PlayerBone`），可选再带两端的精确点（`NpcAnchor` / `PlayerAnchor`，§3）。任意多对可同时生效（一手掐脖子、一手托骨盆）。

## 2. 抓取本体（无精确点时，即 2026-11-15 之前的行为，保持不变）

代码：`CombatSimulator/Npcs/EnemyControlController.cs`（`StartGrabAttack`、`UpdatePreparedGrabTargets`、`ReleaseGrabAttack`、`CancelGrabAttack`）、`Npcs/EnemyControlGrabConformSolver.cs`；物理在库里（`Ragdoll.Grab.cs`）。

1. **配对来源**：`config.EnemyControlGrabPairs`；扩展接口 `ResolveExternalGripPairs` 可以整表替换。
2. **敌人端骨骼**：`ResolveGrabAttackBone` —— 配置的骨骼这只生物没有时，依次退到另一只手 `j_te_l`/`j_te_r`、头 `j_kao`、脖子 `j_kubi`、胸 `j_sebo_c`，都没有就取骨架最末一根。
3. **去重**：解析后的同一组（骨骼 + 精确点）只建一个约束（无手生物多对都退到同一根骨骼时，免得堆几十个相同约束）。
4. **握持体积**（`GrabConformSolver.GripAnchor`）：抓取瞬间从手指骨相对手骨的位置量出“握持体积中心”，存为手骨局部偏移，0.3 s 后抓取动画到位再量一次；目标点 = 手骨 + 偏移 × Grip depth（Fitting → Grip depth，默认 1.75）。没有手指的生物用骨骼原点。
5. **约束**：`playerRagdoll.CreateGrabConstraint(玩家骨骼, 目标点, …)`：库在承载该骨骼的刚体上（骨骼自己有刚体就是它，否则最近的有刚体的祖先，偏移到骨骼所在处）挂一个有限力伺服，0.18 s 拉入、限速、力下限 = 全身重量 + 余量；之后每次 `OnPosePrepared` 用 `UpdateGrabTarget` 跟上敌人的手。
6. **手指贴合**（`ConformSolver.OnRenderFrame`）：每个手指关节转向玩家身体表面（表面用 `TryGetCharacterSurface`：按种族/体型的轻量表面轮廓，退而用物理胶囊），每帧最多转 Finger max angle，强度 Finger strength。
7. **接触**：软接触（默认）时敌人的碰撞体全程保留，尸体靠在它身上；关闭时抓取期间把敌人的碰撞体停放。
8. **抓取动画**：Grab animation（默认内置抓握循环，或任选表情的循环动画），放开 1 s 后恢复原动画。

界面（Effects 页签 → Enemy Control 折叠栏 → 勾选 Show Enemy Control GUI，打开 “Enemy Control” 窗口）：Grab pairs 行、Any npc / Any player、Fitting 折叠栏（Grip depth、Skin gap）、Grab Profiles 折叠栏（保存/载入/覆盖/删除整套抓取设置，内置 Default 与 Embrace）。

## 3. 精确抓取点（anchor，2026-11-15）

用户裁决（2026-11-15）：①只在 3D 画面里点模型来选，不做部位列表；②档案里的敌人点所在骨骼在别的生物身上不存在时，退回手/后备骨骼链并丢掉偏移，记一行日志；③开始时两点相距远照样用现有的拉入（0.18 s、限速、力下限）；④第一版只约束位置，不对齐法线；⑤库 0.1.0-beta.4 提供 `CreateGrabConstraintAt`（内部 API，会被录制）。

### 3.1 存什么

`EnemyControlGrabAnchor`（`Npcs/EnemyControlGrabAnchor.cs`）：`Bone`（拥有那块皮肤的骨骼）、`Offset`（骨骼局部坐标，模型单位：拾取时除掉角色的绘制缩放）、`Normal`（骨骼局部的外法线）、`PickScale`（拾取时的缩放，只用于日志）。存成六个 float，配置里不出现运行时向量类型。抓取对的 `NpcAnchor` / `PlayerAnchor` 为 null = §2 的行为，完全不变；设了点会同时把 `NpcBone`/`PlayerBone` 设成点的骨骼。

坐标换算（`EnemyControlGrabAnchorMath`，纯数学，离线测试直接编译它）：世界 = 渲染骨架位置 + 旋转 × (缩放 ⊙ 模型坐标)，模型坐标 = 骨骼模型位姿 × 偏移。这和渲染几何快照（`FfxivMeshSource.BuildRenderedGeometrySnapshot`）乘顶点用的是同一个变换，所以点能精确落回拾取处。注意：§2 的骨骼读取（`BoneTransformService.GetBoneWorldPos/Transform`）不乘缩放；对缩放 ≠ 1 的生物两者不同，日志行 `GrabAnchor: pair N enemy scale … scaled vs unscaled … mm` 报这个差。

复制与去重（`EnemyControlGrabPairList`）：两个对的骨骼和两端点（骨骼 + 偏移到 0.1 mm）都相同才算重复；配置载入去重、档案保存（深拷贝）、档案载入（深拷贝 + 去重）、抓取开始时的去重都用它。

### 3.2 怎么选点

每个抓取对的两端各有一个 Pick 按钮（敌人端要先附身了敌人；玩家端要有本地角色）。按下后进入拾取模式：

- 一个透明的全屏 ImGui 窗口吞掉鼠标（点击不会落到游戏的选目标或镜头上），屏幕上方提示；Esc 取消。
- 左键点在模型上：取该角色**这一帧**的渲染几何快照（LOD0、装备槽 0–4 和 11 / 怪物全部，与网格体积布娃娃同一份采集），从镜头过鼠标的射线（`Camera/ScreenRay.cs`：以游戏自己的 `WorldToScreen` 为准，近、远两点各自迭代到投影落在鼠标上，残差写进日志）求最近的三角形；拥有骨骼 = 三个顶点的蒙皮权重按重心坐标加权求和后最大的骨骼；法线 = 三角形法线，朝向镜头；换算成该骨骼的局部坐标存下。
- 没点中就在聊天栏提示，保持拾取模式。
- 设好后该端显示 “`<骨骼>` · anchor set” 和 Clear；Enemy Control 窗口开着时，每个点在模型上画一个小圆点和对的序号（敌人端橙色、玩家端青色）。

没有调参控件（界面极简）。

### 3.3 抓取时

- **敌人端有点**：每次 `OnPosePrepared` 用当前骨骼位姿算出点的世界坐标，再沿法线外推 Skin gap（Fitting → Skin gap）作为目标；Grip depth 和握持体积不参与。手指贴合只在点的骨骼本身是“手”时才开（`ConformSolver.HasDigitHand`：2–8 个非 `n_` 子骨、每条都是不分叉且不超过 5 节的链、至少两条能弯），点在前臂上不会把手掰向身体。
- **敌人端点的骨骼在这只生物上不存在**：退回 §2 的后备骨骼链，丢掉偏移，日志 `GrabAnchor: pair N: this creature has no '…'`。
- **玩家端有点**：抓取开始时由当前（布娃娃）骨骼位姿算出点，再沿法线贴回身体当前的表面（`TryGetCharacterSurface`，承载该骨骼的刚体的表面；最多移动 5 cm × 缩放，超过就保留存下的深度并记日志），用来吸收脱衣、换种族带来的深度变化；然后 `CreateGrabConstraintAt(点的骨骼, 点, 目标, …)`：库把该点固定在承载刚体的局部坐标里，伺服拉的是这一点而不是骨骼，身体绕它悬挂、转动。其余（拉入、限速、力下限、更新、放开）与 §2 相同。手指贴合用承载刚体的骨骼名查表面。
- **点在被肢解的部分上**（点的骨骼或其祖先在 `DismemberPocBones` 里）：跳过这一对，日志 `GrabAnchor: pair N: '…' is on a severed part — pair skipped`。
- 读不到玩家端点时退回抓骨骼本身。

### 3.4 日志（`dalamud.log`）

- `GrabAnchor: picked '<bone>' on 0x… at (x,y,z), N mm from the bone (model units), scale S, hit D m along the ray, ray residual Rpx, round trip E mm, T triangles` —— 每次拾取。残差应在几个像素内，round trip ≈ 0。
- `GrabAnchor: pick missed …` / `GrabAnchor: no mesh for …` —— 没点中 / 读不到网格。
- `GrabAnchor: pair N enemy scale (…), scaled vs unscaled '<bone>' origin M mm` —— 每个带点的对开始时。
- `GrabAnchor: pair N start — enemy point on '…' / bone '…', body point on '…' (carried by '…', re-snapped X mm) / bone '…', finger conform on/off, start distance D mm`。
- `GrabAnchor: pair N after 1.0 s — gap G mm (start D mm)` —— 开始 1 s 后两点还差多少。
- `GrabAnchor: re-snap on '…' kept the stored depth (surface … mm away along the normal)`。

### 3.5 离线检查

- `Tests/GrabAnchor`（`dotnet run -c Release`）：骨骼局部 ↔ 世界往返（旋转 + 非均匀缩放，2000 组）、点随骨骼重摆、随缩放放大；合成网格上的射线–三角形与拥有骨骼；沿法线贴回；旧 JSON 读入后点为 null、档案往返保留点、深拷贝、带点的对不被去重吞掉。
- `Tests/RenderedGeometry` 带 sqpack 路径运行时追加跨种族检查：14 个种族的裸体（smallclothes 经种族变形）绑定姿势上拾取，换到别的种族解析，报到对方表面的距离（贴回前/后）。鲁加男、硌狮男、拉拉菲尔不参与（离线骨架与模型对不上，同 RagdollHullFit）。离线贴回的是真实网格；游戏里贴回的是表面轮廓。

## 4. 代码落点

| 文件 | 内容 |
| --- | --- |
| `Npcs/EnemyControlGrabPairConfig.cs` | 抓取对（骨骼 + 可选两端点） |
| `Npcs/EnemyControlGrabAnchor.cs` | 点的存储、纯数学（换算、拾取、拥有骨骼、贴回）、对的复制与去重 |
| `Npcs/EnemyControlGrabAnchorRuntime.cs` | 在活角色上解析点（`GrabAnchorSkeleton`）、点击拾取（`EnemyControlGrabAnchorPicker`） |
| `Camera/ScreenRay.cs` | 鼠标下的世界射线 |
| `Npcs/EnemyControlController.cs` | 抓取开始/每帧目标/放开、`GrabAnchor:` 日志 |
| `Npcs/EnemyControlGrabConformSolver.cs` | 握持体积、手指贴合、`HasDigitHand` |
| `Gui/MainWindow.EnemyControl.cs` | Enemy Control 窗口：抓取对行、Pick/Clear、拾取覆盖层、标记 |
| `Configuration.EnemyControl.cs` | 抓取设置、内置档案、配置去重 |
| 库 `Ragdoll.Grab.cs` | `CreateGrabConstraint` / `CreateGrabConstraintAt`、伺服 |
