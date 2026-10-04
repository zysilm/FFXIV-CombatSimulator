# 高级口水特效重做方案

2026-10-04。状态：研究与实施计划，未实现、未部署。失败原型已独立提交到 `experiment/body-fluid-rendering-prototype`，快照 `08d793c`；原 feature 保持 `3c71a14`。快照编译检查使用临时输出目录，没有替换游戏 DLL。

## 1. 目标与失败结论

用户需要从嘴唇自然溢出的透明黏稠液体：唇缘蓄液、附着皮肤、缓慢沿面流动、拉丝与断裂、接触身体其他区域后转移、落地形成逐渐扩展的积液。近景必须有连续轮廓、晶莹反射和可观察的背景折射。Effects 参数需要恢复默认值；启停、重载与其他插件共存不能造成卡顿或崩溃。

当前原型仅证明世界空间绘制、深度遮挡和独立 producer layer 可用。模拟和外观均未通过。固定 8 边几何、透明高光、单股持续供液及估算 jaw/head 出液点，不能作为成品基础继续堆参数。

保留通用 WorldGeometry 服务的生命周期、深度接入、隔离 context 与 layer 契约；重新设计流体状态、局部表面和液体材质。模拟节点数与渲染细分数量独立。使用有物理约束的局部模型，精度与支持范围逐项实测，不能宣称完整人体流体仿真。

详细源码缺口与原始资料分别见 [行为研究](FLUID_REALISM_RESEARCH.md) 和 [材质、资源研究](FLUID_MATERIAL_RESEARCH.md)。

## 2. 实施顺序和硬门槛

### A. 真实脸部表面与下唇绑定

先不发射液体。在游戏显示脸/下巴的局部表面、下唇 landmarks 和附着点。只读解析实际加载模型、可见 submesh、partial skeleton 与 active shape replacement；不能继续只查 partial 0，不能因 shape mask 非零省略整个脸。shape 是索引替换，不能当普通 morph 位移。当前依赖版本的字段和变换空间必须核实，不能直接照最新上游内存布局读取。

源点保存模型身份、resolved triangle/vertex 与重心坐标，每次最终 pose 更新后重评估。标准脸可有已核实的 profile；mod 拓扑必须匹配实际模型，不能套旧顶点号。开发者可交互拾取唇面生成 profile，不能要求用户用 offset 修正错误 docking。无可靠源点时不发射。

