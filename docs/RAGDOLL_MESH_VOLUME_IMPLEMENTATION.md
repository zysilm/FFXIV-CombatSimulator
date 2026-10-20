# 渲染 mesh 身体碰撞实现记录

分支：`fix/ragdoll-mesh-volume`，基于 `feature/combat-stories` 的 `995fafe`。
实现日期：2026-10-14。未发布或合并。

## 当前行为

`RagdollMeshVolumes` 默认关闭，可在高级布娃娃设置的 **Rendered Mesh Body Collision** 中开启。解释与来源状态只放在悬停气泡中。
支持的身体区域使用当前加载模型的三角表面，直接构造 BEPU 动态 Mesh，不再把原始表面压缩为胶囊、盒子或单一凸包。
腰背等区域的凹陷因此保留；接触来自真实表面，不会由包围形状跨过凹陷产生支撑。

现有解剖关节轴、关节锚点、质量预算和骨骼写回继续使用。物理质量中心与骨骼原点可以偏离：
形状构建时只做一次 COM 重心化，保存完整中心偏移和旋转关系，并补偿从骨骼速度到 COM 速度的角速度分量。
形状不要求具有与身体相同的中轴。

原始三角形按插值权重边界分配到身体刚体；分区不新增外部封口，不用凸包填充凹陷。
早期版本按多骨贡献实时更新表面和 BVH，该路径已按用户要求停用：当前在选项开启且主角色模型就绪时捕获一次，将分区转换到骨骼局部坐标，保存在会话缓存。后续重复激活只复用固定形状和 LOD，不再蒙皮、refit 或因变形恢复完整面数。关节弯曲时会与渲染皮肤产生差异，这是用户接受的性能取舍。
根节点三轴缩放按渲染仿射顺序处理，支持非均匀缩放和反射；不是取最大轴拟合半径。

开放分区使用表面质量惯量，沿用原每段质量；不冒充闭合实体的体积密度积分。
运行中质量中心、惯量和局部三角形固定，BEPU 只移动/旋转各个刚体并计算碰撞。原生碰撞体由当前 simulation 创建/释放；长期缓存只持有托管几何和 LOD，不持有已释放 simulation 的池内存。

## 模型来源

- 从当前 CharacterBase 的加载模型与 ResourceHandle 出发，优先验证加载资源的完整字节；必要时读取对应磁盘文件或 SqPack。
- UTF-8 文件名、Penumbra 包装路径、可见 attribute 子网格和启用 shape 索引替换。
- 最多八个骨骼影响，保留每个影响的实际世界空间贡献。
- 附属骨架按已知连接点分别对齐参考/当前姿势，读取脸部和可确认的扩展骨骼；影响保留实际附属姿势贡献，再绑定到主骨架物理锚点。
- 使用公开解析器独立解析 PBD，按对象的 Penumbra collection 解析路径；不能确认的跨种族路由不使用未变形几何冒充成功。
- 人形捕获身体/装备 slots 0–4 和脸部 slot 11；独立头发和饰品不并入躯干碰撞。
- 不自动检测外观、模型、种族、shape 或缩放变化。关闭再开启选项或点击 `Refresh Mesh` 会替换缓存；说明只在气泡中。切换目标实体会分别捕获，避免把玩家形状套到另一个角色；其他控制器首次激活自己的目标时捕获。
- 保留骨骼数量改变后的停止检查，避免旧索引越界；这不会自动重新读取外观。手动刷新会先请求重建，支持新骨架。
- 来源诊断在捕获时写入日志；界面气泡显示实际接受的 mesh 与 fallback 刚体数量。

## 几何消费者

实际三角形供贴地最低点、最近表面、球体支撑、踩踏、抓取手指、头发接触、衣物碰撞镜像和调试线框共同使用。
真实 mesh 的查询不会再施加旧 profile 的径向缩小系数。
兼容 `TryGetBoneCapsule` 只提供包围数据，不是 mesh 的最终接触表面。
实验模块武器返回落点也改用三角表面查询；该改动位于私有子模块的 `Ragdoll.WeaponReturn.cs`。

