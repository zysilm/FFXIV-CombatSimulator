# Garment Tube Model — 环-链衣物物理模型设计

状态:设计稿(未实现)。目标分支:在 Advanced clothing settle 之下新增实验路径。

## 1. 目标与非目标

**目标**
- 衣物在物理接管(handoff)后表现出"套在身体上"的真实行为:沿身体表面滑动、在肩/胯处挂住、
  从躯干侧面滑落到地面,而不是当前链式刚体的"整体位移 + 关节折叠"近似。
- 从根源消除出生穿插问题:环包围尸体胶囊,而不是刚体出生在胶囊内部。
- 让"落地摊平"从物理中自然涌现(环失去内部支撑后被重力压扁),替代人工 deflate。

**非目标**
- 不做通用布料模拟(粒子/XPBD)。渲染输出通道只有骨骼(脊柱 3–4 根、手臂 4 根、裙摆
  j_sk_*),模型保真度以骨骼表达力为上限。XPBD 方案留作后续演进(见 §12)。
- v1 不覆盖裤子(slot 3)和本地 sim fallback,两者保留现有链式模型。

## 2. 为什么链式模型到顶了

现有 slot-1 rig 是脊柱链 + 手臂链的刚体串(`TryBuildGarmentRigSpecs`)。它编码了衣物的
*分段*,但没有编码衣物最本质的拓扑属性:**闭合筒**。没有任何约束表达"包裹",物理上
衣服可以从身体侧面穿出、整体沉进躯干。近期的补丁(取消尸体碰撞 `collideWithRagdoll:false`、
紧限位渐放、pose-guide servo)都是用外力掩盖这个缺失属性,收益已饱和。

## 3. 核心思想

用现有 BEPU 刚体拼出一个粗糙的筒:**若干"环",每环 N 个小刚体用距离约束连成圈,
环与环之间再用距离约束连成筒**。筒套在尸体 ragdoll 胶囊外面并与之碰撞:

- 胶囊在环内 → 环被撑圆、无法塌陷,衣服"穿在身上";
- 相邻环刚体间隙 < 胶囊直径 → 胶囊无法从环的缝隙漏出,**包裹性由几何保证**;
- 重力 + 摩擦 → 筒只能沿身体表面滑动,滑过肩/胯时被更粗的截面挂住;
- 滑离身体后环内无支撑 → 被重力自然压扁,摊在地面。

## 4. 拓扑与尺寸(slot 1 上衣)

### 环布置(3 环 + 手臂保留链式)

| 环 | 锚定骨 | 环半径来源 | 刚体数 N |
|----|--------|-----------|---------|
| R0 hem(下摆) | j_kosi | 骨盆胶囊半径 0.105 + clearance | 6 |
| R1 mid(腰腹) | j_sebo_b | 胶囊半径 0.09 + clearance | 6 |
| R2 chest(胸) | j_sebo_c | 胶囊半径 0.09 + clearance | 6 |

- clearance(布料悬空量)≈ 0.02–0.03m;环半径 r_ring = capsuleR + clearance。
  胶囊半径从 `RagdollController.GetBoneDefs()` 对应骨读取,乘 `SourceScale`。
- 手臂:沿用现有 j_sako→j_ude_a→j_ude_b 链式段(袖子细,环化收益低、失稳风险高),
  肩关节从 R2 的最近环刚体上挂出(BallSocket),替代现有 j_sebo_c 挂点。
- 环刚体形状:薄盒。切向宽 ≈ 2πr/N × 0.45,径向厚 0.018(布厚),轴向高 ≈ 环距 × 0.4。
- 质量:总质量沿用 `spec.Mass`,环刚体均分 ~70%,手臂链 ~30%。单体 clamp ≥ 0.05。

### 约束表

| 约束 | 类型 | 参数 | 作用 |
|------|------|------|------|
| 环内相邻 (i, i+1 mod N) | DistanceLimit | min = 0.3×rest, max = 1.05×rest | 周长不可拉伸,允许压扁 |
| 环间同索引 (Rk[i], Rk+1[i]) | DistanceLimit | min = 0.2×pitch, max = 1.10×pitch | 筒轴向连接,允许收拢 |
| 环间交错对角(每 quad 取一条,交替方向) | DistanceLimit | max = 1.15×diag | 抗剪切,防筒身扭麻花 |
| 每刚体 | AngularMotor(target 0) | MotorSettings(0.5, 0.4) | 自旋阻尼,防抖 |
| 肩挂点 R2[nearest]→j_sako | BallSocket + SwingLimit(1.8) | 现有弹簧参数 | 袖子挂接 |

