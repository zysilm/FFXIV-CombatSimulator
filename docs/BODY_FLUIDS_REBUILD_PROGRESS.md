# 口水重做进度

2026-10-04，分支 `experiment/body-fluid-rendering-prototype`。持续任务目标 active；完整特效尚未完成。原型快照 `08d793c` 保留，以下为其后的未提交实现。

## 已写代码与验证边界

- Reset 集中默认值、清理口水状态、保存；Effects 和 `/combatsim fluid reset` 接入。
- 性能窗口分别统计 Framework 活跃更新（含加载）和 pose 求解，256 个样本、p50/p95/p99/历史最大值；GPU 使用四组 timestamp/disjoint query、DONOTFLUSH 延迟读取，范围仅插件绘制段，不代表整帧。
- partial skeleton 只读连接与身份检查；MDL active shape 索引替换；真实表面唇 anchor 接口，取消 jaw/head 固定偏移源。未知 lip 不发射。
- 通用液体 API：独立 FluidVertex/FluidMaterial、连续平滑椭球/变半径丝 builder、scene-color snapshot 和 screen-space refraction、覆盖合成。反射是程序棚灯 fallback，厚度是近似，未匹配游戏 light probes。
- 局部守恒膜、固定体积地面积液铺展、Maxwell 松弛细丝组件已接入发射；连续薄膜、球冠附着珠、有限供液和接触转移已写，不等于已通过画面验收。
- 30 秒 surface/material 诊断只走生产 pose/render 路径，无独立模拟脚本。后续源码加绝对截止时间，避免 pose 缺失造成诊断不结束。

## 实机记录

15:47 第一阶段 DLL 在原 build 位置正常加载。用户运行 surface/material/status 后报告两者都不可见。

实际日志：surface 所有模型 faces=0，face slot11 active mask `0x00210080` 无法映射；partial1 已读出 80 个面部骨骼，含 `j_f_dlip_02_l/r`、`j_f_dmlip_02_l/r` 等。Framework 初次拓扑加载最大 27.030 ms，不能因无几何时 pose p95 约 0.015 ms 就认为性能达标。

液滴被残留 rasterizer viewport 检查拒绝，未 Draw。因此第一次测试没有证明折射效果。SetTarget 标记验证主 backbuffer，但不保证 RS viewport 更新；已将快照判断改为实际 source texture/RTV 尺寸、格式及 MSAA 条件，Draw 使用自己的 viewport，诊断记录原 viewport。