球体支撑计算三角面内部以及边缘/顶点接触，替代少量射线采样。
模型双面三角碰撞避免导入绕序或反射造成单面接触失效，查询表面只保留一份。
睡眠时相同姿态与几何 revision 直接复用查询缓存。查询使用固定的缓存分区表面，而非实时变形后的渲染皮肤。

## 主要文件

| 文件 | 职责 |
| --- | --- |
| `Animation/RagdollController.RenderedGeometry.cs` | native 来源快照、资源身份与失效检测 |
| `Animation/RenderedGeometryDecoder.cs` | 可见三角、shape、顶点格式与蒙皮贡献 |
| `Animation/RagdollController.RenderedPartials.cs`、`RenderedPartialGeometry.cs` | 附属骨架连接与姿势对齐 |
| `Animation/RenderedRaceDeformation.cs` | 有界 PBD 解析和路由组合 |
| `Animation/MeshVolume/BodyMeshGeometry.cs` | 实际表面、权重分区、来源绑定、精确支撑 |
| `Animation/RagdollController.MeshVolume.cs` | 身体区域映射、COM 与动态 Mesh 创建 |
| `Animation/MeshVolume/MeshSkinDeformer.cs` | 多骨贡献及完整根缩放变换 |
| `Animation/MeshVolume/MeshSurfaceRefitter.cs` | 原位更新 BEPU 三角形和 BVH |
| `Animation/RagdollController.MeshDeformation.cs` | 一次性应用已缓存 LOD、性能日志；不再运行实时变形 |
| `Animation/RagdollController.MeshCache.cs` | 开启/启动预加载、手动失效、按目标隔离、缓存复用 |
| `Animation/MeshVolume/FrozenMeshGeometryCache.cs` | 无 native 所有权的骨骼局部几何和简化拓扑缓存 |

## 验证证据

以下实时变形测试和后续性能修复记录包含历史实现。当前默认采用固定缓存，上述“当前行为”与文末固定缓存验证优先；实时蒙皮辅助函数的回归仍保留。

- `Tests/RenderedGeometry`：**30 项检查通过**，覆盖可见性、shape、八权重、反射、异常输入、PBD 顺序、附属骨架连接和来源贡献。
- 通过 Lumina 离线读取已安装游戏的 **18 个真实 version 6 MDL、29,068 个三角形及 18 条 PBD 路由**。
  此过程发现并修复原版模型 Type5 byte 权重格式兼容缺口；未读取游戏进程数值。
- `Tests/MeshVolume`：**13 组检查通过**，生产源码直接链接；凹陷、偏心、双面 mesh/mesh 接触、表面分区守恒、支撑、查询缓存及实时蒙皮物理集成。
- 凹腰夹具落地骨骼高度约 **0.19977 m**，目标 **0.2 m**；0.25 m 高的中间凸起没有穿过凹陷托起身体。
- 20 秒双刚体实时蒙皮物理测试产生 **9.08 cm** 非刚性变形；共享边界误差约 **2.02e-7 m**，最大关节误差约 **3.65 mm**，最终落地中心高度 **0.1998 m**。
- `Tests/MeshDeformation`：非均匀/反射、多骨混合、共享分区边界、注册后 BVH 射线命中和无效更新保持原碰撞体。
- 相关头发、附件、衣物、装备脱落生命周期和武器释放回归通过，头发测试包含 **8,792 项断言**。
- 最终公共与带实验模块的 Release 构建均通过，分别有 9/10 条既有 obsolete API 警告，无错误。版本与更新日志一致性通过检查；Python 不可用，该项使用等价 PowerShell 检查。
- CI 包含八个独立离线检查项目，不依赖安装游戏或私有子模块；Windows 另跑 dense/spread、质量 0/100 四组实体地面压力测试，以及共享双 worker＋LOD，各 600 步、256 MiB 硬提交内存上限。

性能样本不是游戏帧耗时承诺：12,000 可见面/24,000 碰撞面的托管蒙皮与 refit 约 1–1.2 ms，预热后零托管分配；不含游戏渲染、接触求解和几何捕获。
极端多归属分区会扩展面数并产生激活分配；设置 100,000 源面、200,000 分区面预算，超限明确回退，不偷偷简化精度。

