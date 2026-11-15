# 过场跟拍镜头清单（Cutscene Shots）

状态：CS-1（离线筛选）完成，清单在 `CombatSimulator/Resources/CutsceneShots.json`；插件侧播放是下一阶段，尚未实现，也未在游戏内验证。

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
- `Resources/CutsceneShots.json` 目前还没有登记为插件的嵌入资源（`CombatSimulator.csproj` 不在本阶段范围），由插件阶段加上。
