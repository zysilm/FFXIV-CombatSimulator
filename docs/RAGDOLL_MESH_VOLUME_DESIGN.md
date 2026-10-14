# 布娃娃真实 mesh 体积：调查与实现设计

基线：`feature/combat-stories`，提交 `995fafe`；修复分支：`fix/ragdoll-mesh-volume`。
本文保留最初的静态代码调查和实现方案。后续运行时实现与离线验证结果见 [实现记录](RAGDOLL_MESH_VOLUME_IMPLEMENTATION.md)；未进行游戏内效果验证。

## 目标与边界

以当前角色实际呈现的模型为几何来源，包括 mod 替换、可见子网格、shape、骨骼变形和缩放；消除通用胶囊/盒子在腰背等区域产生的虚假支撑体积。
碰撞体中心、骨骼关节原点、mesh 几何中心和质量中心分别处理，不假设任何两者重合。

“读取了 mod 文件”不等于“重建了当前渲染表面”；“mesh 派生的凸包”也不等于“原始三角 mesh”。这两项精度必须分别验证、报告。
当前渲染模型可能由装备模型替代裸身体；已经隐藏/删除的裸身体几何无法从可见表面恢复。支持源应明确到 slot、mesh 和可见三角形，不能把装备外壳默认为皮肤，也不能凭空还原隐藏身体。

## 已确认的现状

| 代码位置 | 行为与限制 |
| --- | --- |
| `Animation/RagdollController.cs:4401` | 人形结构 mesh 拟合受 `RagdollCharacterSurfaceProfiles` 开关控制；默认开启，但关闭、通用骨架和不支持身份会回退。 |
| `Animation/RagdollController.StructuralMeshThickness.cs:118` | 遍历当前 `CharBase->Models`，取资源路径加载 MDL，然后按骨骼权重收集顶点。 |
| 同文件 `:261`、`:430` | 收集的是原始 bind-position，用参考骨架测量；没有在该路径应用完整实时蒙皮、可见三角索引、shape 或种族变形。 |
| 同文件 `:328` | 用加权分位数测横截面，以中位数确定横向中心；四肢收敛到圆截面，躯干到盒子。PCA 修正盒子的 roll，并没有保留表面形状。 |
| 同文件 `:591`、`:712` | 人体尺寸和左右对称检查可以拒绝拟合。合法但体型特殊的 mod 也可能回退，需要从日志确认，不能把正常人体比例当成几何正确性的充分条件。 |
| `Animation/RagdollController.SoftTissueMesh.cs:150` | 结构采样复用 `/obj/body/` 路径筛选。装备中的身体面和任意命名的 mod 路径可能不在覆盖范围。 |
| `Animation/RagdollController.cs:4442`、`:4672` | 结构结果最终创建 `Box` 或 `Capsule`，不是身体三角 mesh。 |
| `Animation/RagdollController.cs:1831`、`:8612` | 已有 `CapsuleToBoneOffset`、`ShapeCenterOffset` 和 `SegmentHalfLength`，骨骼写回会减去偏移；偏心补偿不是完全缺失。 |
| `Animation/RagdollController.cs:4895` 附近 | 关节锚点来自骨骼世界坐标，再转换到父/子刚体局部坐标，这个原则应保留。 |
| `Animation/RagdollController.cs:6020`、`:6132` | 现有 static/animated mesh 路径给外部角色创建 kinematic 碰撞体，并不替换布娃娃自身各个 dynamic 刚体。 |
| `Animation/RagdollController.cs:7358` | 外部角色的 `ConvexHull` 模式用骨骼位置建凸包，也不是真实 mesh 体积。 |
| `Animation/SurfaceProfiles/CharacterSurfaceRuntime.cs:145` | 表面缓存重新生成盒子或轮廓环代理；名称里的 mesh 不代表来自当前 MDL 的原始三角表面。 |

### 可复用的部分

