# 过场跟拍镜头清单（Cutscene Shots）

状态：CS-1（离线筛选）完成，清单在 `CombatSimulator/Resources/CutsceneShots.json`；CS-2（插件内播放，§6）已实现，离线检查通过，**待游戏内验证**。

## 1. 用途

用户要在插件 Camera 页签里用可搜索的下拉菜单，从游戏自带过场里挑一个**跟拍镜头**（摄像机根挂在角色身上）给胜利、一个给死亡，然后播放。固定在世界坐标里的镜头对用户没用，不进清单。

这个文件只是一份**引用清单**：哪段过场、哪条时间轴、哪几个镜头、哪段帧，加上我们自己算出来的特征和名字。镜头曲线、关键帧等游戏数据一概不进仓库，插件运行时从用户本机的游戏安装里读。

## 2. 怎么做出来的

全部按数值自动算，不靠人工看片。详细规则和阈值见 `Tests/CutsceneCamera/README.md` 的 “Curated follow shots”。概要：

1. **候选**：Cutscene 表里每段过场、每条时间轴，先解出默认镜头序列（无条件轨道、后者覆盖前者）；连续的、根挂在同一角色 A 上、看同一个目标的镜头组成一段。看的是镜头内偏移或 A 自己 → 单人；看的是另一个角色 B → 双人。超过 15 s 的按镜头边界切成 ≤12 s 的窗口，保留 3–15 s 的段。
2. **特征**（在 A 自己的坐标系里每 2 帧采样，所有角色都用人族中原男的参考骨架与参考姿态）：景别（特写/中景/全身/远景，按 FOV 与距离算出的画面高度）、机位高低（仰/平/俯）、方位（正面/侧面/背后）、运镜（静止、推、拉、左/右环绕、升、降、摇、跟移）、是否有滚转、时长、剪辑次数。
3. **质量过滤**：人形尺寸（挂点都是人形绑定点、注视点和距离在人的尺度内，排除怪物尺寸）、FOV 15–80°、眼位在地面以上、滚转 ≤25°、镜头内无跳变（剪辑处不算）、主体留在画面内 ≥85%。
4. **适合尸体**：只用骨骼挂点（头 1/26/27、胸 28、腹 18、武器 77/78 等）的段标 `corpseSuitable: true`；用到根高度挂点（`n_root` 上的 76/42/71 等）的标 false。插件会把根高度挂点映射到躺地尸体的头/胸骨骼，但死亡镜头优先选骨骼挂点的。
5. **双人方向**：一条双人镜头表现的是它拍到的那张脸。摄像机在 A 前方/侧面且 A 在画面里 → `follow: subject`（跟主体，看对方）；摄像机在 A 背后或 A 基本不在画面里（过肩拍 B 的脸）→ `follow: other`（跟对方，看主体）。“主体”在胜利时是胜者，在死亡时是尸体，由插件决定绑定给谁。
6. **挑选**：按 种类 × 景别 × 运镜 分类，每类先取最好的一条再取第二条；双人每个方向补到 20 条，适合尸体的补到 40 条；总数不超过 150；同一过场文件最多一条；被其他过场复用的同一套镜头只取一次。
7. **名字**：由特征自动生成，例如 `Slow push-in, low close-up, profile`、`Follow other, see subject, pan full`。重名的加资料片标签（ARR/HW/SB/ShB/EW/DT，取自路径前缀），仍重名再加序号。`category`（如 `single/close-up/push-in`）和 `tags` 供搜索。

## 3. 怎么重新生成

```
cd Tests/CutsceneCamera
dotnet run -c Release -- curate            # 写 CombatSimulator/Resources/CutsceneShots.json
dotnet run -c Release -- curate out.json   # 写到别处
```

需要本机装有游戏（路径写在 `Tests/CutsceneCamera/Program.cs`）。结果确定：同一游戏版本跑两次逐字节相同。游戏更新后重跑即可；`id` 由文件名、时间轴、首镜头号组成，同一游戏版本内稳定。

## 4. 格式

顶层 `{ "version": 1, "source", "note", "shots": [...] }`，每条：