依据：[Lumina MDL shape 结构](https://github.com/NotAdam/Lumina/blob/master/src/Lumina/Data/Parsing/MdlStructs.cs)、[PartialSkeleton](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Graphics/Render/PartialSkeleton.cs)、[CharacterBase 的额外 deformer 路径](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Graphics/Scene/CharacterBase.cs)。这些提供排查入口，尚不证明 CPU 表面与最终渲染完全一致。

验收：正/侧/背视角，头转、表情、开闭口、倒地和抓取时仍贴下唇。暂定静态 1 mm、运动 2 mm 为项目目标，须核实单位并实测。若 deformer 造成可见偏差，继续解决实际变形读取；不进入完整发射。

### B. 透明液体材质与平滑形态

在同一游戏渲染路径放一个静止液滴，先证明晶莹透镜感。扩展 API：受控材质和纹理 handle、液体厚度、UV/tangent、场景颜色快照、小范围离屏 targets。现有 vertex 已有 Normal，问题是液面法线质量及材质输入不够，不是完全没有法线。

背景在液体绘制前快照，不能读写同一个 RTV 资源。先核实格式、MSAA、有效 viewport、动态分辨率和颜色空间；CopyResource 不做缩放，MSAA resolve 也不能凭格式名称猜。新增资源随 resize/device 变化重建，异常仍恢复 context，不持有阻碍 swapchain resize 的引用。

依据：[Microsoft SRV/output 冲突规则](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-pssetshaderresources)、[CopyResource](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-copyresource)、[ResolveSubresource](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-resolvesubresource)。

液滴可用解析椭球交点/厚度或自适应连续曲面；丝采用连续曲线和 parallel-transport frame，随屏幕误差细分；薄膜和积液由高度分布生成曲率/法线。材质结合 Fresnel、厚度吸收、背景折射和反射，液体内部光程不能用液面到背景的距离替代。已包含背景的折射颜色用 coverage 合成，不能再次用低 alpha 稀释成灰蓝塑料。

依据：[GPU Gems 2 折射方法](https://developer.nvidia.com/gpugems/gpugems2/part-ii-shading-lighting-and-shadows/chapter-19-generic-refraction-simulation)。屏幕空间无法恢复遮挡/屏外背景，需深度拒绝、边缘退化与明确误差范围。先不承诺焦散或完整光线追踪。

验收：近处实际背景随液滴曲率明显变形，薄边透明、掠射反射，人物正确遮挡；转镜头无面片高光跳变、无 UI 折射、无背景边缘拖影。resize、不同 gamma/动态分辨率设置必须复测。未通过就不增加液体数量。

### C. 贴肤湿膜与体积珠

在已验证下唇/下巴局部 patch 存 cell 体积，以当前面积计算厚度。成对守恒通量负责切向重力、黏性和毛细铺展，接触线有附着/钉扎条件。薄层慢流，积厚后加速；珠滴合并后才可能克服保持力滑落。连续湿痕不能由定时散布独立圆盘代替。

依据：[Functional Thin Films on Surfaces](https://ddg.math.uni-goettingen.de/pub/ftf_surf.pdf)、[Furmidge Equation Revisited](https://pmc.ncbi.nlm.nih.gov/articles/PMC12080332/)、[Rendering of Wet Materials](https://graphics.cs.yale.edu/publications/rendering-wet-materials)。薄膜方程只适合慢速薄层；第四阶项需要稳定离散。皮肤接触角未知，参数是艺术校准值，不能冒充生理测量。

受控焊接兼容权重/参考位置的 UV seam，仅供模拟邻接；不能焊上下唇或衣物/皮肤相邻双层。真实出口、未知邻面和预算耗尽有不同状态。先局部嘴→下巴→颈部，再扩展到其他接触部位。

验收：小体积先停留，聚集后慢滑，动画中湿膜贴肤连续；跨 seam 不掉落，翻身后按受力脱落，无计时强制剥离。仅 overlay 材质时明确它是覆盖薄膜，尚未修改原生皮肤 BRDF。

### D. 唇缘供液、黏丝、桥与转移

供液首先进入唇部 reservoir/湿膜/珠，再根据接触和载荷进入桥、丝或自由滴。源不添加喷射速度，继承唇运动速度。避免把所有新增体积直接变成均匀加长的单根管。

每个丝段存体积，截面积由体积/长度决定；局部质量输运、黏性拉伸阻力、毛细力与应力松弛控制变细、颈缩和断裂。断裂守恒地形成端珠与自由滴；停止供液继续排液，不整条瞬间改成一个球。

依据：[真实唾液拉丝视频](https://arxiv.org/abs/0909.3980)、[唾液流变实验](https://pmc.ncbi.nlm.nih.gov/articles/PMC4549258/)、[Discrete Viscous Threads](https://www.cs.columbia.edu/cg/pdfs/171-threads.pdf)、[XPBD](https://matthias-research.github.io/pages/publications/XPBD.pdf)。DVT 是黏性模型依据；XPBD 是求解工具，两者均不自动提供完整唾液黏弹本构。

丝段扫掠优先检测嘴/下巴接触，转移进入同一湿膜账本；其他身体部位沿用已验证局部 surface。不是命中 flag 改了就算完成附着。预算不足保留状态，不能按 miss 穿透。

验收：蓄液、滑动、拉伸、变细、断裂和回缩连续发生；没有固定周期跳球/水龙头，停止供液后仍有合理残留。近景丝不变成粗水管，远景通过 coverage 抗锯齿保持稳定。

### E. 地面积液扩展

局部地形 patch 持有体积与高度/足迹状态；冲击后铺展，固定体积下边界仍逐渐扩展、平均厚度下降，最后停在润湿平衡。新增滴落局部合并，坡面输运、台阶边缘局部溢流。地形边界探测分帧复用，不用整个圆盘平移或八点扇形横跨空洞。

依据仍为薄膜/润湿资料，具体 reduced spreading 模型属于本项目设计，必须以游戏结果验证。先实现有限、稳定、守恒的 footprint dynamics，再评估局部二维 cell 输运；不能通过固定 50 µm 厚度立即计算半径冒充时间铺展。

验收：停止供液后仍能观察足迹缓慢扩展；重复落滴扩大同一摊，平坦/坡面/台阶行为不同，无漂浮面片。积液边缘薄、法线连续、反射折射随观察角度变化。

### F. 参数、性能和兼容收尾

Effects 加一键 Reset：集中默认值、恢复并保存参数、清理口水 runtime；不清除其他 WorldGeometry layer。参数版本迁移明确，减少缺乏物理意义的 slider。Reset 在 C 阶段开始调参前就落地，F 阶段再次验证持久化。

候选初期容量：数百局部 film cells、几十珠滴、1–2 条 16–32 节点丝、少量 ground patches。保持按需蒙皮，禁止每帧全身 mesh Bake 和同步 GPU readback。托管模型数据解码可后台，原生指针只在合法线程快照，旧 generation 结果取消。

目标预算而非已测承诺：效果活跃时 CPU p95 ≤0.5 ms、GPU p95 ≤1 ms（以当前机器和指定分辨率实测）；同时报告 p99、max 与初始化停顿。既有 4096 顶点/三角测试上限不等于时间预算。历史首次解析约 65 ms 停顿必须解决并复测。统计涵盖资源加载、蒙皮查询、求解、几何构建上传及 GPU passes。

4K RGBA8 一张全屏颜色约 31.6 MiB；每帧复制还有读写带宽成本。只在可见且需要折射时快照，优先局部区域/受控分辨率，不能把全屏多 pass 当免费。细丝避免低分辨率全局模糊，bulk 融合真的需要时才引入屏幕空间流体重建。

验收全部在游戏、同一实际 controller/render API 中进行，不创建独立模拟脚本。阶段提供近景截图/短视频与日志，而非仅 counts。最后持续运行、KO/抓取、切图、redraw、resize、启停与插件重载；先停止发射再部署新 DLL。DynamicPortrait 多视图识别仍是未解决兼容门槛，不能把当前暂停策略称为已兼容。

## 3. 去哪里找效果、图片和资产

| 用途 | 来源与选择 | 能解决什么/不能解决什么 |
| --- | --- | --- |
| 真实唾液形态参照 | [唾液实验视频](https://arxiv.org/abs/0909.3980)；[MIT 微距图片与实验解读](https://news.mit.edu/2010/physics-saliva-0811) | 珠滴/细丝/断裂形态参考；不能把照片贴到管上替代模拟 |
| 游戏湿润与玻璃外观参考 | [Naughty Dog《Uncharted 4》官方技术演讲](https://advances.realtimerendering.com/other/2016/naughty_dog/index.html) | 官方介绍包含 wetness 和 glass shading；不是现成口水实现。PDF本次获取失败，不声称已阅读具体 slide 算法 |
| 液体重建与晶莹外观 | [NVIDIA GDC 2010 演示 PDF](https://developer.download.nvidia.com/presentations/2010/gdc/Direct3D_Effects.pdf) | 已查看渲染流程与最终透明效果。可参考 depth/thickness/normal/background 多 pass；近处细丝需单独表示，不能全局 blur |
| 可打包反射资源 | [Poly Haven Studio Small 01](https://polyhaven.com/a/studio_small_01)，[CC0 许可](https://polyhaven.com/license) | HDR/EXR 转低分辨率 mip cubemap；室内棚灯只作验证/fallback，游戏环境匹配仍待解决 |
| 可选积液细节纹理 | [TextureCan Others 0028](https://www.texturecan.com/details/531/)，[条款](https://www.texturecan.com/terms/) | CC0 normal/displacement 实验素材；雨水扰动不匹配安静口水，最终宜程序生成小幅涟漪 |
| 其他引擎现成特效 | Unity Liquid Volume / Unreal Niagara liquids，详细链接见材质报告 | 作为演示/技术参考；组件、shader 和 native runtime 不能直接在 Dalamud 加载。尚无核实适用的完整现成包 |

不提取其他游戏模型/贴图作为分发资产。当前未下载商业包或运行素材。后续采用资产建立 manifest：来源、许可、格式、分辨率、转换步骤和用途。AI 图片可用于概念板或次要 mask，经检查后使用；不能生成真实动态厚度、正确法线与实时场景折射，因此本轮没有以生成图片冒充液体实现。

## 4. 通用渲染 API 的扩展边界

保留普通 layer 的简易 triangle/unlit 用法。新增 `WorldMaterial`/texture handles 和受控 `SceneColorSnapshot`，液体渲染使用独立 payload 或 vertex layout；不要强迫所有调试 producer 为流体支付多 pass 开销。GPU pass/资源由上层服务统一调度，producer 只提交有限 immutable 数据，不能任意操作游戏 context。

每帧背景快照共用一次；区分普通透明、折射覆盖合成与 fluid offscreen pass 的排序。多折射层重叠先列为限制并实測，单个背景 snapshot 无法自动正确解决所有自透明。缓存尺寸/格式/generation，关闭效果释放其资源和 layer，其他 producer 继续工作。

最终推荐：先 A 与 B 两项证明，随后 C→D→E；F 的 reset、统计和生命周期从早期贯穿实施。真正的完成标准是唇缘、皮肤、丝、地面及光学效果都在真实游戏画面成立，不能再把绘制接入成功当成高级液体功能完成。