## 高面数卡顿与 OOM 修复

游戏日志中的实际负载为约 **73,475 个源三角面、32 个 mesh 刚体和 59 个其他刚体**；party 模式把求解配置提升到 8 次迭代、16 个内部子步。
OOM 栈位于 BEPU 的 `BatcherContinuations.CreateContinuation` / `CompoundPairCollisionTask.ExecuteBatch`：mesh 配对展开的三角形候选在接触过滤之前分配，源文件面数上限或事后的 child 过滤无法约束这些内存。
按相同数量构造的离线高面数夹具复现了单步碰撞约 1.5–4.6 秒，以及受保护进程中的 OOM。该夹具不是实际 mod 几何快照，不能将其耗时视作游戏 FPS。

`CollisionWorkBudget` 在分配 continuation 之前限制大型 mesh/mesh 配对的工作量。它从当前 refit 的 BVH 分布选取原始三角面接触候选，再交给 BEPU 原有三角形检测与 manifold reduction；所有碰撞三角形及精确表面查询仍完整保留。
大型配对按质量滑条使用 8–32 个 A 面区域、每区域最多 4 个 B 面；默认 50 对应 16 个区域。小 A mesh 分支最多 64 个区域。BVH 遍历也有固定节点/叶访问上限。
较小一方不超过 64 面且两边面数乘积不超过 65,536 时，直接使用原始完整检测：地面接触不能因采样缩减为不稳定的零散点。独立衣物/肢体模拟也注册同一保护。
这是**接触候选采样**，不是穷举所有三角形接触；复杂细碎特征仍可能遗漏接触，不应宣称完整碰撞结果与无限制原版完全一致。凹陷不会被凸包或圆筒填平；实际来源查询保持精确。

实验同时发现 `Mesh.ComputeBounds` 会遍历全部三角形。候选选择改读 BVH 根边界，避免每个刚体配对重复扫描高面数表面。
原顶点通过相同来源绑定精确共享，避免三角形汤导致重复蒙皮；不移动或合并不同几何位置。只在使用到的骨骼矩阵变化时重算蒙皮，局部顶点未变时跳过 native refit；查询复用局部 BVH，调试线框限制每帧绘制量。

渲染线程的补算增加 1/60 秒墙钟预算：完成一个固定物理步后，若已超预算，丢弃追赶积压，避免一个慢步引发同帧连续四个慢步。
求解固定步长及配置的内部子步不变；过载时会损失模拟时间，表现为物理变慢。该机制不保证单个步骤本身小于预算。超预算诊断最多每 5 秒记录一次耗时、实际面数、刚体数量和 native pool。

完整命令、失败对照、修复迭代、原始 JSONL 和最终统计见 [`Tests/MeshStress/README.md`](../Tests/MeshStress/README.md)。
最终串行任务的 dense/spread 各 600 步均通过 256 MiB 硬限制，峰值 RSS 约 99 MiB、最终表面贴地误差约 1.77/1.22 mm；清醒物理步中位数约 43.2/23.6 ms。额外 2/4 worker 对照只存在于离线测试工具，没有改变插件的线程所有权。
`Tests/BoundedMeshContacts` 另外验证双方均为高面数时的凹腰间隙、旋转、3 cm 细脚、表面 refit 及两种查询顺序；高度夹具约束角自由度以单独验证支撑，不能替代自由翻滚或实际 mod 的运动验证。
长测分别记录清醒与睡眠阶段，不能用睡眠耗时掩盖清醒性能；最拥挤夹具的清醒物理步仍可能达数十毫秒，不能宣称已达到 60 FPS。

## 实际限制

新增 CPU 多线程与碰撞 LOD 对照功能：`Parallel Mesh Physics` 默认开，在所有控制器之间共享双 worker，物理步同步完成后才执行蒙皮更新/游戏骨骼写回；最后一个引用释放时销毁线程及 worker 缓冲。普通步进及实验武器返回的两个半步都经过同一入口。可即时关闭对照，没有把物理异步延迟到下一游戏帧。

