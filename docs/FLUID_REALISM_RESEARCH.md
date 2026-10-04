# Saliva 基本真实性研究与验收路径

研究日期：2026-10-04。范围：只读当前实现和 primary sources；不 build、不修改实现、不创建独立模拟或测试。以下区分源码事实、研究依据、建议和未证实假设。现有游戏内深度遮挡通过，不代表皮肤表面、口唇定位或液体行为通过。

## 1. 结论与停止线

继续调整当前单股丝的长度、阻尼或 mouth offset，无法补齐用户否决的效果。第一阻塞是**液体使用的实际面部 surface 是否存在且与最终渲染一致**；第二阻塞是**液体源绑定真实下唇/口角而不是 jaw/head 的估算点**。这两项未通过前不应重新发射完整效果要求用户试参数。

在当前 CPU 条件下可行的方向，是局部、按需蒙皮的 material-space surface + 守恒湿膜/流痕 + 体积珠 + 少量可变截面细丝 + 局部地面摊。不是全身每帧 Bake，不是全域体积 SPH/FLIP。必须承认这是有物理约束的 reduced model，不能宣称完整真实流体。

## 2. 当前源码的实际缺口

| 用户看到的失败 | 已核实的实现原因/限制 | 必须解决的结果 |
|---|---|---|
| 源点不贴唇 | `CharacterFluidSurface.CapturePose` 用 `j_ago` 的 `(0,.004,.025)` 或 `j_kao` 的 `(0,-.052,.075)` 加用户 offset；不是 lip vertex/triangle | 下唇和口角的稳定物质点绑定，动画后重评估；offset 不作为正确性的替代 |
| 没有实际 skin flow | `TryGetSkeletonFromCharBase` 只返回 `PartialSkeletons[0]`；`ReadMesh` 骨名映射只查这个 skeleton；任何非零 shape mask 整 slot 被省略 | 面部实际 slot、partial、权重骨、active shape 都有明确映射；先证明 face/chin surface 命中 |
| 只有零散颗粒/短痕 | Deposit 是单个 triangle+barycentric 点；trail 每 .06s 留下离散体积，4s 消失；不存在连续膜厚度场 | 具有宽度、厚度、体积和连续接触足迹的 surface film/rivulet |
| 丝像水龙头/绳子 | emission 把 reservoir 直接持续供给第一条 Fed filament；12 节点 stretch-only XPBD；参考长度均匀增长；没有沿丝截面质量输运/毛细压力/黏弹状态 | 唇部蓄液、润湿、珠滴/桥/丝转换；截面随局部伸长变细；停止供液后排液、颈缩、断裂 |
| 短丝弹跳 | 约束 compliance 和 rest-length relaxation 不是黏性或黏弹本构；velocity damping 只能掩盖弹性回弹 | 拉伸应力随应变率/松弛演化，材料点不以弹性绳返回固定 rest shape |
| 低模，像蓝色塑料 | builder 固定8边；ellipsoid 只有上下两扇；每段 tube 独立取frame；bead 是平面上8边尖顶小片；shader 固定方向 light+specular+alpha | 曲线/截面连续、曲面贴附、膜与珠的不同视觉、相机相关细分和稳定抗锯齿 |
| 地面摊不 spread | 半径直接由 `sqrt(V/(pi*50µm))` 得出，仅新体积/地形采样时更新；8径向采样点；无接触线运动、厚度分布或边界输运 | 固定体积也能随时间增大足迹并降低高度，到润湿平衡/边界后停止 |
| 一到某处就脱皮落下 | adjacency 仅相同vertex ID，UV/normal split会变成开放边；向下normal+固定.22s后peel | 分类真实边界、UV seam与被隐藏面，pinning与detachment按力/体积/接触条件 |
| 调完回不去默认 | GUI没有reset动作；Clear只清液体，Clamp只限幅，不还原saved params | 集中默认值、一键reset并保存、清除runtime状态；参数版本迁移需明确 |

