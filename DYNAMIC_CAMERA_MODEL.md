# Dynamic Camera — 动态相机算法设计

状态:**v2 已实现**(`Camera/DynamicCameraSolver.cs` + `Camera/DynamicCameraController.cs`,
GUI 在 Professional 窗口的独立「Dynamic Cam」页)。

> ⚠️ **本文档 §6 及其后的「三平面闭式解 / 俯仰下限」全部作废**,是 v1 的错误模型。
> 正确模型见下面的「v2 修正」。§1–§5(目标、GoW 镜头语言调研、基建盘点、DOF 所有权)仍然有效。

---

## v2 修正(实测驱动,以代码为准)

### 1. DirV 符号:v1 整个搞反了(相机穿地仰望的直接原因)

v1 的 `Basis` 假设 **DirV 正 = 相机在轨道中心上方俯视**。**反了。** 证据:

- FFXIV 的俯仰限位不对称——`DirVMin ≈ −1.48`(85°)、`DirVMax ≈ +0.79`(45°)。游戏里你可以
  近乎垂直俯视角色、却只能约 45° 仰视,**幅度大的那端(负值端)才是「相机在上」**。
- 症状逐字吻合:v1 写入 +0.20~0.35(自以为温和俯视),游戏把相机沉到 pivot 下方
  `d·sin(0.2~0.35)`——正好钻进地里仰望;死亡态默认开的碰撞补丁又移除了最后的阻拦。

修正:`Basis` 里 `toCamera.Y = −sin(dirV)`。控制器内部一律用「俯角 χ 为正」的人类直觉空间,
只在 Submit 与读回边界处取负。**DirH 部分是对的**(经 FightingKO 的 `atan2(side.X, side.Z)`
交叉验证,且 `right = cross(UnitY, fwd)` 在 Y 翻转下不变——这也解释了为什么 v1 战斗态的左右
构图方向是对的)。

### 2. 三平面闭式解是错误的参数化(符号修好也治不了「没有倒地感」)

v1 把**构图当硬约束**(凶手头顶钉上边线、尸体远端钉下边线)、把**相机位置当自由变量**,
3×3 解出的相机数学上正确、摄影上任意:没有任何东西把它约束在地面附近的人类尺度高度,
而且「双主体撑满全帧」根本不是倒地特写的构图。

**v2 = 趴地摄影师模型**(`GroundedFit`):相机**位置被约束**、**构图是涌现结果**——

- 相机高度 = **地面射线探测**(`BGCollisionModule.RaycastMaterialFilter`,同
  `Simulation/TerrainHeightService.cs` 的调用)+ 高度滑条,EMA 平滑;
- 相机躺在从尸体胸锚沿 yaw 方向后退的水平射线上,**唯一未知量是后退标距 t**;
- t 从「特写距离」起,只在凶手装不下时才增大(几何扫描 + 二分,~24 次投影,便宜);
- **倒地感来自尸体近、凶手远的纵深差**,不是全帧拉伸。

「俯视一些就得更高更远」自动成立:χ 加大 → 凶手头顶相对视轴更高 → 拟合自己把 t 推远。

### 3. 松弛阶梯砍掉了「借偏航」那一级

v1 有一级「向双主体角平分线偏航」。**它从来没生效过**:求解在 `playerYaw + bias` 下拟合,
但我们(按设计)从不写 DirH,游戏仍在 `playerYaw` 渲染——拟合与实际画面不一致。
v2 直接删掉:后退标距本身就压缩横向角展,这一级是冗余的,而且它与「永不夺走玩家相机」相悖。
现存四级:`原镜头 → 加宽镜头 → 凶手只保头+躯干 → 放弃凶手(纯尸体特写)`。

### 4. 弹簧作用在**相机位置**而非 pivot

v1 分别弹簧 pivot 和 distance,而游戏重建的相机 = `pivot − fwd·distance`——两条弹簧不同步时
重建出的相机会偏离解算点,**而「偏离」包含「掉到地面以下」**。v2 直接持有并弹簧相机位置,
提交前 `curCam.Y = max(curCam.Y, ground + 0.15)` 硬夹一道,再反推
`pivot = curCam + fwd·distance`。**「相机不可能入地」因此是构造性保证,逐帧成立、过渡期也成立。**