| 字段 | 含义 |
|---|---|
| `id` | `<过场文件名>-t<时间轴>-s<首镜头号>` |
| `name` | 下拉菜单显示名（英文，多数 ≤6 词） |
| `category`, `tags` | 搜索用：种类/景别/运镜，及方向、`slow`、机位、方位、`roll`、`cuts`、`corpse`、资料片 |
| `cutscene` | Cutscene 表的 Path，文件为 `cut/<path>.cutb` |
| `timeline` | CTTL 块在文件里的序号 |
| `firstShot`, `lastShot` | 镜头号 = 该时间轴里 C004 条目的文件顺序序号 |
| `shots[]` | 每个镜头可见的部分 `{shot, from, to}`，时间轴帧（30 fps），左闭右开 |
| `startFrame`, `endFrame`, `duration`, `cuts` | 整段帧范围、秒数、剪辑次数 |
| `kind` | `single` / `two-person` |
| `follow`, `lookAt` | 跟谁（`subject`/`other`）、看谁（单人为 null） |
| `followActor`, `lookActor` | 原过场里的角色序号（根挂接 / 注视挂接） |
| `corpseSuitable`, `attachPoints` | 是否只用骨骼挂点；用到的绑定点编号 |
| `features` | `framing`、`angle`、`side`、`motion`（最长镜头的运镜）、`slow`、`motions`（逐镜头）、`roll`、`maxRoll`、`fovMin`、`fovMax`、`distance`（米，中位数）、`quality`（排序分） |

## 5. 已知局限

- 所有角色按人族中原男的参考姿态算；过场里坐着、跪着的角色，以及拉拉菲尔/鲁加族的实际头高会偏，景别和“主体在画面内”只是近似。
- 运镜按镜头首尾两帧判，往返式运动会被判为静止或别的运动。
- FOV 是垂直还是水平、c117 是否就是滚转（度），仍待游戏内定标；景别阈值按垂直 FOV 定。
- 左右（环绕方向）按右手坐标系、角色面向 +Z 推算，未在游戏内核对。
- 播放时的局限见 §6.7。

## 6. 插件内播放（CS-2）

### 6.1 代码落点

- `CombatSimulator/Camera/Cutscene/`：解析器（`CutbFile`、`TmbTimeline`、`CameraShot`、`Eid`、`CutsceneSequence`）和播放用的纯计算（`CutsceneShotCatalog` 清单、`CutsceneShotLoader` 读游戏文件、`CutscenePlayback` 采样、`CutsceneAnchoring` 挂点、`CutsceneRoles` 角色、`CutsceneView` 视图矩阵）。**这个目录里的文件一律不碰游戏**（只吃字节数组和读文件的委托），`Tests/CutsceneCamera` 直接链接编译它们——只有一份解析器。
- `Camera/CutsceneShotDirector.cs`：触发、角色解析、相机写入、日志；`Camera/CutsceneProbe.cs`：被动定标探针；`Camera/Configuration.CutsceneShots.cs`：两个设置；`Gui/MainWindow.CutsceneShots.cs`：界面。
- 清单作为嵌入资源进 DLL；镜头曲线和种族 `.eid` 在运行时用 Dalamud 的 `IDataManager` 从用户本机游戏读（`.cutb` 按条目缓存，`.eid` 按种族缓存），不落盘。

### 6.2 播放

- 一段 = 清单里的几个镜头首尾相接；按 30 帧/秒的时间轴帧换算，每帧用曲线连续采样（Hermite），到 span 边界立即切到下一个镜头（硬切，不混合）。
- 相机：播放期间是 `CameraModeCoordinator` 里优先级最高的 `CutsceneShot`（60）；它向协调者要与目标视角匹配的游戏 FoV、冻结鼠标/手柄转视角；真正的位姿在游戏相机更新**之后**写（`GameCameraUpdateHook.PostCameraUpdate`，与受击震屏同一处，震屏仍叠在上面）：眼位、注视点、视图矩阵（场景相机和渲染相机）、投影矩阵（只改 M11/M22，保持宽高比和深度项）。视图矩阵的布局不靠猜：每帧用游戏自己刚算出的矩阵比对四种布局，取能复现它的那种（日志 `CutShot: view matrix layout …`）。
- 进入时从当时的画面 0.3 s 平滑过渡；结束时先把相机还给原来的主人（协调者），再用 0.3 s 过渡到游戏此刻自己的相机。
- Directed 相机的死亡构图：镜头播放期间暂停（`DynamicCameraController.HoldForCutscene`），交还时从镜头最后的画面重新开始它的平移。Enemy Control、Follow-bone 相机在镜头期间被压住，结束后照常。Fighting Mode 下不播；私有场景占用相机时不播。
- 保护：游戏自己的过场（`OccupiedInCutSceneEvent` / `WatchingCutscene` / `WatchingCutscene78`）或 GPose 开始时立即交还相机（不过渡），这时也不开播；`Tick` 超过 0.5 s 没跑（且已过不止一次相机更新）时停止写相机（`tick stalled`）；视图或投影矩阵有非有限元素时跳过该帧，写入的 FOV 夹在 1°–170°；镜头的绑定父链在读取时检查（最多 16 层、不许成环），坏的条目跳过。
- 选定胜利/死亡镜头时（以及插件加载后的第一帧，针对已保存的选择）预读 `.cutb` 和参考种族 `.eid`，第一次触发不再卡一帧；都在主线程。