- 公共 `ModelGeometryData` 和 `TryReadModelGeometryData` 已包含 submesh、shape 和 Dawntrail 变长骨骼表元数据，可作为统一解析起点。
- `BuildMdlMeshBoneMap`、顶点解析、`TryBuildSkinDeltas`、`SkinVertex` 可以复用思路。当前 `SkinVertex` 只处理四个影响，遇到扩展 mod 顶点格式须明确检查是否支持。
- `BoneTransformService.OnPosePrepared` 是插件骨骼修改后的只读观察点。它仍在原生提交前，不能据此声称已经观察到最终 GPU 变形结果。
- 现有关节、质量预算、物理步进和骨骼写回可以继续使用，避免同时重写运动规则。

### mod 与渲染一致性缺口

当前 loader 通过 `FileName.ToString()` 和 `GameData.GetFile` 重新取 MDL，没有核对读取字节与当前资源内容的对应关系，也没有这里显式处理 Penumbra 路径包装、UTF-8 文件名及磁盘文件版本。
这不能证明所有 mod 都读取失败，但不能保证读到当前显示的版本。

实验模块 `CharacterFluidSurface.Topology.cs` 已有 UTF-8 路径、包装路径剥离、attribute 可见性和 shape 索引替换；`ModelLoads.cs` 已有磁盘/SqPack 分流与资源身份缓存。
这些是参考实现，不能整体依赖私有实验模块：公共发布会排除它，且它带有单独许可条款；优先基于公共解析器独立实现公共捕获服务，必要的公共化另行明确来源与许可。

其种族变形实现明确标记 `NativeSlotChainVerified => false`，源种族有路径推断成分；不能把该实现视为“最终渲染 mesh 已精确复现”的证据。
原生 `ModelResourceHandle.ModelData` 也不是可直接拿来替代完整 MDL 文件的承诺，它指向模型元数据。顶点流和变形来源必须另行核实。

## 根因候选与先做的测量

现象可能同时来自盒子填平腰背曲面、胶囊的圆截面、模型源错误、bind/live 变形差异、非均匀缩放或拟合拒绝后的回退。当前没有复现场景，不能只断言中轴偏心。

在同一帧显示并保存以下数据：

1. 当前可见 mesh 三角表面与实际 BEPU 碰撞体线框。
2. 骨骼原点/解剖轴、碰撞体局部轴、质量中心及三者的偏移。
3. 每段 mesh 来源、资源版本、可见/隐藏面数量、shape/PBD 支持状态和回退原因。
4. 实际接触点、法线、地形高度、接触位置到真实表面的距离。
5. 是否仍受关节限位或抓取约束托住，避免把关节支撑误诊为碰撞体支撑。

## 实现管线

### 1. 公共渲染几何快照

新增公共 `RenderedCharacterGeometryCapture`（名称暂定），输出托管顶点、索引、完整骨骼影响以及 slot/资源/变形身份。

- 快照阶段读取 native 身份、可见性 mask、active shape、骨骼 palette/partial skeleton、当前变换和实际 scale。
- 路径按 UTF-8 读取；分别处理磁盘与 SqPack；优先校验实际加载资源，并记录无法建立一致性证明的情况。
- 只纳入当前可见索引引用的顶点，应用 shape 的索引替换；不能再扫描整个顶点缓冲并假设全部可见。
- 处理衣物/body 替换关系、额外骨骼和 partial skeleton；不要只靠文件名识别物理身体区域。
- 区分原始模型位置、种族/装备变形、实时蒙皮以及世界缩放。对应 native 变形链不明时明确降低支持等级，不能静默按原版几何处理。
- 先建立 CPU 重建与 native 渲染之间的可视验证。若无法重建必要变形，再调查经过变形的实际顶点数据捕获；不要假设现有 Hook 能直接提供 GPU 输出。
- worker 只处理托管快照；native 指针不传给后台。结果提交前核对 generation 和资源身份，重绘/换装后丢弃过期结果。

拓扑和资源解码可缓存；缓存键至少含资源/data 身份、磁盘版本或内容指纹、可见 mask、shape、骨骼绑定、变形身份和 scale。
姿态与拓扑分别更新。快照需要覆盖激活前外观和实际物理外观切换的时序，避免读取已经被死亡/装备效果替换的错误模型。