### 5. 战斗态两处构图修正

- **拉近瞄点上移**:v1 整体覆盖轨道中心,把游戏原生「拉近时瞄点上移到头」的行为也覆盖没了,
  于是 zoom in 后镜头对着腰腹、头出框。v2 按距离把锚点从胸椎向头骨 lerp。
- **侧偏改屏幕占比语义**:固定世界侧偏(yalm)在近距离对应的屏幕偏移会放大到把角色挤出画面。
  v2 用 `offWorld = frac · d · tan(hFov/2)`,屏幕上的偏移量在整个 zoom 范围内恒定。

### 6. 凶手不需要新建追踪

`CombatEngine.LastPlayerKillerAddress` 早已存在;只补了 `LastPlayerKillerEntityId` 以便
地址失效时经 NpcSelector 重解析。

---

## v2.1:视图重建改用矩阵真值(实测仍不重合后的最终形态)

v2 修好了 DirV 符号推断,但 overlay 实测显示求解器重建的投影(品红)与游戏自己的
WorldToScreen(绿)**完全不重合**——说明"从轨道角重建当前视图"这条路里还有约定猜错了
(FoV 字段的光学含义?游戏附加的 tilt 偏移?)。与其找出是哪一个,v2.1 把猜测从架构里
整体移除:

1. **当前视图 = 矩阵真值**(`GameCameraView`):前向量取自 SceneCamera 的 ViewMatrix
   (行/列与符号歧义在运行时用「相机→LookAt 方向」消解,四个候选取最贴合者);镜头参数
   直接读投影矩阵——`tanHalf = 1/P.M11, 1/P.M22`,这正是 WorldToScreen 自己乘的矩阵。
   轨道角(DirH/DirV/FoV 字段)从此不再参与任何投影计算。
2. **规划视图用求解器自有的 (ψ, χ) 约定**,与 Project 自洽;规划 FoV 的镜头由
   「当前 FoV 字段值 → 实测镜头」的比例缩放得出(`Lens.ScaledToFov`),FoV 字段是竖直角、
   水平角还是别的什么,被比例吸收,无需知道。
3. **DirV 写入改为带符号自锁的反馈**:每帧向"当前信念方向"步进(限速 3 rad/s),
   下一帧用实测俯仰核对;若持续反向移动则翻转信念并记日志。信念错误从"埋进地下的 bug"
   降格为"前几帧多走半度后自愈"。玩家拖动的意图读回也经同一信念换算。
4. overlay 的品红点现在投影在实测视图上,**绿/品红必须重合**(验证纯数学);
   约定问题改由读数回答:`chi(real) vs ±DirV`、`tanHalfV vs tan(fov/2)` 两行数字
   一眼定谳游戏的真实约定。

## v2.2:实测诊断定谳的三件事(绿/品红重合后的第一份数据)

overlay 重合后,第一份实测 dump 直接揭示:

1. **视线俯仰 = −DirV + 偏移(~0.23 rad)**——游戏的瞄点在轨道中心之上,纯符号翻转永远不对,
   反馈环是唯一正确的写法(它已自行收敛)。
2. **轨道方向 ≠ 视线反方向**(差的正是那个偏移)。v2.1 用视线方向做 pivot 分解,游戏实际把
   相机放低了 ~0.3y——穿透了我们自以为有的地面保证。修复:**轨道方向也实测**——上帧提交的
   pivot 与本帧实测相机位置之差就是游戏当前的轨道方向,用它分解,一帧收敛。
3. **俯仰读回会把"游戏自己动 DirV"误判成玩家拖动**(偏好从 +0.01 漂到 −0.18)。修复:读回
   门控在 `rotateHold > 0`(检测到真实输入)上。

另:**相机高度改为派生量**。"身体在画面中的位置"(band,默认 −0.45)+ 角度 + 标距闭式反解出
相机高度——倒地感(身体在下带、占屏大)从此是构图目标的直接结果,不再依赖手调一个恰好合适的
高度。fov 滑条对反填(min>max)做了防御(实测就是这么填的,基础镜头被钳在 1.2 超广角,主体全变小)。