查明实际 MDL v6 palettes 为相对偏移/数量与变长索引，不是固定 64 项；实际 declaration type17 是 8 字节骨索引/权重。液体适配路径已补读 v6 表和 8 influences，推进正确 shape cursor，不改原 collision parser。依据为 [TexTools 上游 MDL 读写实现](https://github.com/TexTools/xivModdingFramework/blob/master/xivModdingFramework/Models/FileTypes/Mdl.cs)，具体内存字段同时核对当前 Dalamud 依赖。

15:56 第二阶段 DLL：用户确认线框贴脸；液滴表现为暗色不透明宝石。首次 Framework 模型处理最大 395.769 ms，未达标。

16:14 第三阶段 DLL：使用 inverse-view 获取实际相机眼点、显式近侧法线选面；新增仅折射背景诊断。用户确认 surface、材质正常，refraction 不黑，红点位于下唇缘且随脸，已执行 confirm-lip。材质仍白亮，来自程序棚灯反射，需要提供艺术反光权重调整。

模型读取/解压/MDL 解析移到 managed worker，Framework 仅采集身份并安装 ready 的拓扑。首次模型 snapshot=2.119ms，后台解析20.060ms；第三轮首次 Framework 最大33.667ms、pose最大22.249ms。缓存后的再次 surface：Framework 最大2.509ms、pose p95=.274ms、最大.470ms。不能把缓存结果当作冷启动成绩；完整身体尚未测量。

第三轮材质 GPU 样本816：p95=.189ms、p99=.459ms、最大.948ms，无 disjoint/query errors；仅覆盖本轮静态材质预览，不保证完整流动性能。

16:22 第四阶段 DLL 编译成功、实机日志确认加载：艺术供液默认 .006ml/s，范围 .001–.12（不声称生理测量值）；旧原型供液迁移一次。黏度 .18Pa·s 和 Maxwell 松弛 .45s 接入求解；反光权重 .35/粗糙度 .08 接入生产与预览。反光0同时恢复透射，避免暗边。移除没有接入新算法的旧滑块，保留 JSON 字段兼容；Reset 恢复新参数。

湿膜共享顶点厚度重建守恒体积，自由表面法线由抬升后的三角形面积加权生成。珠改为体积决定的局部球冠，接触足迹与阻力一致。

首轮低供液测试失败：用户报告 fluid on 完全不可见。14秒日志体积.0858ml、105vertices、128cells，说明早期仅薄膜；55秒体积.3338ml、7845vertices，说明后期已生成额外几何，但依然没有目视证据。稳定 pose p95 从.159ms增至.924ms，后期4096三角接触预算耗尽、deferred=.0167s。体积账本误差约1e-21m³，不能用质量守恒代替可见性/真实性验收。完整身体冷安装 Framework 最大44.682ms。

下一诊断只改变现有生产几何着色：inspect不透明法线，inspectdepth在深度剔除前显示红(拒绝)/绿(通过)，inspectnormal保留深度并显示背向红/正向绿。生产Composite、offset、物理库存不改。用户贴脸线框通过只证明0.7mm偏移，不能证明80µm湿膜通过最终场景深度。另查明mod路径StdString.ToString走Windows ACP，已改为有4096字节长度保护的UTF8读取，仅液体路径。

仍有明确限制：地面积液只支持验证过的平坦三角形内部，坡面/台阶输运未实现；UV 接缝焊接未实现；反射未读取游戏环境探针；最终 GPU deformation、多视图与完整接触/性能尚未验收。完整功能未完成。

## 第五轮反馈与修正方向

用户确认 inspectnormal 能看到绿色的实际液体面；production on 几乎不可见，并观察到绿色区域沿脸摊开。绿色不是整个身体采样范围，而是生成的液体几何。真实绘制/正向法线已有证据，薄湿膜外观微弱；目前所有嘴部供液先进入皮肤膜的建模选择偏离“唇缘垂涎，身体承接缓流”的目标。

悬滴仍未正确加载细丝：旧规则仅 quarter seed 分给短丝，其他体积留在贴皮珠，源压力又可能低于细颈压力。以当前日志 .00416ml、约5mm长为例，均匀半径约.515mm，重量约40.8µN，当前毛细轴向力约89µN，不能期待它自发拉成可见长丝。这是现 reduced 方程的量级推论，非实测人体液体参数。DVT 论文明确需要一起考虑外力、表面张力与伸长/弯曲/扭转黏性；本实现只覆盖其中部分。[Discrete Viscous Threads](https://www.cs.columbia.edu/cg/pdfs/171-threads.pdf)

下一模型让唇缘接触珠先蓄液，局部薄膜只通过润湿/滑动/接触获得液体；真实失去法向保持时形成颈与终端滴。终端滴体积由同一model唯一拥有，其质量加入末端节点；断颈后守恒生成自由滴。几何颈尺度和艺术参数需实机校准，不使用固定体积百分比或固定周期喷射。

已写未部署：三角 current/previous swept AABB 按帧缓存，只有候选才执行原三slice检测；skin4096、narrow4096、cheap candidates32768 budgets保留，未完成查询仍 Pending。八项影响owner已修正。湿膜一整patch校验后安装pose geometry，避免部分更新。新增12秒有限生产trace，只读游戏状态，不运行独立模拟。Clear/off恢复Composite，防止debug绿色状态留到下一轮。

17:02 第六阶段 DLL已重载：pendant唯一terminal库存与真实末端质量、唇cap先蓄液、小范围25µm接触润湿、3×3黏性/横向正张力隐式积分、局部trial-strain限制、24×12终端球已接入；query缓存与worker也部署。源码编译通过不代表行为通过。

本轮实机仍失败：用户仅看到绿液珠停唇部。17:04 trace的source normalY为+.098至+.120，液珠在.020ml上限、蓄液区积累.136ml，而thread/drop为0。法向载荷为0、保持力约150µN。源珠被固定且只法向释放的条件会使竖直/朝上的真实唇面永远无法垂涎，必须允许经过验证的切向脱钉和下滑到唇边，不能仅靠增加供液量修补。

冷完整拓扑Framework最大5.971ms，后台邻接/簇代码改善了之前约45ms峰值；pose稳定p95约.199ms、GPU p95约.186ms。但这一轮没有任何细丝，不能据此声称完整运动性能达标。

正在修复：源珠切向移动后变成普通材料接触珠，嘴部仍向原验证唇点供液；真实相交且同一支撑层的足迹合并，避免无数小源珠填满池；大旧珠保留移动锚点，不能吸回嘴唇。未确认邻接/边界仍保留库存。

地形膜GroundFilmRuntime已写并接入未部署源码，替换旧平坦单三角circle摊：3mm格裁剪真实BG三角、384cell、守恒坡面输运、每step最多4新支撑probe、同层真实公共边连接及连续自由液面。编译通过；台阶出口掉滴和动态地形重新验证未完成，尚未实机验证。

17:14:22 第七阶段DLL已重载：source cap达到切向脱钉后可沿已验证表面移动，只有成功推进才转为runoff role；原唇点继续供新cap；真实重叠同patch/同层且walk连通的cap合并并保留旧runoff位置。所有附着thread引用的bead slot禁止复用/合并。新增每cap normal/位移/blocked诊断。地形膜同步启用；用户已获20秒真实游戏验证命令，结果待回。

共享渲染与角色表面接口及限制已分别整理在 [WORLD_GEOMETRY_API](WORLD_GEOMETRY_API.md) 与 [CHARACTER_SURFACE_API](CHARACTER_SURFACE_API.md)。当前角色服务仍仅LocalPlayer，未来泛化不能移除身份检查。没有用独立模拟/测试脚本作为验收。

## 第七轮实际结果与时间步修复

17:16–17:17 实机失败，用户确认 inspectnormal 有绿色但液面浮空，Composite/on 不可见。最初约19秒 thread/drop/ground均为0；到74.55秒才有2条thread，共.03393ml（terminal子集.03330ml），地面积液仍为0。不能把后期存在几何视为滴落通过。

最新trace两条thread的requestedAge为47.25/45.28秒，simulatedAge仅.0024/0秒，lastRequested和lastSimulated显示0。源码确证controller的float `1f/60f` 比runtime的double `1.0/60` 大约8.69e-10秒；runtime每次完整step后额外跑一个微小残步，并再次重置碰撞预算、启动proposal。碰撞proposal跨帧完成后若落到残步，会持续启动几乎没有位移的新proposal。这是确定的生命周期错误，正在统一分步，不能靠放大query预算解决。

旧湿痕按液珠体积比例转移，19秒时film已占总液体约93%。已写未部署修复：湿痕体积=实际验证接触宽度×实际走过距离×25µm，并受接收cell的25µm欠量限制；实际接收量才扣液珠库存，不改变供液量或表面张力。

分步修复现已写入：完整消费传入时长的均匀分步，60Hz float时长只跑一次runtime step；加入实际dt、substeps、microstepCount和solver停止原因，不改本构/断裂/积分阈值。追加minSegment、节点最大速度与world float ULP，只在status/trace读取，便于真实游戏区分短颈收缩与世界坐标精度，不使用独立模拟验证。

地膜注册原先只限制4个新ray，缓存支撑上的建格却可每步集中插入；现新增共享每step最多2tile/8cell（包括碰撞入液及缓存扩展），邻接精确检查先过滤非相邻XZtile。拒绝接收的液体仍归原owner所有。地面增长速度和CPU成本尚未实测，不能宣称通过效能要求。

浮空需分别检查两点：film实际厚度在旧算法下已达.4–2mm，另附着球冠底圈位于单个triangle切平面，跨嘴唇曲率可能离开真实皮肤。固定80µm绘制偏移不能解释全部误差；原surface wire有.7mm偏移，因此此前“贴脸”不等于CPU/GPU皮肤在亚毫米尺度一致。

旧折射仅计算液体内部的局部位移，缺少自由液滴出射界面及出射后的背景传播，毫米液滴可能几乎没有可见背景变形。正在给显式椭球补真实内部弦长和双界面折射；scene depth距离只用于空气中传播，不用作吸收厚度。推导依据：[PBRT dielectric](https://pbr-book.org/4ed/Reflection_Models/Dielectric_BSDF)、[sphere intersection](https://pbr-book.org/4ed/Shapes/Spheres)。这是屏幕空间近似，无法恢复被遮挡/屏幕外背景，贴肤膜也不能声明为自由球体。

## 第八、九批与身体表面失败证据

17:41 第八批真实游戏 trace 已确认正常帧只运行一次完整 runtime step，残余微时间步计数为0；但液体外观仍失败：约 .203ml 被保存在11个球冠里，球冠因支持域映射失败隐藏；约 .020ml 悬滴的 simulatedAge 远低于 requestedAge，短颈积分反复被 strain 限制拒绝。不能把时间步修复或已有库存视为垂涎功能通过。稳定 pose 低于1ms，但有约140ms冷峰和36ms Framework峰，需要区分采样、初始化及渲染锁等待。

18:07 第九批部署了守恒短段合并、实际库存统计、支持域裁剪球冠及有界全身线框诊断。编译通过；尚未证明液滴运动和正常材质可见。18:17 用户再次开启时，日志库存与渲染顶点均为0，实际状态为 `Paused: source pose/verified lip unavailable or pose gap`，需与之前“有液体但隐藏”的失败区分，不能全部归为体积太小。

18:08 全身诊断后，用户指出身体三角形没有贴合 Bodymod，脸部却贴合。日志已证明模型文件来自当前 Penumbra 替换资源，身体 `c0201`、脸部 `c0801`。代码缺少跨种族 PBD 预变形，原始 LBS 不能证明对应游戏最终皮肤。参见 [表面 API 的变形缺口](CHARACTER_SURFACE_API.md)。18:18 已通过实际游戏命令入口关闭出液，先验证身体模型变形与当前外观对应，再恢复落液碰撞测试。当前此项为高可信缺口和待验证原因，并非已完成修复。

## 接下来

1. 先证明 face/下巴表面可见且动画贴合、静态液滴真实折射。
2. 根据实际 lip 骨骼和受限面部权重/表面区域自动绑定唇缘，不将 offset 校准交给用户。
3. 接入连续薄膜并验证停留、合并、缓流与接触转移，再接黏丝和 ground spreading。
4. 全程补资源缓存/初始化开销控制与 CPU/GPU 测量，最后验证 KO/抓取、地形、重载与多视图兼容。