### 2. 身体分区保留三角拓扑

保留目前各个动态骨骼刚体和关节拓扑，把真实表面分到相应身体段。先覆盖骨盆、四段躯干和主要四肢，再扩展头、手足和软组织。

- 用权重、骨骼邻接和空间边界共同分区，不能只按最大权重随意丢弃跨关节三角形。
- 跨关节区域使用边界裁剪/共享边界策略，验证没有大面积重叠和漏面；若需要封口，封口限定在内部切面，不能成为外部虚假支撑面。
- 真实渲染 mesh 是蒙皮表面，一个顶点可随多个刚体变化。固定到一个刚体的局部 mesh 只能保证捕获姿态；关节转动后不一定继续一致。
- 因此保留原始蒙皮表面用于每帧比较，量化肩、髋、腰等混合权重区域的误差。需要动态变形碰撞时另设局部更新路径，不把一次捕获当成永久精确。

### 3. 碰撞表示的选择

用户要求的最终验收目标是真实 mesh，不应把“一个凸包替代一个胶囊”称为完成。

| 表示 | 优点 | 本问题的限制 |
| --- | --- | --- |
| 每段动态三角 `Mesh` | 保留原始表面和凹陷，最接近逐三角目标 | mesh/地形及 mesh/mesh 接触要验证单面、薄面、退化三角、穿透和性能；开放表面不能当闭合实体算惯量。 |
| 每段单一 `ConvexHull` | 实体接触、质量中心和惯量较容易处理 | 会填平腰背凹陷，不能作为本问题的最终方案。 |
| mesh 分解的多个 hull + `Compound` | 可形成实体，保留更多局部形状并控制成本 | 是有误差的近似；需要自适应分解与误差上限，必须显式报告精度。 |

建议先在离线原型中比较动态三角 mesh 与真实 mesh 分解 compound 的接触结果，优先把“保留凹陷的真实表面碰撞”做成可运行基准。
若动态 mesh 不能满足稳定性或预算，compound 作为工程候选，不默认获得“逐三角精确”的验收。
分解不能简单按全段建 hull；腰背、腋下和胯部的凹陷需要额外拆分，按表面误差而不是固定数量决定停止。