## 贯穿两版的教训:写回值必须先被游戏的限位夹一遍

玩家意图 = 「游戏报告的值 − 我们上一帧写入的值」。如果我们写入一个游戏会夹掉的值
(Distance < `MinDistance`,或 DirV 出 `DirVMin/Max` band),那个夹取动作在下一帧与
「玩家动了相机」**完全无法区分**——相机会把自己的写入误判成玩家输入,永久让出控制权。
见 `ClampToGameDistance` / `ClampToGamePitch`。

## 1. 目标与非目标

**目标**
- 战斗态:模拟 God of War Ragnarök 的镜头语言 —— 角色偏于画面一侧(越肩构图)、
  身体大部分可见且占据较大画面比例、群战时自动留出空间不显拥挤。
- 死亡态:相机在可配置时长内 translate 到一个构图:己方尸体默认全身入镜、位于画面
  下部;**击倒玩家的那个敌人从头到脚完整入镜**(硬约束);通过滑条控制尸体入镜比例
  (1.0=全身 → 0.25=头+胸)与俯仰角下限(防严重仰视看不到地面)。
- **全程不锁玩家相机输入**:玩家的旋转/缩放随时生效,系统自适应重解构图。

**非目标**
- 不做镜头切换(cut)——所有过渡都是连续 translate(GoW 的 one-shot 原则,也是
  现有代码的既有风格)。
- 不接管 Fighting/Monster/UserActiveCam 模式(它们在 coordinator 里优先级更高)。
- 不做屏外敌人指示器(TelegraphOverlay 已覆盖威胁提示职能)。

## 2. GoW Ragnarök 镜头语言要点(调研结论)

1. **一镜到底**:全程无 cut,镜头移动全部是连续运动 → 我们的过渡只用 translate + 弹簧。
2. **近距越肩**:Kratos 锚定在画面偏离中心的一侧(约 1/3 线),画面占比大;
   代价是群战被普遍批评"幽闭"、看不到侧后方敌人 → 我们默认比 GoW 略宽,并加
   "拥挤度拉远"补偿。
3. **锁定/辅助构图是软性的**:Lock-On 及 Priority+ 设置会在挥击/招架时向最近敌人
   重定向,但摇杆输入永远即时生效 → 我们的横向辅助只做限速小偏置,输入一来立即让位。
4. **战斗中镜头略微拉远**,处决/踩踏时推近(本设计不做推近,留作演进)。

## 3. 现有基建盘点(全部复用,不新造轮子)

| 原语 | 位置 | 用途 |
|------|------|------|
| 轨道中心替换(玩家保留旋转/缩放) | `ActiveCameraController.GetCameraPositionDetour` | **不锁输入的根本机制**:我们只换 pivot,DirH/DirV/Distance 仍归游戏+玩家 |
| 单写入权仲裁 | `CameraModeCoordinator`(OrbitCenter/DirH/DirV/Distance/MaxDistanceAtLeast) | 新增 owner 提交请求 |
| FoV/倾角写入 + 限位加宽/恢复 | `DeathCamController.ApplyLens` / `EnsureCameraLimits` | 死亡态 FoV 调整 |
| 相机碰撞禁用补丁 | DeathCam/ActiveCam 共用 Cammy 签名 | 死亡态低机位防撞地形 |
| smoothstep 定时过渡 | `DeathCamController.Tick(Interpolating)` | 死亡 translate 的时间轴 |
| 双主体中点取景 + 间距定距 + translate 相位机 | `FightingModeController.UpdateCamera/UpdateKoCamera` | 死亡态求解器的雏形参照 |
| 骨骼世界坐标(任意角色) | `ActiveCameraController.GetBoneWorldPosition(bone, addr)` | 主体锚点采样 |
| 布娃娃刚体位置(尸体被踢走也跟得上) | `RagdollController.GetBodyWorldPosition` | 尸体锚点必须走这里,骨骼 pose 停在死亡点 |
| 命中震屏叠加层 | `DeathCamController.ApplyHitShake` | 与动态相机天然共存 |

## 4. 核心架构:自由度所有权(DOF ownership)