关键取舍:**环内不加防塌陷撑杆**。穿着时由体内胶囊撑圆;离体后允许压扁正是想要的
落地形态。DistanceLimit 的 min 值只防止数值上完全重叠。

弹簧参数起点:DistanceLimit 用 SpringSettings(15, 1.5)(比现有 BallSocket 的 (9, 1.4)
硬——距离约束是布料不可拉伸性的载体,太软会"橡皮筋")。

## 5. 生成流程(handoff 时,替换现有 TryCreateRagdollGarmentRig 的 slot-1 分支)

1. 从 clone 骨架(已含 slip 偏移的姿态)取各锚定骨的世界位置与脊柱段轴向。
2. 每环:在垂直于脊柱轴的平面内均布 N 个刚体,位置 = 锚点 + r_ring × 方向(i)。
   方向 0(seam,缝线基准)取骨骼世界旋转的 +Z(胸前方向),供骨骼回写定 roll。
3. 刚体朝向:切向对齐(盒的宽边沿圆周切线,薄边沿径向)。
4. 速度种子:沿用 `ResolveGearBodySeedVelocity`(释放速度),无激活冲量。
5. 约束按 §4 建立。ExternalRig API 需扩展:`ExternalRigJointSpec` 增加类型字段
   (BallSocketSwing | DistanceLimit),`AddExternalRigJoint` 按类型分派。
6. **肩部起滑(已定决策)**:slot-1 tube 出生时套在肩/胸原位,由物理承担整个下滑。
   因此 tube 路径下把视觉 slip 降到最小(仅保留 ClothHoldMinFrames 的贴身,slip≈0),
   让环生成在 j_kosi/j_sebo_b/j_sebo_c 的实际穿着位置,重力+摩擦驱动完整下滑。
   （对比:链式模型靠 slip 把衣服"预滑"到胯部再交给物理,是掩盖链式无法自然下滑的手段;
   tube 不需要。）环与胶囊初始为轻度贴合而非深穿插。
   实现点:`ComputeAutoBindSlip` / 手动 slip 在 `UseGarmentTube(c)` 为真时对 slot-1 返回 ~0;
   或更干净地,在 tube 分支跳过 visual-bind 的 slip 累积,handoff 判定仍按 ClothHoldMinFrames。

## 6. 骨骼回写

新增 `GarmentRing` 驱动数据(替代环刚体的 per-body 骨骼映射;手臂仍走现有
`DriveGarmentRigBones` 的 per-body 路径):

```
GarmentRing {
  int[] BodyIndices;          // N 个环刚体
  int BoneIndex;              // 锚定骨
  Quaternion CapturedFrameInv; // 出生时环坐标系
  Quaternion CapturedBoneRot;  // 出生时骨骼世界旋转
  Vector3 CapturedOffsetLocal; // 骨骼相对环质心的偏移(环坐标系)
  Quaternion SmoothedFrame;    // 帧间平滑状态
}
```

每帧:
1. 质心 c = mean(body positions)。
2. 环坐标系:法线 n = Σ (p_i − c) × (p_{i+1} − c) 归一化(环面法线,即筒轴向);
   seam 方向 s = (p_0 − c) 在垂直 n 平面上的投影;frame = LookRotation(n, s)。
3. **退化处理**:|n| < ε(环压扁)时保留上一帧 frame 的轴向,只更新质心;
   frame 用 slerp(SmoothedFrame, frame, 0.4/substep) 平滑,防接触抖动直传骨骼。
4. 骨骼世界位置 = c + frame × CapturedOffsetLocal;
   骨骼世界旋转 = frame × CapturedFrameInv × CapturedBoneRot。
5. 转 model 空间写入(沿用 `WriteBoneTransform` + 传播,子骨/裙摆骨自动跟随)。

base transform:沿用 `ResolveGarmentRigRootPosition/Rotation`(腰部锚定 + bind 旋转),
质心来源改为 R0 环质心。

Phase 2(可选):hem 环的每个刚体直接映射最近的 j_sk_*_a 裙摆骨,下摆呈现不均匀
垂坠;替代 `DriveSkirtHang`。

## 7. 碰撞与材质

- 环刚体 **恢复与尸体 ragdoll 的碰撞**(不加入 `externalRigNoRagdollContactBodyHandles`
  ——tube 的意义所在)。材质走现有 gear 软接触(recovery 0.08,摩擦 0.45–0.9)。
- 摩擦是"滑而能挂"的关键旋钮:起点 0.55,调参区间 0.4–0.8。太高衣服黏在身上,
  太低秒滑到底。