关键文件：`Effects/BodyFluids/Surface/CharacterFluidSurface*.cs`、`FluidModelData.cs`、`Animation/BoneTransformService.cs`、`Animation/RagdollController.FluidModelData.cs`、`Effects/BodyFluids/BodyFluidController.cs`、`Rendering/WorldGeometry/WorldGeometryBuilder.cs`、`WorldTrianglePass.cs`、`Configuration.BodyFluids.cs`、`Gui/MainWindow.BodyFluids.cs`。

## 3. Surface 与 lip docking：先证明，再模拟

### 3.1 Partial pose 与实际渲染蒙皮

保留资源 identity/mask/generation invalidation 和按需顶点缓存。新 surface reader 应只读地枚举 `Skeleton.PartialSkeletons` 的有效 pose，记录 `(partial index, skeleton identity, bone index)`，而不是把重名骨合并为一个字符串索引。FFXIVClientStructs 的 `PartialSkeleton` 有 connection indices；`Model` 还有自己的 `Skeleton`、`BoneList` 和 `BoneCount`。这些是追踪实际骨palette的线索，`void** BoneList` 的语义不能凭名字猜，也不能直接强转后认为完成。

路径：先导出每slot的实际resource path、material、mesh/submesh、权重骨名与被解析到的partial；核实脸上哪些骨来自何处，再核对游戏实际 palette/变换。如果多个partial有相同骨名，通过所属model/connection和实际渲染比较消除歧义；不要默认先找到的同名骨正确。每个partial独立重建reference model transform和inverse bind，并验证其坐标空间：partial的ModelPose是否已包含连接/缩放关系，不能再盲目乘连接变换，否则double transform。读取最终动画/插件pose之后的快照，不调用会写游戏pose的同步路径来掩盖读取时序问题。

只针对口唇、下巴、接触路径读取所需pose和蒙皮顶点。骨数组可在有效pose阶段copy，顶点仍懒求值；只在resource/redraw/active topology变化时重建几何。不再把总triangle count或“HasSurface=true”当face通过，身体/衣服面存在并不能证明脸存在。

### 3.2 Active shape 不是普通 morph offset