这是整个设计的地基。FFXIV 相机 = pivot + (DirH, DirV, Distance, FoV) 的轨道模型。
"不锁玩家输入"不等于"什么都不写",而是**把自由度分给两个主人,互不抢夺**:

| 自由度 | 战斗态 | 死亡态 |
|--------|--------|--------|
| DirH(偏航) | 玩家(可选软辅助:限速偏置,输入即让) | 玩家 + 求解器小偏置(≤12°,限速,输入即冻结) |
| DirV(俯仰) | 玩家 | 求解器写,但玩家垂直输入被**读回**为偏好调整(见 §6.5) |
| Distance | 玩家(占比自适应只在输入静默期缓推) | 求解器给下限,玩家只能拉远不能破坏构图地拉近 |
| Pivot(轨道中心) | 控制器(胸骨 + 视角系侧偏) | 控制器(求解输出) |
| FoV | 基本不动(可选微调) | 求解器,[f_min, f_max] 内 |

**输入检测与让位**:每帧读 `gameCam->InputDeltaH/V` 及 Distance 与上帧写入值之差。
非零 → 开一个"玩家意图窗口"(1.5–3s):窗口内冻结所有软偏置与自适应,只维持硬约束
所需的自由度(pivot/FoV)。绝不调用 `ClearInput*`(现有 DeathCam 锁输入的做法在
动态相机路径上**明确弃用**)。

## 5. 战斗态算法(GoW framing)

每帧(玩家存活、sim active、无更高优先级 owner):

1. **焦点 F**:锁定目标(PlayerTargetController)> 最近交战敌人 > 交战敌人质心。
2. **选边**:F 在玩家的屏幕投影侧的**反侧**放角色(角色在左 1/3,敌人占右侧空间)。
   带迟滞:仅当 F 持续 0.8s 在另一侧才翻转;翻转用慢弹簧(ω≈2)过渡侧偏量。
3. **Pivot** = 玩家胸骨(j_sebo_b,可配)+ 视角系侧偏(复用 ActiveCameraSideOffset
   的实现,但符号动态、量值弹簧化,默认 ~0.45m)+ 高度偏置。侧偏是视角相对的,
   所以玩家任意旋转下越肩构图恒成立——这是它优于改写 DirH 的原因。
4. **画面占比 → 距离下限**:角色角高 β = 2·atan(h_char / 2d),占比 = β / vFoV。
   目标占比滑条(默认 0.55,区间 [0.35, 0.75])反解 d*。只通过
   `MaxDistanceAtLeast` + 输入静默期的缓弹簧 Distance 写入去接近 d*;玩家一滚轮,
   暂停 3s 并**把玩家选的距离反解为新的占比偏好**(适应玩家,而不是拗回去)。
5. **拥挤度拉远**:交战敌人对相机的水平角展 σ 超过 0.7·hFoV 时,距离下限乘
   (1 + k·超出量),k 小、上限 clamp——只补 GoW 的幽闭短板,不做成自动导演。
6. **软偏航辅助(默认关或极弱)**:玩家输入静默 >2s 且 F 超出 |x_ndc|>0.6 时,
   DirH 以 ≤30°/s 限速向包含 F 的方向读-改-写(在玩家当前值上加小增量,非绝对写),
   输入一来立即归零。

## 6. 死亡态算法(双主体构图求解器)

### 6.1 输入

- 尸体锚点链:头/胸/髋/膝/足,从 **RagdollController 刚体**采样(EMA τ≈0.15s 抑制
  落地尖峰)。躺地的尸体"头到脚"基本是水平展开的——这是它和站立凶手在约束形态上
  的本质区别。
- 凶手 K:CombatEngine 需新增"最后伤害来源"记录(见 §7),每帧刷新其位置;
  身高 h_K = 头骨骼世界 Y − 脚(GameObject.Position.Y),非人形骨名探测失败时
  fallback ≈ 2.0m × ModelScale。凶手是活的、会走动 → 求解器每帧连续重解。
- 玩家当前 DirH(偏航基准)、纵横比(Device/ImGui DisplaySize)。

### 6.2 约束(硬→软)