本地 BEPU 2.5.0-beta.22 的 XML 提供 `Mesh.ComputeClosedInertia`、`ComputeOpenInertia` 和 compound 中心计算：闭合实体惯量与开放三角表面惯量含义不同。
最新官方源码注册了 mesh/mesh 碰撞，但这不替代对本项目 beta 版本的运行验证。
官方也说明移动三角网格有单面薄三角接触和成本问题，因此不能把替换 shape 构造函数视为充分实现。
参考：[BEPU 默认碰撞注册](https://raw.githubusercontent.com/bepu/bepuphysics2/master/BepuPhysics/DefaultTypes.cs)、[BEPU 形状性能说明](https://docs.bepuphysics.com/PerformanceTips.html)。

### 4. 完整骨骼—刚体映射

不需要强行令物理主轴等于身体主轴。解剖轴用于关节运动，几何主轴用于形状/惯量，两者用显式变换关联。
保留解剖轴构造刚体 frame 可以减少对现有关节的影响；mesh 顶点在该 frame 内仍可任意偏心。

记捕获时骨骼 world frame 为 `(p_b, R_b)`，刚体质量中心 frame 为 `(p_c, R_c)`。
用与当前 `Vector3.Transform` 一致的实际旋转运算保存：

```
d = Transform(p_c - p_b, Inverse(R_c))
q = Inverse(R_c) * R_b
v_body = Transform(v_world - p_c, Inverse(R_c))

运行时：
p_b = p_c - Transform(d, R_c)
R_b = R_c * q
v_world = p_c + Transform(v_body, R_c)
```

其中 `d` 必须包含全部中心偏移，不能再重复扣除 segment midpoint。新路径可令 `SegmentHalfLength = 0`，保留旧路径兼容；之后逐步统一为 BodyToBone 映射。
逆变换和 quaternion 组合顺序必须用 round-trip 测试确认，不仅靠命名推导。

非均匀 scale 必须先完整应用到 mesh/骨骼捕获数据，再建立刚体局部顶点；不允许用一个标量半径代替 X/Y/Z，也不允许把 scale 夹进只保存 rotation 的映射后丢掉。
反射/负 scale 要检查绕序和法线。关节锚点仍用骨骼原点转换到真实 COM frame，不能用几何中心作为关节位置。
COM 取决于质量分布，不一定等于表面顶点平均值。闭合分区可按体积算；开放区必须有明确质量模型，保留既有每段质量预算并报告该近似。
BEPU 对 hull/compound 返回的 recenter 偏移只能应用一次。

### 5. 所有几何消费者同步升级

新路径不仅创建 shape，还要保存统一的运行时局部几何和 shape 身份，提供实际表面的 bounds、最低点、ray/nearest/sphere 查询。

- `BodyBottom` 和初始化 `requiredRigLift`：对实际顶点/shape support 求最低高度，不能走胶囊公式；检查地形，而不仅是平面。
- `CharacterSurfaceRuntime`：接受实际碰撞几何，停止重建圆环或盒子作为新路径的真实表面。
- `TryGetWalkableSurfaceHeight`、NPC 踩踏/穿越、EnemyControl 抓取 conform、分离附件接触：迁移到统一表面查询。
- `TryGetBoneCapsule` 可保留作兼容包围查询，但调用者必须明确它只是 broad-phase bounds，不能决定最终接触/支撑。
- 调试叠层显示真正使用的三角 mesh/hull compound，同时显示原始渲染 mesh 与误差。
- shape 更新时同步惯量、bounds、sleep/wake、query cache 和资源释放；销毁 compound 时核对 child shapes 的所有权。
- 不在每个物理子步重建整个人物 mesh；先分析固定分区误差，再只更新确实变化的区域和 native shape acceleration 数据。

## 交付顺序

1. **几何来源验证**：公共捕获服务和叠层；在无 mod、身体 mod、换装/shape、非均匀 scale 下验证表面一致性。
2. **局部物理原型**：先让骨盆/腰背实际表面参与落地，验证偏心变换、COM、凹陷和原有地形接触。先测试当前 beta 的 mesh/mesh 注册及表现。
3. **完整刚体集成**：保留关节运动规则，替换结构碰撞；接入统一表面查询、抓取与贴地。
4. **跨关节与生命周期**：验证蒙皮混合区域，按误差决定局部动态更新；处理重绘、装备变化、资源撤销和异步结果失效。
5. **性能及回归**：多角色/多尸体睡眠、分解缓存、激活尖峰、分配与清理；公共构建独立通过。

首阶段失败或不支持的输入应输出具体原因并显式回退，不显示“mesh 精确模式”已成功。

## 验证标准

离线测试：偏心/倾斜不对称 mesh 的骨骼 round-trip；四元数组合；非均匀 scale 和反射；shape 索引替换；隐藏 submesh；跨关节边界；开放/闭合几何质量模型；真实最低点；资源过期结果丢弃。

物理测试：带腰部凹陷的体段在平面及真实三角地形上仰卧、俯卧、侧卧；比较胶囊、单 hull、compound 和动态 mesh 的虚假支撑/穿透。
必须断言没有由 hull 跨凹陷、内部封口、重复体积或错误法线引起的接触。

游戏内：原版、实际安装的身体 mod、非 ASCII mod 路径、跨种族装备、shape 切换、重绘、不同体型/scale、抓取、布娃娃重新激活和多尸体。
测量接触点到实际表面的偏差及局部表面到地形的距离；腰部出现空隙本身不必然错误，真实人体姿势也可能保留腰背间隙，验收针对虚假物理支撑。

精度阈值在第一阶段实测后固定，初始可用毫米级误差作为目标，但不能在没有渲染对照数据时声称已经达到。
本次没有运行这些测试：没有运行时代码改动，也没有游戏复现场景。