当前 `FluidModelData` 只携带基础LOD、mesh、bone、submesh，Lumina和raw bridge都未暴露shape tables。上游 Lumina 提供 `ShapeStruct`（LOD shape mesh ranges）、`ShapeMeshStruct`（目标mesh index offset）、`ShapeValueStruct`（base index entry与replacement vertex）。这是**索引替换**的入口，不能直接当可线性相加的position delta。[Lumina结构](https://github.com/NotAdam/Lumina/blob/master/src/Lumina/Data/Parsing/MdlStructs.cs)

建议在generation阶段解析当前enabled shapes，生成可见的resolved index view，检查每个LOD/mesh/range/替换vertex与其权重都合法。多个active shape覆盖同一个index的优先级必须以真实客户端/现有可靠loader验证，不能猜mask bit顺序。若某mesh规则尚不清楚，标记该mesh unsupported；避免整slot省略所有不受影响的mesh，也避免把未应用shape的基础面当真皮肤。保留不同shape状态的resolved topology cache，避免mask变化时每帧磁盘解析。

更难的是 post-bone deformer、角色比例、人脸shader/其他shader-only位移。ClientStructs 的 CharacterBase 公开 `PostBoneDeformer`，这证明存在额外变形路径，不能由普通LBS自动涵盖。[CharacterBase](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/CharacterBase.cs)。先比较真实脸mesh与CPU patch的残差；若局部误差达到可见gap，必须补读实际局部deformer或受控读取GPU局部最终几何。GPU readback若不得不用，只做小块、异步、延迟快照，不让D3D11 immediate-context同步等待全mesh成为每帧代价。

### 3.3 稳定下唇绑定

使用实测下唇/口角区域的三个或多个surface landmarks，存 `(model identity/path hash, slot, mesh, resolved triangle/vertex ids, barycentric)`；每pose评估其位置、切线、normal与surface velocity。源点是口腔侧与外侧下唇边缘的短线/小区域，不能只用一个head-forward法向。表情、开闭口、头部转动与ragdoll时该区域应随最终face skin移动。

对于已支持vanilla face，可通过离线face profile标出实际顶点区域，但仍解析当前loaded mesh并做身份/拓扑检查。mod mesh不能仅按同race套旧vertex IDs。首次导入可提供开发者交互拾取真实triangle并记录profile；这是验证工具，不把用户调offset变成正确docking责任。无可靠face landmark时明确不发射，直到绑定通过。重新找最近点仅限同一解剖patch并有geodesic/法向/身份限制，不能每帧全身nearest point吸到衣物、牙齿或远侧脸。

UV seam可以按reference position、相同权重/partial、resolved shape兼容性和法向/所属解剖面做**受控simulation welding**；渲染顶点保持原本拆分。不能空间距离一近就焊上下唇、衣服与皮肤、双层mod mesh。真正开放边、hidden submesh边、unsupported邻居、UV seam、budget exhaustion要有不同query结果。

### 3.4 首个必须通过的游戏内验收

不开流体，只显示选中face/chin patch wireframe、lip landmarks、normal和anchors，并输出每patch的slot/partial/shape解析结果。相机从正面、侧面、背面检查，慢转头、开闭口、表情、倒地、ragdoll。目标为贴唇误差在近景不出现可见gap；可先设静态≤1mm、运动≤2mm作为项目验收目标，world metres假设也要验证。这是建议阈值，不是已测结果。

在真实下唇放一个固定material anchor，观察它跨表情/倒地始终贴面；再在同patch施加小的沿面位移，验证走到下巴，不跨UV seam掉落、不跳到远侧。源码计数、budget及volume error只能辅助，截图/视频上的实际surface matching是必要证据。该门槛未通过，不进入流体改进验收。

## 4. 需要哪些物理，而不是更多随机轨迹

### 4.1 膜与流痕

薄膜应在material surface上存体积/厚度。对于缓慢薄层，lubrication approximation给出局部质量守恒 `D(V)/Dt = inflow - outflow - evaporation - transfer`；低惯性切向通量随膜厚大致按 `h^3/(3*mu)` 变化，驱动力包含重力切向分量及毛细压力梯度，压力含自由面曲率项。薄层因此慢，积厚后更快，曲率/润湿影响铺展；一个固定全局surface speed不提供这些行为。[Functional Thin Films on Surfaces](https://ddg.math.uni-goettingen.de/pub/ftf_surf.pdf)

初期可以只建立嘴→下唇→下巴的active triangle/cell patch，保留非负volume和成对守恒的edge flux，显式设置无通量/真实出口/未知边界。形变时通过当前cell面积重算 `h=V/A`，防止动画拉伸凭空产生液体；在相邻cell间输运，不能单纯散布独立blue disks。强曲率、厚珠、快速惯性、悬空丝不满足薄膜假设，必须切换表示；论文的第四阶非线性问题不能粗糙显式积分后以大dt宣称稳定。

### 4.2 珠、粘附和slow runoff

接触珠需有体积、footprint、前/后接触角与pinning state。切向驱动力低于接触线保持力时会停，积液/倾斜/运动后才滑；退润湿与前润湿有不同阈值。Furmidge类量级关系 `F_ret ~ k*gamma*w*(cos(theta_R)-cos(theta_A))` 可作为reduced criterion，但k与角度不是所有皮肤通用常数。[Furmidge Equation Revisited](https://pmc.ncbi.nlm.nih.gov/articles/PMC12080332/)

运行时比较体积引起的重力、运动surface的有效加速度与保持力，然后使用黏性drag给缓慢滑动。珠滴merge后体积增大，footprint/保持力更新，可能开始流动；留下的湿膜继续存在。下向面脱离取决于法向有效载荷与capillary bridge/contact perimeter，不用“normal.Y<-.45且age>.22”的统一计时器。皮肤的确切接触角受油脂、粗糙度与液体组成影响，当前资料不能给FFXIV所有角色一套测量参数；要称为校准preset而非人体真实性数据。

### 4.3 丝、桥与断裂

真正唾液的拉丝涉及extensional viscoelasticity，不能只把shear viscosity调大。当唇部蓄液/珠与另一接触点分开，形成短桥，桥伸长、局部变细，最终断裂；自由流出也可形成丝但不能无条件把所有流量变成长tube。[人类唾液的流变实验](https://pmc.ncbi.nlm.nih.gov/articles/PMC4549258/)、[真实唾液capillary bridge视频](https://ecommons.cornell.edu/entities/publication/7787fac8-0ad6-4957-85f1-c22c00c15d71)

Reduced filament可用centerline+每segment体积，截面 `A_i=V_i/L_i`，沿丝有限体积质量输运，端点供液/排液，毛细轴向力和黏性拉伸阻力；需要本构时再加入低自由度应力/松弛状态。Discrete Viscous Threads给出伸长、弯曲、扭转速率的黏性框架，它是Newtonian黏丝起点，不自动等于mucin黏弹性。[Discrete Viscous Threads](https://www.cs.columbia.edu/cg/pdfs/171-threads.pdf)、[2013推导](https://www.cs.columbia.edu/cg/pdfs/1382124152-dvr-jcp.pdf)

断裂应来自局部neck半径/应力演化并有守恒拆分，产生端珠和自由drop；每段volume仍在账本内。当前全局radius与固定30%丝/70%末端珠无法表达局部颈缩。XPBD可作为积分/约束求解技术，但其compliance不替代流变、毛细力或润湿条件。[XPBD](https://matthias-research.github.io/pages/publications/XPBD.pdf)

接触必须考虑丝段扫掠与可见surface，不能只轮换一个interior node，近唇/下巴优先。预算不足延迟/保留volume；已解析接触/未知contact不能混为miss后自由穿透。源切断后需允许桥排液/颈缩过程，不因`!Emitting`立即把整条丝体积倒入一个末端drop。

### 4.4 地面摊

局部固定地面patch上存height/volume与footprint/contact line；冲击阶段快速铺开，之后黏性/毛细缓慢铺展和合并，达到平衡后保持。开始阶段可用经验证的守恒footprint动力学，后续扩展2D有限体积patch。地形采样应分散到帧，已有采样稳定复用；新边界未验证前不越界。不要把全部puddle在坡上平移作为表面流动，也不要只测8个径向点就跨空洞/台阶画triangle fan。

固定 `V` 下验收footprint area随时间增大、平均厚度降低、总volume守恒；新drop加入后先局部merge再扩大。处于悬崖/台阶边缘时局部volume转成下落drop，其他部分留下，而不是整个摊瞬间消失/掉落。50µm与7cm半径上限属于现有视觉假设，没有真实saliva在任意游戏地面上的依据。

## 5. 渲染：膜、珠、丝、摊不能共用一种蓝透明小三角

湿材料外观来自液面反射与基底透射/散射变化，单纯blue alpha overlay不等于wet skin。[Rendering of Wet Materials](https://graphics.cs.yale.edu/publications/rendering-wet-materials)

共享WorldGeometry API可扩展material类型与更多vertex属性，但simulation只输出自己的bounded geometry/data。薄膜先用贴合已验证surface的连续patch/流痕，提供wet specular/roughness、coverage和thickness变化；若不修改基底skin material，就诚实称为overlay wet film，不能宣称真实皮肤BRDF已改变。环境反射可先采用可用cubemap/受控近似，再考虑受支持的scene-color折射；scene depth不是scene color，不能凭现有depth SRV推断已具备refraction。直接写全角色ForcedWetness也无法表达局部流痕。

珠用平滑球冠/变形椭球与连续surface footprint；丝用连续parallel-transport frame、变半径和末端圆滑连接，不为每段重新选择world-axis切线；摊用连续边界、浅厚度和边缘coverage。根据投影误差细分圆周/纵向曲线，近景有足够silhouette，远景减到低预算。模拟节点数与渲染细分独立；增加triangle数不能修正错误本构。

半透明需要明确depth-write、内部重复表面、画序/OIT和gamma/后处理策略；当前pass只是常规alpha blend和scene-depth discard，self-transparency、TAA和反射并未通过。优先稳定近景surface film和体积珠，再加折射，不把大量高光作为真实感替代。

## 6. CPU预算与实机验证

现有4096 skinned vertices、4096 triangle tests是每pose预算，最多两次simulation substep共享pose；保留这一上界思想。预算不是性能证据：`LastCpuMilliseconds`只测pose callback，不包含 `UpdateActor/BuildTopology` 的文件I/O与mesh解析hitch，历史status已有一次约65ms加载停顿记录。需要同时报告Framework generation load、pose skinning/query、fluid solve、geometry build/upload和GPU pass；p50/p95/p99、最大值、budget stalls，不能只报一个0.043ms sample。

推荐初期活动区上限：一个唇/下巴patch的数百到千级顶点；膜几十到数百cells；最多1–2条活动丝，各16–32个物理节点；珠几十个；地面摊少量局部patch。以上是候选容量，不是已测容量。Priority为lip anchors→attached surface/films→近身丝碰撞→自由滴→远地面扩展。可见geometry也有限额，surface query与draw不能互相抢到所有budget。

资源解析在generation变化时做；使用managed byte snapshot的解码可移到后台，native pointer只在合法游戏线程快照，不把raw pointers留给worker。generation完成前暂停需要该surface的发射。重复redraw/mod变更必须有cache和取消旧结果。正常帧避免全mesh bake/同步GPU readback；超过预算时降级activity或更新频率，不能增加预算假装解决。

对快速ragdoll/传送、pose间隔>.15s、LOD切换、partial重建、shape切换、deformer不支持、真实open/nonmanifold边、ground缺采样、mirror/multiview都设置显式失败/暂停边界。恢复时不能由错误previous snapshot算出巨大velocity；generation anchor失效不能继续挂在新模型上。一般三slice moving triangle contact不是精确旋转CCD，快速头部/边角仍可能miss；验收必须包含这些动作。

## 7. 分阶段验收（每阶段是游戏内同一路径，不创建独立测试模拟）

1. **Surface命中与源绑定。** 只做真实face patch、landmarks与anchor运动观察；partial/shape诊断可解释；静态、表情、头转、倒地全部贴合。未通过就停在该阶段。禁止让用户改offset替代修复。
2. **局部湿膜与珠。** 人工在已验证下唇patch注入小体积，用同一runtime controller观察停留、merge、慢流到下巴、连续湿痕、倒置后有条件脱落。flow volume、footprint和厚度关联；没有穿透、seam跳落或蓝disk序列。
3. **唇部供液及丝/桥。** 接入source reservoir，先形成下唇wetness/珠，再按状态形成桥、drop或长细丝；流量有变化时不中断跳动；停供后排液和necking连续，拆分守恒。附着接触发生后确实把体积送给skin film，不只是tip flag变化。
4. **地面摊。** 同样固定体积的两帧/多秒对比可见spread；重复落滴merge；坡、台阶和边缘局部输运，ground patch无凭空跨越。圆形只适合平坦局部，并非所有地面形状。
5. **材质与连续近景几何。** 膜、珠、丝和摊分别观察正面/掠射、静止/移动，近景不显8边、转段不扭、不像塑料；distance LOD不改变液体物理体积；透明和深度顺序可解释。
6. **默认值/reset与稳定性。** 一键恢复所有默认参数并清理runtime；保存后重启仍一致。相同真实场景持续运行、换图、redraw、resize、启停，确认没有预算失控和stale anchors；报告各阶段p95而不是声称理论预算通过。

实施顺序中物理默认值、source placement验证优先于更多可调slider。每阶段提供近景短视频、实际slot/partial/shape与volume/budget计数；通过后再推进。全局“看起来有一根丝”或计数`skin>0`不构成基本真实性完成。

## 8. 资料范围和未决事项

上述论文和实验支持reduced representation、wetting/pinning、viscous/viscoelastic thread与wet appearance的方向，但没有一篇直接提供FFXIV实时插件完整方案。精确face partial palette、active shape覆盖顺序、post-bone deformer读取和所有mod topology仍需在项目当前依赖版本/实机确认；上游最新ClientStructs字段只作路径线索，不作为内存offset落地依据。[Model](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Graphics/Render/Model.cs)、[PartialSkeleton](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Graphics/Render/PartialSkeleton.cs)。论文页面的搜索crawl日期不等于论文出版日期，DVT为2010/2013，wet materials为1999，XPBD为2016。

完成的研究不等于验证了实现。当前最有信息增益的下一项工作是**真实face surface与lip anchor proof**，不是再build一版完整口水让用户试。