`Collision Mesh LOD` 为实验选项，默认关；`LOD Target Error` 默认 3 mm、范围 0.5–50 mm，下次激活生效。完整 mesh 用于 COM/惯量、查询及原始表面调试，碰撞 LOD 独立使用保留原顶点的简化拓扑。保留开放边界/锐利折线和顶点绑定；实时 refit 从完整顶点数组按索引选取，检测采样误差超标后恢复对应区域完整碰撞网格。目标误差属于有限采样，不是全局几何或全姿势严格上限。示例曲面/凹腰实测初始采样误差约 2.83/2.13 mm。

同面数夹具从 146,950 双面碰撞三角形降到约 23,330（完整查询仍保留原 73,475 面）。多线程在密集场景中位数 50.7→40.1 ms；展开场景变化很小。LOD 在部分场景有效，在较粗三角形引发更多重叠接触的场景也可能更慢，故保留默认关闭与独立对照。完整四组历史实验及最终 600 步记录见 MeshStress README，不能用减面比例推算 FPS。超预算日志现在分别记录 skin/refit、collision、solve、other、实际求解迭代/子步、worker 和碰撞/完整面数，便于核对设置是否真的生效。

游戏后续反馈为部分肢体穿地。初始全局抬升日志不能独立解释它：新的 mesh 专属诊断仅发现 12 mm 实际面穿透，而较大的 249 mm 请求来自其他包围体。复现进一步确认，落地后脚部蒙皮向下变形 80 mm，原来的无厚度地形停在骨骼 y≈0.206 m，未恢复到应有的 0.28 m。单纯放大预测接触距离或完整检测三角面仍失败。

当前 mesh 模式的 `TerrainCollisionSurface` 保留采样顶面、向下建立 0.75 m 实体地面，使用 native convex/mesh 完整检测，绕过身体之间的候选采样。矩形共面 patch 合并为一个平板或斜板；非共面 patch 按原三角顶面向下拉伸为棱柱。射线回归验证平面、斜面、非共面高度及缺面区域。80 mm 局部变形测试现恢复至 y=0.27993 m；66 mm 初始穿透在不启用启动修正的实体地面压力测试中也恢复至约 -1.36 mm。实体地面不是把顶面上移或放大角色碰撞体。

启动还会按真实 mesh 的穿透量统一移动刚体和关节锚点，最多 0.35 m；代理体包围尺寸仍由原 `RagdollGroundPenetrationLift` 控制。这不等于修改渲染 Y 偏移，或为所有 fallback 体强行开启原设置。复杂地形及实际 mod 的 Ragdoll Follow 交互仍需游戏内验证。

新增 `RagdollMeshContactQuality`（0–100，默认 50），GUI 开启 mesh 后显示 **Mesh Contact Quality** 滑条，立即生效，说明只在气泡中。它只改变身体间接触候选和搜索叶预算，不改变原 mesh、不跳过蒙皮，也不缩减地面接触。最低档仍有蒙皮、完整地面接触、关节求解的固定开销；更低质量可能漏掉身体间细小接触。同计数密集夹具的 0/100 档碰撞中位数约 22.8/48.2 ms，完整物理约 46.8/66.7 ms，不能据此承诺游戏帧率。

第一版 DLL 已按用户要求构建到自动加载目录；用户报告严重卡顿及 OOM。前述离线小样本没有覆盖该负载，不能作为完整碰撞性能验收。后续性能修复先输出到隔离目录，完成上述完整负载压力与回归后，再构建到 `CombatSimulator/bin/Release/CombatSimulator.dll`（含实验模块，0 错误、10 条既有警告）。尚未验证实际 mod collection 的最终 GPU 渲染结果或新版本游戏内帧率。
CPU 数据路径支持上述模型与变形，但不是最终 GPU 顶点回读；shader displacement、未知 native post-deformer 修改不能据此宣称精确匹配。
附属骨架仍要求连接数据可确认；额外骨骼的独立动画以及运行中软组织 scale 修改不等同于物理锚点驱动的表面更新。
不可解析的模型、骨骼绑定或 PBD 路由会记录原因并使用显式区域回退。
已有独立分离身体段的自建物理 rig，以及非人形通用布娃娃仍可使用原有形状；没有把这些特殊路径伪装成已完成 mesh 支持。
渲染已隐藏或删除的裸身体不能从外衣恢复；当前可见装备表面与裸身体表面不是同一份几何。
实际腰背因姿势形成的间隙可能合理，验收目标是消除虚假碰撞体造成的支撑。
## Small-tree refit regression (2026-10-14)