- **H1** 凶手全身入镜:头顶+边距、脚+边距都在安全框内(边距 6% 画幅)。
- **H2** 尸体入镜比例 s(滑条 0.25–1.0):沿尸体锚点链按弧长累计取前 s 段,
  所有必需点入镜;构图上尸体胸锚点落在下带 y_ndc ∈ [−0.8, −0.5](随 s 调整)。
- **S1** 俯仰角:θ ∈ [θ_min(滑条,保底俯视量,保证地面可见), θ_max];
  θ_pref 由滑条+玩家读回偏好决定。
- **S2** FoV ∈ [f_min, f_max],偏好 f_pref(默认=原生 FoV)。
- **S3** 偏航偏置 |Δψ| ≤ 12°,速率 ≤20°/s,玩家转动时冻结并缓慢衰减回 0。
- **S4** d ≤ d_max(凶手跑远时的爆炸保护)。

### 6.3 求解:定向后线性定位(核心洞察)

pivot 是我们的(三维!),所以**给定朝向和 FoV 后,相机位置是线性可解的**,
不需要非线性优化:

1. 定朝向:ψ = 玩家 DirH + Δψ,θ = clamp(θ_pref, θ_min, θ_max),f = f_pref。
2. 取两个绑定锚点:顶 = 凶手头顶(目标 y_K ≈ +0.7),底 = 尸体必需段远端
   (目标 y_C ≈ −0.75)。在视空间中,对点 Xᵢ 与目标 NDC yᵢ:
   `up·(Xᵢ−Cam) = yᵢ·tan(f/2)·fwd·(Xᵢ−Cam)`
   两点 → 关于 Cam 的 2×2 线性方程组(前向/上向分量),闭式解;
   侧向分量由"双主体水平重心落在目标 x 构图"再解一条线性方程。
3. **可行性检查与松弛阶梯**(逐级,每级带迟滞防抖):
   a. 其余必需点越出安全框 → FoV 加宽(≤f_max);
   b. 仍违约 / θ 被滑条钳死导致无解 → **固定 θ=θ_min,把高度和距离作为未知重解**
      (2 未知 2 边界约束,仍闭式)——这正是"要俯视就得又高又远"的数学体现;
   c. 水平溢出 → 先 Δψ 向双主体角平分线偏置(≤12°),再增 d;
   d. d 触顶 d_max(凶手跑远)→ 凶手约束降级:全身→头+躯干→放弃凶手,
      回退纯尸体构图(s 约束保持,尸体居中下带)。
4. 分解回轨道参数:任选 d(取 Cam 到双主体加权中点的距离,让玩家滚轮语义自然),
   pivot = Cam + dir(ψ,θ)·d,连同 DirV=θ、Distance=d、FoV=f 提交 coordinator。

每帧全量重解(尸体滑动、凶手走动、玩家转视角都自然吸收);解出的目标状态不直接写,
而是被弹簧追踪(§8)。

### 6.4 Translate 时间轴

死亡瞬间捕获当前相机状态为起点;可配置时长 T(默认 2s):
`state(t) = lerp(起点, 求解目标(t), smoothstep(t/T))`
——与现有 DeathCam 插值同构,区别仅在**目标是逐帧重解的动点**。t ≥ T 后转入纯弹簧
跟随。复活/重置 → Release owner,游戏相机自然恢复(DeathCam.Deactivate 先例)。

### 6.5 死亡态的输入自适应(替代锁输入)

- **偏航**:从不绝对写 DirH。玩家转到哪,求解器就以哪为基准重解——主体永远在框内,
  因为 pivot/距离/FoV 归我们。
- **俯仰**:每帧写 DirV,但先读回 `观测DirV − 上帧写入值` 作为玩家垂直意图,
  累进到 θ_pref 并 clamp 到 [θ_min, θ_max] → 玩家上下拖动变成"调整偏好",
  相机重解位置维持约束,体感是"能调但永远不破构图"。
- **缩放**:同法读回滚轮意图为倍率 μ ∈ [1.0, 2.5],沿视线从锚点中区后撤
  (拉远永远安全,角度只会变小);μ<1(往里滚)被可行性地板吸收。

## 7. 接线与新增件

