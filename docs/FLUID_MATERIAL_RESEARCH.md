# 透明黏滞液体：材质与可移植资源研究

研究日期：2026-10-04。范围：只读研究；未下载商业包、未构建、未修改运行代码、未操作游戏。以下实现建议是根据现有代码和官方技术资料作出的工程推断，不代表已在 FFXIV 验证。

## 结论与当前缺口

最优先做“真正采样背景的液体材质 + 平滑法线 + 厚度”，再做口部/皮肤/地面三个不同形态。增加透明度、白色高光、更多低模球或更长直管不能产生透镜折射，也不会自然成为口水。

本仓库当前 `WorldVertex` **已有世界空间 Normal**；`WorldTrianglePass` 已将其插值给 PS，使用固定方向 Blinn 类高光及 Fresnel，绑定 `SceneDepth` 到 t0。不能再把问题描述成完全没有 normal。实际缺口是：缺少液面质量的曲率/法线、UV/tangent、厚度数据、scene-color SRV、环境贴图/normal texture/sampler 绑定、材质参数和液体多 pass。当前 alpha-blend 的 PS 没有读取背景颜色，因而只是透明有色表面，无法折射。

建议液体 API 独立于调试三角形 API：保留后者，增加 `FluidMaterial`（IOR、roughness、absorption、refraction strength）、`FluidVertex`（position、normal、UV、thickness 或 material ID），以及受控的 `SceneColorSnapshot`/texture handles。别把任意第三方引擎 shader 塞进现有 Draw 调用。

## 官方技术来源及移植判断