### 6.3 挂点（`CutsceneAnchoring`）

- 每个角色按自己的种族读 `.eid`（人族以外用参考种族 c0101，并按实测头高缩放根高度偏移）；挂点的坐标系 = 挂点位置 + 角色朝向（与离线筛选时相同）。
- 骨骼挂点（头 1/26/27、胸 28、腹 18、武器 77/78 …）：活骨骼位置 + `.eid` 偏移（按骨骼旋转）；角色布娃娃生效时用布娃娃刚体位置（没有刚体的骨骼就近替代：n_hara→j_kosi、武器→手、j_kao→j_kubi）。
- 根高度挂点（`n_root` 上的 76/42/71 …）：站着时 = 角色位置 + 朝向 × 偏移；倒地（死亡或布娃娃）时 76、42 及 1.3 m 以上的点改挂活的头骨 `j_kao`，71 及更低的点改挂骨盆 `j_kosi`。
- 眼位不低于跟拍对象脚下地面 + 0.15 m（倒地时地面取 min(角色位置, 骨盆 − 0.2 m)）。

### 6.4 角色（用户定的规则，`CutsceneRoles`）

| 情形 | 主体（subject） | 对方（other） |
|---|---|---|
| 死亡 | 玩家（尸体） | 杀死玩家的人 |
| 胜利，玩家活着 | 玩家 | 最后倒下的敌人 |
| 胜利，玩家已死 | 给最后一个敌人最后一击的队友；不知道就取离玩家最近的活着的队友 | 最后倒下的敌人 |
| Play Now | 玩家 | 当前目标，没有就取最近的活着的敌人 |

单人镜头只用主体。双人镜头里“跟拍的过场角色”（`followActor`）取条目的 `follow` 角色，“被看的过场角色”（`lookActor`）取 `lookAt` 角色。缺了必需的人就不播，日志写原因（Play Now 还在聊天栏提示）。“最后倒下的敌人”和“谁打的最后一击”由插件从引擎的伤害事件（`CombatEvents.DamageLanded`）记录。

### 6.5 界面与设置

Camera 页签 → 折叠栏 **Cutscene Shots**：一个可搜索的下拉菜单（搜索框按名字和标签匹配，所有词都要命中），三个按钮 **Use for Victory**、**Use for Death**、**Play Now**，下面两行 **Victory: …**、**Death: …**（选 None 再按按钮，或点行尾的 x 清除）。设置 `CutsceneVictoryShot`、`CutsceneDeathShot`（清单 id，空 = 关）。

### 6.6 日志与探针

- 每次播放一行 `CutShot: play <id> "<名字>" (<触发>, <单人/双人>, <秒>, <镜头数>, points …) — <角色规则>; subject …, other …; camera follows …, looks at …`，结束一行 `CutShot: end <id> after <秒> (<原因>); remaps/fallbacks: …`；跳过时 `CutShot: <触发> shot <id> skipped — <原因>`。
- `/combatsim cutprobe start|stop|status`：看游戏自己的过场时，逐帧把渲染相机（位置、注视点、视图/投影矩阵、各 FoV 字段）、正在播的过场（`ScheduleManagement.CutSceneController.CutsceneId` = Cutscene 表行号，及出现后的秒数；停止时日志写出对应路径）、相机 40 m 内最近 12 个角色的根骨和头骨世界位姿写进插件配置目录的 `cutprobe-<时间>.csv`。只读，不写游戏内存、不调游戏函数；最多 10 分钟。过场内的时间轴帧号拿不到，只能用出现后的秒数近似。

### 6.7 假设与局限（待游戏内定标）

- c116 按**垂直** FOV（度）、c117 按绕视线的滚转（度、正号）处理；各是 `CutsceneLens` 里的一个常量（`FovIsVertical`、`RollSign`），探针数据出来后可翻转。
- 相机位姿在游戏更新后整体覆盖；若游戏的剔除用的是覆盖前的视锥，画面边缘或身后的物体可能缺失；穿墙/穿地不做碰撞检测，只有眼位不低于地面一条。
- 倒地时为站姿构图的镜头会被地面限制压低：离线检查中躺地尸体的采样有约 42%（适合尸体的条目也约 42%）需要抬到地面以上。
- 布娃娃只有主要骨骼的刚体，布娃娃生效时骨骼挂点的小偏移（≤0.15 m）不再按骨骼旋转。
- 离线检查：`Tests/CutsceneCamera -- verify`（解析器回归哈希、149 条全部可读、首/中/尾采样有限、站姿平移旋转不变性、躺姿重映射、五种角色情形、视图矩阵布局识别、FOV 夹取、坏绑定父链被拒）。