- **新 owner**:`CameraOwner.DynamicCam = 15`(压过 DeathCam=10,让位于
  MonsterFollow/Fighting*/UserActiveCam)。一个 owner 两个内部人格(Combat/Death)。
- **CameraRequest 扩展**:加 `float? Fov`,coordinator 内做 save/restore
  (照抄 MaxDistanceAtLeast 的模式)。DirV/Distance/OrbitCenter 已有。
  绝不置 ClearInput 标志。
- **凶手记录**:CombatEngine 在对玩家结算伤害处记录 lastPlayerDamageSource
  (entityId+address),暴露 `KillerAddress`;死亡回调时冻结快照。凶手中途死亡/
  消失 → §6.3d 降级路径。
- **抑制旧 DeathCam**:config.DynamicDeathFraming 开启时不调用
  deathCamController.Activate()(优先级虽已压制,但避免两个插值器同时跑)。
  `SuppressDeathCam` 委托链已有先例(FightingMode)。
- **与 EnableActiveCamera 共存**:用户显式 Active Cam 开启时,动态相机整体不提交
  (尊重用户显式选择;否则 coordinator 的 orbit-center 级联会让我们的 pivot 借道生效,
  造成"关不掉"的观感)。
- **Tick 顺序**:`dynamicCameraController.Tick(dt)` 插在 fightingModeController.Tick
  之后、`cameraModeCoordinator.Apply(dt)` 之前(OnFrameworkUpdate 现有序)。
  读到的 combatEngine 状态晚一帧,可接受。
- **orbit 钩子供电**:coordinator.WantsOrbitHook → activeCameraController.SetModeActive
  已在 Plugin.cs 接好,pivot 提交即生效,零新钩子。
- **Config 新增**:总开关、死亡构图开关、身体比例 s、θ_min/θ_pref、T、占比目标、
  侧偏量、软偏航辅助强度(默认 0)、f_min/f_max、d_max、尸体下带位置。
- **调试 overlay**:安全框矩形 + 主体锚点 WorldToScreen 投影 + 松弛阶梯当前级别
  (RagdollDebugOverlay/FightingDebugOverlay 先例)。

## 8. 数值与稳定性

- 所有目标量走临界阻尼弹簧,分频:pivot ω≈6、距离 ω≈3、俯仰 ω≈3、FoV ω≈2、
  侧偏翻转 ω≈2。离散决策(选边、松弛级别)一律迟滞 + 驻留计时(0.5–1s)。
- 尸体锚点 EMA;硬着陆速度尖峰 clamp(HardLanding 事件已有,可顺手抑制那一帧)。
- 相机限位:进入死亡态用 EnsureCameraLimits 加宽 DirV/FoV/Distance 限位,退出恢复
  (DeathCam 模式,原样复用)。碰撞补丁在死亡态按 config 启用。
- 求解退化:双主体几乎重合(凶手站在尸体上)→ 单主体构图;2×2 方程行列式过小
  (两锚点视线近平行)→ 回退上帧解 + 增 d。

## 9. 边界情况

- 复活/Reset/换区/登出:Release owner + 恢复限位/补丁,挂进现有清理路径。
- 凶手被队友击杀后仍在(尸体):照常入镜(尸体也有骨骼);despawn → 降级。
- 多敌补刀:凶手 = 最后一次对玩家的伤害来源,死亡时冻结,不追新仇。
- 玩家死于 Fighting/Monster 模式:它们的 KO 相机优先级更高,动态相机自动让位。

## 10. 实现分期

1. **P1 基建**:owner 枚举 + CameraRequest.Fov + 凶手记录 + 控制器骨架 + 调试 overlay。
2. **P2 战斗态**:侧偏 pivot + 占比距离 + 拥挤拉远 + 弹簧/迟滞(不含偏航辅助)。
3. **P3 死亡态**:双锚线性求解 + 松弛阶梯 + translate 时间轴 + 三根滑条。
4. **P4 自适应打磨**:俯仰/缩放读回学习、软偏航辅助、FoV 呼吸、参数调校。

每期独立可验证:P2 站在训练木桩前就能肉眼验构图;P3 用 /combatsim 死一次即验。