| 来源 | 已核实技术事实 | 本项目适用性 |
| --- | --- | --- |
| [NVIDIA GPU Gems 2 第19章](https://developer.nvidia.com/gpugems/gpugems2/part-ii-shading-lighting-and-shadows/chapter-19-generic-refraction-simulation) | 从背景渲染纹理扰动采样模拟折射，加入折射 mask 防止采样非目标区域；不同色折射表面重叠有误差 | 可写成 D3D11 HLSL；是屏幕空间近似，不能宣称真实光线透镜求解。先实现正常法线驱动的背景扰动和深度拒绝 |
| [NVIDIA Screen Space Fluid Rendering for Games，GDC 2010](https://developer.download.nvidia.com/presentations/2010/gdc/Direct3D_Effects.pdf) | 粒子前表面 depth→边缘保持平滑→重建 normal；单独 thickness target；背景折射、Fresnel cubemap reflection、Beer absorption | D3D11 原生可实现多 render target / PS passes。第10–11页是管线，第43–50页是厚度与材质。只重建最近液面，不能正确处理所有前后液体层；细丝不能盲目降分辨率 |
| [van der Laan/Green/Sainz，I3D 2009 论文/作者演示](https://wstahw.win.tue.nl/edu/2IV06/andrei/particle_rendering/provided/p91-van_der_laan.pdf) | screen-space curvature flow 用于平滑粒子液面，避免明显球块轮廓 | 可作为表面重建算法参考；该 URL 是论文演示资料，不是可自由打包的美术资产。渲染平滑也不能代替黏滞/接触模拟 |
| [NVIDIA FleX 官方 Manual](https://nvidiagameworks.github.io/FleX/1.2/lib_docs/manual.html) | cohesion 带来长丝；adhesion 使粒子贴附并沿固体滑动；surface tension 影响液滴；提供 anisotropy/smoothed particles 数据 | 行为参考很匹配口水。FleX 本身不渲染；此处不建议直接引入旧 native SDK，只借鉴约束和椭球数据布局。其代码/二进制许可必须另审，不能把公开文档等同开源许可 |
| [FleX Fluid Surface Rendering](https://nvidiagameworks.github.io/FleX/1.2/ue4_docs/FLEXUe4_FluidSurfaceRendering.html) | thickness 由粒子 sphere additive blend 生成，液面材质是受限制的 UE 材质子集 | 说明“下载一个液体材质”仍依赖专用 renderer。借鉴 pass 与 thickness 输入，不直接加载 UE 资产 |
| [AMD FidelityFX SSSR 技术说明](https://gpuopen.com/manuals/fidelityfx_sdk/techniques/stochastic-screen-space-reflections/) | SSR 输入包括 color、normal、roughness、depth hierarchy，失败时以 environment cubemap 回退 | 可参考反射设计；[官方 sample](https://gpuopen.com/manuals/fidelityfx_sdk/samples/stochastic-screen-space-reflections/)要求 DX12/Vulkan，不能直接放进当前 D3D11 pass。完整 SSR/denoising 不是最先需要的扩展 |

## 第一阶段可落实的材质管线

1. **获得干净背景。** 在液体绘制前，复制当前 scene color 到插件拥有的 texture，再创建 SRV。理想挂点在世界绘制完成、UI绘制前；实际挂点仍须验证，否则会把 UI 也折射。先记录 source 的格式、尺寸、sample count、viewport 与颜色空间。不要假设当前 backbuffer 就是 HDR scene color。
2. **不直接读写同一 texture。** D3D11 会将与 output 重叠的 SRV binding 置 NULL，因此从当前 RTV 取 SRV 后一边写一边采样是错误设计。[Microsoft PSSetShaderResources](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-pssetshaderresources)
3. **复制/resolve 正确匹配。** 同 sample count 的 compatible format/尺寸用 CopyResource；MSAA→单采样使用 ResolveSubresource。CopyResource 不做缩放或颜色转换。低分辨率 snapshot 需要另做采样 pass。[Microsoft CopyResource](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-copyresource)、[ResolveSubresource](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-resolvesubresource)
4. **法线和厚度。** 少量液滴/丝可先用光滑椭球和 spline tube 的解析 normal，厚度由局部截面近似；膜/积液由 height profile 求 normal。粒子数量增加才引入独立 fluid depth + thickness targets。液体厚度是液体内部光程，**不是** scene depth 与液面之间的距离（后者可能包含空气）。
5. **折射。** 初版以 view-space normal.xy 与厚度扰动 scene UV；厚度趋零时偏移连续趋零。深度比较拒绝被扰动到液面前方的像素，并在屏幕边缘逐渐归零。只 clamp UV 会拖出边缘条纹；仅读 scene depth 无法恢复屏幕外/被遮挡背景。闭合液滴可升级到前后表面交点 + Snell 方向 + depth ray march，但屏幕空间仍无隐藏面信息。
6. **合成。** 建议以 `F = F0 + (1-F0)*(1-NdotV)^5`，`T = exp(-absorption*thickness)`，`C = (1-F)*T*sceneRefracted + F*envReflection + directSpecular` 作为材质起点，吸收颜色保持很弱。IOR/roughness 为艺术可调参数；水样 IOR≈1.33、F0≈0.02 仅作初值，不宣称是经测量的口水参数。该 C 已含背景，使用覆盖 mask 做 lerp；再次乘常规低 alpha 会重复混入未折射背景、冲淡透镜感。
7. **环境与高光。** 用 CC0 HDRI 转 cubemap 并生成 roughness mips，支持旋转/曝光。可选 1–2个受控灯的直接 specular；不要以大量白色 diffuse 填满液体。固定 studio HDRI 适合证明透明体质感，但游戏内照明会不匹配，属于 fallback。后续可读游戏 light/probe 或做局部 SSR，工程风险更高。
8. **资源生命周期。** 使用当前 isolated context state 思路管理额外 SRV/RTV/sampler；尺寸/格式/sample count/device 变更重建，自有资源不保留 swapchain 引用；异常也恢复状态。先验收颜色复制与折射，再扩大到粒子重建。

## 可实际获取的少量资源

| 资源与获取页 | 已核实许可/格式 | 用法与限制 |
| --- | --- | --- |
| **首选：** [Poly Haven Studio Small 01](https://polyhaven.com/a/studio_small_01) | 页面提供 HDR/EXR，1K–16K；[官方资产许可](https://polyhaven.com/license)为 CC0，允许商业使用和再分发，不要求署名 | 取低分辨率 HDR/EXR 转为自有 mip cubemap，用于窄而清晰的晶莹反射。下载原始 HDRI，不下载球体 preview 当材质。站点 logo/示例 render 不是同一资产许可范围 |
| **可选测试纹理：** [TextureCan Rain Ripples on Puddle，Others 0028](https://www.texturecan.com/details/531/) | 1K/2K/4K maps 与 SBSAR 下载链接；[官方条款](https://www.texturecan.com/terms/)标明纹理 CC0、可商业使用/随项目再分发 | 仅用 normal/displacement 做地面积液细节实验；纹理本身不含实时折射，不宜把“被大量雨滴击打”直接当静慢口水。最终积液建议自生成小幅 height field；SBSAR 需要离线工具求值，D3D11不能直接加载 |
| **不选作最终材质：** [Screaming Brain Studios 140+ Liquid Textures](https://opengameart.org/content/140-liquid-textures) | 页面明确 CC0，提供 PNG ZIP，128/256/512，作者说明适合复古/low-poly | 合法且可取，但是有色外观图，缺少实时体厚与光学行为；与用户拒绝的低模观感方向冲突，不应因为有“liquid”名字就采用 |
| **商业参考：** [Kronnect Liquid Volume Pro 2](https://assetstore.unity.com/packages/vfx/shaders/liquid-volume-pro-2-129967) | 商品页标明 Standard Unity Asset Store EULA、Extension Asset；[官方 EULA](https://unity.com/legal/as-terms)、[官方 FAQ](https://assetstore.unity.com/browse/eula-faq)约束集成分发与座席。开发者[文档](https://kronnect.com/unity/lvp/doc/QuickStartGuide.pdf)描述容器内液体与 camera command buffer 折射模糊 | 适合参考厚度/容器材质，不适合直接解决唇缘长丝与贴皮肤流动。Unity shader/组件/CommandBuffer 依赖 Unity；购买后仍需重写 D3D11资源/状态管理。不可将购买包源码/纹理原样提交到公开仓库；不能据商品图宣称移植质量已验证 |
| **仅候选，不建议采购：** [WildCuts Liquid Emitter - Niagara Fluids](https://www.fab.com/listings/3606ed80-e781-4eae-977b-10af35e53d4b?lang=en) | 页面只有 Unreal Engine 格式，描述 honey/metaballs 等 presets；动态 License 控件未暴露具体成交许可，因此这里不能确认该商品最终授权 | 观看行为参考可行，Niagara assets 不可在 Dalamud 直接运行。[Fab Standard](https://www.fab.com/eula)一般允许兼容工具/商业项目、禁止独立资产再分发，但须购买时核对实际 license/tier；不能把这个一般规则当作已核实该商品许可 |

没有发现并验证可直接放入本插件的、完整且许可明确的“真实透明口水实时材质+动画”通用包。首选资源只需 **一张 CC0 HDRI**；动态法线/厚度/膜形状宜自己生成。ambientCG 的[官方许可](https://docs.ambientcg.com/license/)虽为 CC0，但本次 `Water001` 具体页面不可访问，故未把它列成已验证可下载素材。

## 形态应分开处理

- **唇缘出液：** 口/唇附着点 + 短小 meniscus/薄膜；先在唇边积聚、缓慢越过唇缘，再形成丝。持续同直径直线射流会读成水龙头；出液时序和附着位置比纹理更关键。
- **黏滞丝：** 受约束的 spline/粒子链，连续变化的截面；连接两端时拉伸变细，局部颈缩后断裂、端部回缩并并入液滴。体积决定截面积，避免每个点都成为独立球串。高 viscosity 是速度阻尼，cohesion/弹性拉伸和断裂另需约束。
- **贴皮肤滑动：** 使用动画跟随的 skin surface proxy/附着坐标、tangential gravity、adhesion 与慢速接触线。骨骼锚点/投影只是 proxy，scene depth 是当前视角的遮挡，不能当作完整身体碰撞体。建议先唇/下巴/颈部小范围，再扩展；湿膜在接触面上应薄、附着且连续。
- **地面积液：** volume 累积进入二维 height field/不规则边界；扩张时半径增长、中心厚度下降，边缘有 meniscus、平滑法线。落点注入微小扰动，平静后衰减；贴住地面并用 slope/proxy 控制流向，避免悬浮平盘和无限独立球。

这些行为方案为工程设计推断；shader 不能凭空提供 attachment、collision、质量守恒或黏滞拉丝。

## 最小实施顺序与验收

1. 做一个静止透明平滑液滴：干净 scene snapshot、法线、近似 thickness、CC0 cubemap、正确 coverage composite。验收：近处网格/背景能随镜头与液面曲率发生折射，薄边透明、掠射边缘反射，遮挡正确，缩放/场景切换正常。若这一步看起来仍是塑料，就不要扩增粒子。
2. 单根受拉丝 + 端点液滴：用同一材质；验收：截面变细、回缩、断裂连续，无低面数高光跳变/低分辨率消失。丝的最小屏幕尺寸需单独抗锯齿/coverage，不能为了可见性加成粗水管。
3. 唇缘薄膜 → 贴下巴缓慢滑动 → 落点积液连续转移；通过体积预算连接三者。验收不同视角下液体始终来自唇缘，贴皮肤而非穿模/悬浮，积液自然扩张。
4. 仅在粒子形态/融合确实需要时增加 screen-space surface reconstruction；SSR和焦散最后评估。粗糙度、光学效果、行为分别开关对比，以便定位失败来源。

报告中的网页截图、游戏演示、论文图片用于观察或技术依据，均不构成可提取资产授权。未提取任何游戏资源；生成图片同样不能提供动态体积、厚度、曲率或与游戏背景一致的折射，不是本方案的液体渲染替代品。
