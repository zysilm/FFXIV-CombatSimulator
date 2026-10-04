# 世界空间几何绘制 API

公共入口位于 `CombatSimulator.Rendering.WorldGeometry`，由 `CombatSimulatorPlugin.WorldGeometry` 提供共享服务。模块独立于口水的发射、皮肤采样、物理和配置，可用于世界标记、调试形状及其他三角形特效。每个新功能通过构造函数接收同一个 `WorldGeometryRenderer`，不要重复创建底层服务。

## 使用方式

```csharp
// 在功能控制器的构造函数中创建，控制器 Dispose 时释放。
WorldGeometryLayer layer = renderer.CreateLayer("My world marker", capacity: 96);
WorldGeometryBuilder mesh = new(capacity: 96);

mesh.AddEllipsoid(worldPosition, 0.03f, new Vector4(1, 0.3f, 0.2f, 0.9f));
layer.SetEnabled(true);
layer.SubmitFrame(mesh.Vertices, shaded: false);

// 最新提交会保留显示；位置变化时 Reset、重建、重新提交。
// Clear/关闭/Dispose 只影响本层，不影响其他功能。
layer.Clear();
layer.SetEnabled(false);
layer.Dispose();
```

`WorldVertex` 包含世界位置、法线与 RGBA；也可直接提交自建 `ReadOnlySpan<WorldVertex>` 三角形列表。Submit 在返回前复制数据，调用方随后可以重用自己的数组。无需将游戏裸指针、D3D 对象或相机矩阵传给绘制层。

`WorldGeometryBuilder` 提供 `AddTriangle`、`AddEllipsoid`、`AddTube`、`AddSurfaceBead`、`AddSurfaceTriangle`，并暴露 `Count`、`Vertices`、`Overflowed`、`Reset`。它复用固定缓冲，构建函数不做 GPU 操作；表面几何需要调用者提供接触点与法线，不会自行查询地形或身体。

## 所有权与容量

- Plugin 拥有一个 Renderer，在 Framework 调用其 `Tick()`，卸载时最后 Dispose。
- 功能控制器各自拥有一个 Layer，开启、更新、清空、释放均按层隔离。
- 最多 8 层；每层有固定容量，帧合计最多 32766 顶点。只提交完整三角形，不扩大缓冲应对超限；容量按功能需要指定。
- 三个有界帧槽在提交线程与消费线程之间传递复制后的几何；合并槽记录各层范围与选项。
- 切图和 logout 由 Plugin 调用 `ClearAll()` 清空所有世界几何；消费者应同时重置自己的模拟/有效期。
- 一套原生 hooks 和共享 shader/动态 GPU 缓冲负责所有层。效果关闭和没有几何时不上传顶点。

## 渲染行为

默认使用场景 reverse-Z 深度，保持游戏 UI 之后的正常绘制顺序。`shaded: true` 使用简单高光材质，`false` 为不受光照影响的 RGBA，适合调试标记；两者均默认接受场景遮挡。无有效主 target、深度或支持的视图时跳过；故障停止本服务，不阻止原生命令执行。

用于诊断的 `testSceneDepth`、`clipSpace` 和 `diagnostic` 参数允许对照测试。生产调用使用默认世界坐标与深度测试；GPU 深度读回仅在明确请求的诊断中进行。

游戏 ViewMatrix 的齐次列在实机中为零/未定义填充。服务只在自己的副本中补齐 `(0,0,0,1)`，再乘游戏投影矩阵；不写相机。这个修正经实机三维标记与人物遮挡验证。底层使用 D3D11.1 context state 保存/恢复完整渲染状态，私有状态清空以免保留 backbuffer 影响 resize。

现阶段的限制：DynamicPortrait 加载时暂停服务，主视图识别尚无可靠契约；透明物体按层/提交顺序混合，没有完整透明排序或 OIT；材料不支持场景折射、贴图或自定义 shader。当前 API 是插件内共享能力，尚未提供跨插件 IPC。

## 实机复用验证

`/combatsim geometry marker` 使用独立的 API preview 层显示 8 秒标记，`/combatsim geometry clear` 只清除该层。与 `/combatsim fluid on` 同时运行，可以检查口水和标记共存，以及关闭一方后另一方仍正常显示。旧 `/combatsim fluid marker` 保留分段投影/深度诊断，属于口水层，运行时会清空当前口水模拟。

提取前已验证位置与人物深度遮挡；多层共享后的实机回归结果记录于 [体液实现状态](BODY_FLUIDS_IMPLEMENTATION_STATUS.md)。