- 同 rig 内部刚体之间:**全部禁用碰撞**(把同 rig 所有 pair 加入 connected-pairs 集合,
  或新增 per-rig no-self-collision 标志)。v1 不做布料自碰撞,防接触堆积失稳。
- 不同 garment rig 之间(上衣 vs 裤子):禁用(评审已发现现状互推问题,顺带修复)。
- 环 vs 地面 static:沿用 gear-ground 材质(recovery 0.25、高摩擦)。
- 环 vs 单件掉落(帽子等 external):沿用现状禁用。
- NPC 打击 kinematic:保持允许(踢尸体时衣服被带动)。

## 8. 与现有系统的交互

| 系统 | 处理 |
|------|------|
| Cloth hold presets / slip | 不变。tube 只改变释放后的行为 |
| Visual-only preset | 不变(永不 handoff,与 tube 无交集) |
| ApplyGarmentHandoffDrag | tube rig **禁用**(真实接触替代人工拖拽;两者会打架) |
| Pose-guide servo(fa8bc73) | tube rig 不建 servo(包裹性替代保形);链式路径保留 |
| SwingLimit 渐放窗口 | tube 的环约束不需要;手臂链沿用 |
| deflate / collapse | tube rig 跳过(ShouldSkipGarmentDeflate 已覆盖 rig != null) |
| UpdateGearSettleProgress / 休眠 | 兼容(avg/max 速度统计对更多刚体同样成立) |
| RemoveExternalRig / 清理 | 兼容(Bodies/Constraints 列表泛化,只是数量变多) |
| 本地 sim fallback | v1 保留链式模型(本地 sim 无尸体碰撞体,tube 无意义)。 后续如需:用 pcNpcCollision 的 kinematic 代理机制镜像玩家尸体骨骼 |

## 9. 宿主策略

v1 仅在 `TryCreateRagdollGarmentRig` 路径启用(玩家 ragdoll sim 存在且 ready)。
判定失败 → 现有链式 rig(ragdoll 或 local)不变。即 tube 是第三层:
tube(host) → chain(host) → chain(local) → 单刚体 → 纯视觉。

## 10. 性能预算

- slot-1 tube:18 环刚体 + 4 手臂刚体 ≈ 22 刚体;约束 ≈ 18(环内)+ 12(环间)
  + 6(对角)+ 6(手臂)+ 22(阻尼)≈ 64。
- 宿主 ragdoll sim 现有 ~25 刚体、4 迭代 solver;tube 约使 sim 规模翻倍,预估
  单件 < 0.3ms/帧(桌面 CPU),两件(上衣+裤子 v2)< 0.6ms。可接受。
- 护栏:同时存活的 tube rig 超过 2 个时,后续衣物退回链式模型(常量上限)。

## 11. 风险与缓解

| 风险 | 缓解 |
|------|------|
| 薄盒 + 多接触抖动 | 加厚径向(0.018→0.025)、AngularMotor 阻尼、frame 平滑(§6.3) |
| 环压扁时骨骼朝向退化 | §6.3 的退化回退 + slerp 平滑 |
| 尸体被高速踢飞时胶囊从环缝漏出 | max 拉伸限制间隙;speculative margin;接受罕见漏出(衣服掉落即可) |
| 环驱动脊柱骨导致衣服网格急弯 | 每帧骨骼 delta 限幅(位置 ≤ 0.05m/substep,旋转 ≤ 0.12rad) |
| slip 后环套在胯部,肩部悬空 | 预期行为(衣服已滑落);观感不对再把 R2 锚点上移 |
| 调参盲调低效 | 先做 §12 Phase 0 的 debug 绘制 |

## 12. 分阶段落地

- **Phase 0**:ExternalRig API 扩展(约束类型字段)+ `RagdollDebugOverlay` 增加
  garment rig 刚体/约束线框绘制开关。无行为变化。
- **Phase 1**:slot-1 躯干 3 环 + 手臂链,host-only,骨骼回写 + 退化处理。
  验收:KO 掉衣,衣服套在尸体上→沿躯干侧滑到地→压扁摊平,无爆开、无穿墙。
- **Phase 2**:hem 环→裙摆骨映射;摩擦/材质调参;堆叠观感。
- **Phase 3**:slot-3 裤子(胯环 + 每腿 1–2 环);视需要做本地 sim 尸体代理。
- **远期**:XPBD 粒子代理 + shape-matching 回写(复用 §6 的骨骼映射层)。

## 13. 开关

- 复用 `KoStripAdvancedClothPhysics` 为总开关;新增内部常量或 dev-only 开关
  `GarmentTubeModel`(默认 off)控制 tube vs chain,便于 A/B 对比与回退。