BEPU 2.5.0-beta.22's two-leaf `BinnedBuild` fast path does not initialize leaf backlinks. A single source triangle becomes two collision leaves after backface duplication; reading those uninitialized backlinks during refit could update the wrong root child or cause an access violation. `MeshSurfaceRefitter` now reconstructs both backlinks from the authoritative root children before dereferencing them, including reordered children. The regression deliberately poisons the leaf metadata. Before the fix, 6 of 25 fresh-process deformation runs failed; after the fix, all 50 passed. MeshVolume, MeshDeformation, MeshDispatcher, MeshLod and solid-ground BoundedMeshContacts checks also passed after this change.

## Background LOD activation

The first LOD implementation synchronously simplified every accepted region during activation. The 40 ms work limit was per region and excluded preparation/final validation, so 32 regions could block the host for over a second. `MeshLodBuildQueue` now serializes builds on background task continuations using private managed vertex/index/binding snapshots. Workers never access the simulation, game memory or native pools. Activation starts with complete colliders. The host consumes at most one result per 16.7 ms, gathers current endpoints, validates current deformation and switches native shapes outside worker steps while retaining the original mass frame/inertia. Disposal cancels queued work without waiting and prevents publication from an old generation. Initial full capture/collider creation and contact warmup still cost time; this change removes synchronous LOD construction, not every activation cost.

Actual game logs at 3 mm showed repeated deformation fallbacks and about 50,088/77,286 collision/full faces; collision detection consumed about 20–36 ms, versus skin/refit around 3–5 ms. Increasing tolerance can reduce fallback as well as permit more simplification. The GUI range is now 0.5–50 mm, default unchanged at 3 mm, next activation only. Higher values permit visible contour/support errors and do not guarantee a lower frame time. All explanations remain hover tooltips.

## 武器专用身体 mesh 接触（2026-10-20）

用户反馈：开启 `Rendered Mesh Body Collision` 后武器落到身上的效果明显变好，但整体 mesh 布娃娃仍有拖慢，默认不敢开。新增 `WeaponBodyMeshContacts`（默认开，Weapon Drop 区 **Weapons collide with body mesh**），与全身 mesh 开关解耦：布娃娃刚体保持原有体积，只有武器接触使用缓存的身体表面。

- 缓存：复用 `FrozenMeshGeometryCache`。主控制器在仅武器模式下只预载本地玩家；其他角色在其武器掉落时按需同步捕获一次（以地址＋EntityId＋DrawObject 为键，最多 8 个，失败结果也缓存避免重复卡顿）。仅武器模式固定生成 8 mm 碰撞 LOD（`targetFaceRatio .15`，后台构建）；全身 mesh 自身仍按原 LOD 设置决定是否使用。`Refresh Mesh` 同时清空两类缓存。
- 普通武器掉落（`WeaponDropController`）：有缓存时用每个身体区域一个运动学双面 mesh 替换原 18 根骨段胶囊，按游戏骨骼 ModelPose×角色缩放跟随；前臂/手区域沿用释放期接触屏蔽。存在 mesh 碰撞体时使用 2×1/120 s、4 子步和硬接触材质。无缓存（关闭、非人形、读取失败）回退原胶囊。
- 武器回落（实验模块，布娃娃 simulation 内）：武器注册后，为没有 mesh 的布娃娃刚体创建运动学 mesh 孪生体，每次步进前同步到骨骼位姿并带同样刚体速度；孪生体只与已注册武器接触，武器则跳过被替代的原刚体体积。孪生体质量无限，武器不会反推布娃娃（用户确认可接受）。最后一把武器移除时孪生体随之删除。
- `Tests/WeaponBodyMesh`（已加入 CI）：武器盒体在静止/移动、正反绕序的运动学身体壳上沉降，残余速度 <1 mm/s，稳定阶段竖向相对速度峰值约 2 mm/s，无下沉。尚未游戏内验证。
