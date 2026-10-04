# 只读种族变形数据

`FluidRaceDeformation` 独立解析稳定的 managed PBD 字节快照，不访问游戏指针。它验证文件范围、矩阵有限性、骨名和层级，再提供源到目标的父链及按骨权重执行的逐阶段预变形。只有正向已验证父链受支持；缺少正权重骨骼矩阵时失败，不猜测 identity 或未验证的父骨。

`FluidRaceDeformationSource` 在游戏 Framework 上通过 Penumbra 的对象 collection、原游戏 MDL 路径反查及 PBD 路径解析采集候选身份。目标来自已映射 SDK 的 `Human.RaceSexId`。源来自原游戏路径，不能从 mod 磁盘文件名推断；路径歧义或 IPC 不可用时失败。该数据明确不是原生每 slot deformer 链的直接观察。

参考事实：[Meddle PBD 布局](https://github.com/passivemodding/meddle/blob/main/Meddle/Meddle.Formats/Files/PbdFile.cs)、[其顶点预变形顺序](https://github.com/passivemodding/meddle/blob/main/Meddle/Meddle.Utils/MeshBuilder.cs)。本实现独立编写，未复制或改编该参考项目代码。

## 2026-10-04 实际游戏验证

- 当前角色运行 Penumbra Bodymod 和 Glamourer 外观。实际胸部、手、腿、脚与连接模型原游戏路径为 `c0201`，SDK 目标为 `c0801`。
- 对象 collection 解析的实际 `human.pbd` 来自 Bodymod 的 `yet another skeleton/posing`，文件大小316288字节，不是默认游戏数据。
- PBD 解析成功，201→801 为一个变形阶段；上述主要身体模型顶点未因 PBD 缺骨被拒绝。
- 在同一游戏视图运行 raw 和 corrected 全身采样线框，截图确认修正减少了躯干、手臂附近的偏离。脸部801→801不需要这一步。
- 1024三角形的稀疏线框只能验证可见部位，不能证明完整身体、动画和碰撞全部准确。腿部存在原有 shape 冲突 mesh 省略；尾部反查路径不合格，仍明确报告未校正。
- 全身线框运行的稳定 pose p95约0.628ms；这不是完整出液性能证明，Framework仍记录10.643ms冷峰。

此提交只提供解析器和只读身份采集 API；运行时表面的集成仍处于工作区诊断验证阶段。未启用液体模拟的默认 PBD 修正，也未宣称口水功能完成。所有行为验证均在实际游戏内完成，未运行独立模拟脚本。
