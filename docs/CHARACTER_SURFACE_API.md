# 只读角色表面查询

当前公共实现为 `CharacterFluidSurface`，位于 `Effects/BodyFluids/Surface`。它读取角色可见模型、shape、骨骼和实际表面，查询本身不依赖口水库存或材质，不写游戏 pose、相机或碰撞状态。这个目录将在液体行为稳定后迁到通用几何层；现阶段保留公共接口，不复制一个口水专属 mesh 求解器给其他功能。

## 生命周期和坐标

Framework 调用 `UpdateActor(address, objectId, territory)` 采集合法身份和模型快照。管理式文件读取/解析以及邻接与分组在 worker 完成；worker 不接触原生地址。ready 数据必须通过签名核验后安装。`FaceOnlyCapture` 适合诊断/面部功能，`IncludeBodySurface=true` 请求完整可见角色模型。`Clear` 清理当前几何和身份，调用者同时处理自己拥有的效果库存。

当前 `CapturePose` 身份门槛只接受 LocalPlayer。公共几何查询可复用到本地角色其他模块，但尚不是任意敌人/角色服务；扩大 actor 范围前必须补充生命周期和占用身份验证，不能简单移除检查。

最终 pose 准备事件里调用 `CapturePose(delta)`，其成功后才使用 `HasSurface` 和查询结果。世界坐标使用米。`FluidSurfaceAnchor` 是 generation、triangle 和重心坐标构成的材料锚点，随皮肤动画移动；generation 变化后旧锚点不能继续查询。预算耗尽和 pose 不可用都不是无碰撞。

- `TryEvaluate(anchor, out sample)`：锚点当前世界位置、法线、表面速度。
- `TryContact(start,end,radius,...)`：扫掠接触，当前三个时间切片是近似移动三角形 CCD，尚不支持完整旋转和角/边球接触。
- `TryWalk(ref anchor, displacement,...)`：邻接三角形上的有界材料空间移动，未知边界停止，不跳到另一个身体部位。
- `TryGetAdjacentTriangle`、`GetAdjacentTriangles`：真实已验证邻接和有界局部 patch，不将靠得很近的两层皮肤当作连接。
- `TryGetDiagnosticTriangle`、`TryGetTriangleVertexIds`：只读三角形/顶点身份，适合贴肤膜、标记与实际表面诊断。

唇缘候选/验证是附加功能，普通身体接触查询不应依赖它。当前发射器需要经过实机红点确认的材料唇锚点，不能把 jaw/head 固定偏移当成嘴唇。

## 性能和未知状态

每 pose 蒙皮最多4096顶点、精确测试4096三角、cheap候选32768。当前和上一帧三角扫掠包围盒按帧复用，保持所有八项正权重影响。`BudgetExhausted`、`ContactPendingReason`、候选/蒙皮/测试计数可区分等待与真正无命中；消费者需保留库存/位移提案并延期，不能当作miss后穿过身体。

复杂mod shape优先级未知时拒绝冲突mesh，并报告 omitted。这不是完整身体支持的证明。UV接缝还没有焊接，未知边界封闭；最终shader/其他deformer可能改变GPU表面，CPU LBS需要画面验证。

## 与液体诊断的区别

`fluid surface` 是读取模型表面并显示局部线框和唇缘候选，线框抬高0.7mm方便检查。`fluid inspectnormal` 显示的是已经生成的实际液体面，绿色表示正向法线，不表示采样了哪些身体区域。普通湿膜抬高80µm并按真实厚度渲染，不能因为线框可见就断言湿膜最终可见。

2026-10-04 用户已经确认面部线框贴合、唇缘红点正确、实际液体诊断绿色可见。完整模型、贴肤移动、接触转移、mod变形和性能仍需实机验证。

## 身体模型变形缺口（2026-10-04 实机发现）

`fluid surfacebody` 是最多1024个三角形的分槽采样诊断，不是完整网格绘制；线框稀疏本身来自采样上限。但用户明确观察到身体线框不贴合，属于有效失败结果，不能以稀疏采样解释位置错误。

日志确认胸部、手、腿、脚读取的是当前 Penumbra 的 `C:/FFxivMods/hs-Rue.../c0201...mdl` 文件，脸部为 `c0801...fac.mdl`。因此“未读取 Bodymod”不是当前证据支持的结论。当前实现仅执行目标骨架的 inverse-bind / pose LBS，未执行跨种族模型的 PBD 预变形；这很可能解释身体偏移、脸部贴合的差异，但尚需修复后的实际画面对照来确认因果和剩余误差。

[Meddle 的模型构建](https://github.com/passivemodding/meddle/blob/main/Meddle/Meddle.Utils/MeshBuilder.cs)在目标骨架蒙皮前逐级执行按骨骼权重混合的 PBD 顶点变形；[其 PBD 格式实现](https://github.com/passivemodding/meddle/blob/main/Meddle/Meddle.Formats/Files/PbdFile.cs)给出了源/目标种族父链和按骨骼命名的矩阵。集成必须验证实际模型的源/目标身份及实际 PBD 资源，不能仅凭 mod 磁盘文件名推断，也不能调用有创建副作用的原生 deformer 函数作为诊断。

修复验收：先检查身体线框随当前外观及动画贴合，再验证落液接触和贴肤流动；确认前不把身体采样标为准确，也不通过增加液体厚度或绘制偏移掩盖错误。

18:25 第十批实机只读 metadata 采样已成功：Penumbra 反查原游戏 MDL 路径确认胸部、手、腿、脚及 `c0201` 身体连接模型的候选 source=201，SDK 当前 Human target=801；脸部与头发为801→801。当前对象 collection 解析出的实际 PBD 不是 vanilla，而是 Bodymod 内 `yet another skeleton/posing/chara/xls/bonedeformer/human.pbd`。已确认除了 MDL 还需读取替换 PBD。此批只新增 metadata 读取与日志，几何尚未校正；这不是身体贴合验证通过。

18:36—18:39 第十一、十二批完成候选诊断集成与 raw/corrected 实机对照：PBD 解析及主要身体201→801变形成功，相关顶点 PBD 拒绝数为0。同一视图的修正线框减少了躯干、手臂附近漂浮，而脸部保持801→801。`fluid surfacebody` 现在启用候选修正；`fluid surfacebodyraw` 保留原始采样用于对照。正常出液尚未启用默认 PBD 校正，完整动画、下肢及落液碰撞仍待验证。解析器和只读 metadata API 已单独提交 `aad0817`，诊断与液体重做的其余改动保留在工作区。
