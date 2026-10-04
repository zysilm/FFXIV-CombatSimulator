# 口水特效实现状态

2026-10-04。当前版本未达到用户的真实性要求，仅作为实验快照保存于 `experiment/body-fluid-rendering-prototype`。绘制位置、遮挡与多层 API 已验证，但不能视为高级口水功能完成。研究依据与分阶段门槛见 [完整方案](ADVANCED_BODY_FLUID_EFFECTS_PLAN.md)。

用户验收失败项：液体像水龙头般重复滴落；无可信黏滞/附着/皮肤缓流；嘴部来源不是可靠唇缘绑定；几何与材质过于简陋；缺少背景透镜折射、可信积液扩展及参数 Reset。下一阶段先重新研究表示方式、素材和渲染 API，再按可见结果逐项验收。新的连续供液与短桥排液代码未完成实机验收，不能据源码声称解决上述问题。

## 已实现的代码

- Effects tab 的 Saliva 控件：手动发射/停止/清理、可选 KO 触发、流量、皮肤滑动速度、细丝保持与长度、积液寿命、可见度、嘴部偏移。
- 单个本地角色：口部无额外喷射速度，继承口部移动速度；有限节点 XPBD 伸长、自然长度松弛、变细、断裂转滴；皮肤/地面接触转移同一份体积。
- 只读 CPU 表面：缓存实际模型路径、可见 submesh、参考逆矩阵与邻接；骨骼 bounds + 最多 128 面的 KD 叶筛选，局部按需蒙皮；预算耗尽延期处理。
- 三角面 anchor、切向重力与解析阻尼滑动、少量湿痕残留、向下表面的有限黏附与脱离、移动身体的近似接滴。
- 液滴和细丝端点/抽样内部节点的地面路径检测；小积液 patch 的边界探针、合并、坡面迁移、淡出与槽位复用清理。
- 轻量自建 D3D11 triangle batch：最多 32766 顶点，使用 scene reverse-Z depth SRV；无深度/无有效 target/尺寸异常时跳过。无额外 Framework.Tick、Scene.Update 或相机切换。
- 独立 D3D11.1 context state 保存/恢复游戏管线；私有状态清空，避免持有 backbuffer 影响 resize。依据 [SwapDeviceContextState](https://learn.microsoft.com/en-us/windows/win32/api/d3d11_1/nf-d3d11_1-id3d11devicecontext1-swapdevicecontextstate) 与 [CreateDeviceContextState](https://learn.microsoft.com/en-us/windows/win32/api/d3d11_1/nf-d3d11_1-id3d11device1-createdevicecontextstate)。
- 正常 MDL 解析失败时，通过新增只读适配桥调用现有 raw parser，并补读 submesh 属性；既有 ragdoll 求解和原解析路径未改。

## 固定容量和隔离

64 自由滴、128 表面沉积、8 条 12 节点细丝、16 积液；每 pose 最多 4096 蒙皮顶点/4096 三角形测试，每模拟步最多 32 地面查询/40 表面接触请求；60 Hz 固定步、最多 2 个追赶步。

预算不足时保留状态并延期，不视作“没有碰撞”，也不把有效 anchor 误判成失效。嘴部 reservoir、各状态与过期体积有独立账目，`status` 可输出误差。

新只读 `BoneTransformService.OnPosePrepared` 在现有所有骨骼修改回调之后运行；每次 Framework 更新只采样一次。效果关闭时不加载/扫描角色表面，不上传几何、不启用渲染 hooks。启用后卸载、切地图、logout、重绘会清理或使旧 anchor generation 失效。

DynamicPortrait 加载时暂停本模块：其 legacy 额外视图使用同一相机并增加原生 frame counter，现有接口不能可靠识别主视图。未改变 Portrait 配置或实现。这里是保守的暂时兼容限制，不能写成“已兼容多视图”。

## 已验证和未验证

已验证：Release 和 PUBLIC_BUILD 编译通过；源码与独立只读审查已修复 actor 身份检查、空 RTV 入队、积液旧边界复用、预算处理和姿态采样时间问题；游戏日志确认新版 DLL 正常加载。

实机接口采样（14:09:48–14:09:51，DynamicPortrait 已卸载）：120 帧全部取得主 target 与场景深度 SRV，跳过 0 帧；target 为 3840×2160，深度有效区域为 3744×2106、分配为 3840×2160，格式为 `FormatR24UnormX8Typeless`。这次没有实际绘制（drawn=0），因此不能证明深度遮挡或动态分辨率坐标正确。

首次 marker 实机绘制（14:16:16）未通过：`Draw` 获取 device 时抛出 `NullReferenceException`，模块停止绘制，用户未看到测试球。已将使用的 COM context 从 kernel `ImmediateContext` 内部缓存字段改为 `Device.D3D11DeviceContext`（现有 Portrait 使用的入口），添加空 vtable 检查，并从已经成功查询描述的深度 SRV 获取 device。14:17:45 修正 DLL 自动重载；实际绘制复测尚待完成。

修正后的 marker（14:18:30）用户报告没有异常，但仍看不到球。新增 12 秒分段诊断：屏幕空间无深度三角形、世界空间无深度球、世界空间深度球各 4 秒；日志记录每段首次实际 Draw 的顶点和 clip 坐标。渲染 pass 改为返回实际提交状态，空 RTV 不再误记为 drawn。只读审查未发现矩阵乘法/顶点布局的明显错误；后续 ToneAdjust 覆写为待实机排除的假设，资源非空不能证明当帧使用。

分段实机测试：用户看到屏幕三角形与三维位置正确的红绿蓝菱形（低面数球），但有深度/无深度阶段均没有人物遮挡；日志各段有真实 Draw，排除了本场景最终合成完全覆写。世界标记日志的 clip Z 始终为 0，W 正常随距离变化。下一步记录两份投影矩阵，并使用极小 GPU gather 读取 4 个深度值，非阻塞读回，区分退化投影与深度来源/采样问题。

14:40:49 的矩阵日志定位到明确的深度计算错误：`ProjectionMatrix` 与 `ProjectionMatrix2` 均为正常无限远 reverse-Z（M33=0、M34=-1、M43=0.1、M44=0）；`NearPlane=0.1`、`FiniteFarPlane=false`。Scene ViewMatrix 的 M14/M24/M34/M44 均为 0，Render ViewMatrix 对应列为极小垃圾浮点值。原来的直接 4×4 相乘使所有点的 clip.Z=0；WorldToScreen 只需 XYW，因此三维屏幕位置仍正确。修复仅对插件自有 view 副本设置末列 `(0,0,0,1)`，不写游戏相机；修正后的实际遮挡待复测。新增深度诊断在 marker 中提交一次 4-thread compute gather，读取 3 球对应 texel 与纹理中心，16 字节非阻塞 staging 读回；正常液体不触发诊断。

矩阵修正后实机遮挡通过：用户确认最后一段开启深度后人物能正确遮挡测试球。14:43:46 深度采样显示中间球投影深度 0.03842929，场景对应像素 0.03852904（reverse-Z 下场景更近），两侧场景像素约 0.0097（更远）；不是空深度 SRV。14:44:10 再次读回获得非零场景深度。确认本场景主视图投影和深度接入；未据此宣称所有 TAA、gamma、地图与多视图情形均通过。下一门槛为实际液体模拟与表面附着。

首次口水预览：用户确认液体可见、三维位置与遮挡正确，但极细、不明显。14:45:52 状态为 2 自由滴、2 细丝、1 积液，体积误差约 0，单次 pose 模拟耗时 0.043 ms；该值不是长期 p95，且首次加载表面出现约 65 ms Framework hitch。surface 为部分支持，未验证实际皮肤沉积。

根据用户复用要求，绘制层已提取到上层 `Rendering/WorldGeometry`：Plugin-owned 服务、最多 8 独立 producer layer、共享 hooks/COM/GPU 缓冲、按层清空和启停、默认 scene-depth 遮挡、可选 unlit 材质。口水通过注入该服务注册自己的 layer。通用 builder 提供三角形/椭球/细管/表面小片；[API 文档](WORLD_GEOMETRY_API.md) 含使用示例和限制。新增 Visual thickness（1–4 倍、默认 2 倍）只影响绘制半径，不改变流量、碰撞或体积计算。提取后的多层并存、关闭隔离与加粗效果待实机复测。

共享 API 的 Release 与 PUBLIC_BUILD 均编译通过，14:57:46 日志确认正常开发版 DLL 已在原 build 位置重载。源码检查补齐关闭 hooks 时清理未消费帧槽，避免反复启停后槽位耗尽；按层 generation 失效排队帧，关闭一个 layer 不会失效另一个 layer。实际多层验证使用 `/combatsim geometry marker` 独立预览层，不启动或清空口水模拟。

用户确认多层测试正常，但口水仍不明显，并指出固定频率弹跳、其他效果缺失；功能没有完成。已替换离散 0.02 ml/次短丝：单条口部丝持续接收 reservoir，参考长度随新增体积按 `ΔL=0.3ΔV/A` 增长，seed 长度与 seed 体积一致；保留原 XPBD compliance，使用仅承拉约束与重建速度后的轴向阻尼，去掉常规 1.2 秒计时断丝，8 秒只作寿命保险。模型逐 slot 拒绝原因日志已加入，未盲目取消 shape/attribute 过滤。连续供液版本已 build，但实机观察被 UI 异常打断，尚未验证效果。另已在源码加入身体接触后短桥向皮肤逐步排液的处理；该追加项尚未 build。

15:07 的实际报错来自 `MainWindow.DrawProfessional` 原有侧栏尺寸计算：可用宽度太小时 `Math.Clamp(sidebarWidth,80,totalWidth-150)` 区间反转（日志上限 -107）。HEAD 中存在同一计算。已增加窗口最小尺寸及空间不足保护，15:09:45 修正 DLL 正常加载。该日志没有证实液体开启时的 COM 释放故障；不能把报错归因于热重载或口水。为避免中途热重载打断观察，等待用户完成当前实机测试再 build 下一版。

尚未通过：实际液体的皮肤贴合、TAA/动态分辨率在其他设置下的对齐、非默认 gamma 链是否覆盖绘制、嘴部准确位置、倒地/抓取时视觉稳定性、CPU/GPU p95、持续运行及重载行为。代码中的限制不等于这些性能/兼容测试已通过。

当前模型局限：CPU LOD0，不接纳 active shape 替换或未映射 partial skeleton；未处理额外 LOD mesh 和 shader-only 变形；衣物也作为可接触外表面。UV seam 未焊接，开放边缘可能转成自由滴。嘴部 jaw/head 偏移是估算，需按角色校准。移动三角面 CCD 为三时间片近似，未实现完整球体 edge/corner 碰撞；丝内部仅轮换抽样节点，不能保证每条线段的精确碰撞。

基础材料是带透明度/高光的视觉近似，无场景折射、真实环境反射、完整透明排序/OIT。表面薄膜省略压力梯度，细丝是有限松弛近似而非完整黏弹本构。

## 游戏内验证顺序

1. 暂时在 Dalamud 禁用 DynamicPortrait，输入 `/combatsim fluid probe`。最多 120 次预 UI 阶段观察，10 秒超时；不发射液体。日志记录 target、depth、reverse-Z 和 ToneAdjust 信息；这只能证明接口可用。
2. 输入 `/combatsim fluid marker`，观察 12 秒：前 4 秒左上区域紫色屏幕空间三角形，中间 4 秒头部附近无遮挡红绿蓝球，最后 4 秒为正常遮挡球。保持角色不动，比较各阶段；不加载皮肤或运行液体模拟。
3. 标记通过后，在 Effects → Body fluids → Saliva 使用 Start preview。近景检查嘴部位置、墙/身体遮挡，再缓慢转头。Stop emission 让现有液体继续，Clear 立即清空。
4. 用倒下、翻身、抓取和接近地面的场景检查附着/接滴/地面；每次只改一项可见参数。
5. 输入 `/combatsim fluid status` 记录 poseFrames、液体数量、三角测试/蒙皮数量、budgetHit、当前 CPU 时间、体积误差和渲染状态。
6. 用 `/combatsim fluid off` 关闭，比较同场景帧表现；随后检查 resize/redraw/切图/重载。

当前原型按用户要求提交到独立实验分支，不 push、不修改 main；原 `feature/dynamic-camera-low-angle` 保持 `3c71a14`。此前攻击音效撤回和研究方案已在该 feature branch 推送。
