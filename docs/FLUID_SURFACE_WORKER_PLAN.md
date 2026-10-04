# Fluid surface topology worker plan

2026-10-04。MDL 文件读取、解压和元数据解析已使用 managed worker。本轮源码进一步把 adjacency、clusters、静态 centroid 计算和 pose 缓冲分配移到 worker；尚未构建、部署或实机验证。三角解码、native shape/bone-map 快照及唇部候选筛选仍在 Framework。下述完整顶点解码和拓扑缓存部分仍是后续计划。

本轮 worker 独占 `points.ToArray()` 和 `triangles.ToArray()` 的私有托管数组，静态构建方法不访问实例、原生地址、当前 pose 或 skin matrices。Framework 只轮询已完成的任务，核验 generation、actor/object/territory、draw/skeleton、scope 及所有 resource/data/attribute/shape 签名；现有 `UpdateActor` 同时核验 partial 连接。核验成功后一次安装所有数组和 clusters，再按原有 face profile 恢复已验证唇锚。等待期间明确返回 topology pending，不改变液体库存或时间累积；取消旧任务只发 token，不等待，旧 worker 无法写当前表面。

诊断新增 `topologySnapshot`（Framework 顶点解码及快照）、`topologyWorker`（邻接及 clusters）、`topologyInstall`（原子交换和唇候选恢复）与 `topologyBuild` 状态。必须用冷 face/full、scope/shape/redraw 切换的真实游戏数据确认收益；不能把整个历史 40 ms 都算作已经移出 Framework。

## 当前实机证据

16:23:55 完整身体 generation 7 包含 48,982 triangles、56,892 vertices。各 slot 读取约在 55.921 完成，最终 surface 日志在 55.961；随后 Framework 统计 max 为 44.682 ms。因此后台文件读取已经解决之前约 396 ms 的同步读取问题，但主线程拓扑安装仍有约 40 ms 冷态工作，不能声称初始化卡顿已经解决。

16:24:51/56 的实际碰撞查询达到 4,096 triangle tests，出现 budgetHit 和 deferred simulation；pose p95 约 0.9–1.0 ms。dominant owner 现已比较八个影响，但这只修正分组，性能改善仍须游戏测量。`CreateCluster` 对每个 triangle 的三个顶点保留全部八个 nonzero influence bounds；owner 不参与丢弃权重。

## 工作边界

Framework 负责读取当前合法 draw/model/partial 身份、UTF-8 路径、可见 attribute mask、shape 名称到 native index 的映射，以及模型骨名到当前 partial 的已验证映射。原生指针和 native identity table 留在 Framework。worker 接收托管 MDL bytes/metadata、托管骨名映射、resolved active-shape 列表和不含地址的 request ID；不读取 actor/model/pose，不调用 native `GetShapeIndex`，不写骨骼或物理。

在已经完成的 MDL decode 阶段之后，Framework 再复制一次托管 topology request。worker 完成 vertex/index 解码、八权重校验、shape 索引替换、可见 submesh 过滤、邻接、影响 bounds、静态 centroid、空间 partition 以及 lip candidate metadata。预计算 centroid，避免 recursive sort 的 comparer 反复读取三顶点并重新求中心。

worker 产物是独立、不可变的 topology：vertices、faces、邻接、clusters、所有影响 bounds、face/lip candidate IDs 和 diagnostics。skin matrices、当前/上一帧位置、frame stamps、每帧 budget 仍由 Framework/final-pose 路径持有；worker 不计算实时 pose。

## 安装、取消和缓存

一组 face/full 请求全部完成后，Framework 验证该 request 的当前 draw、resource、partial、attribute 和 shape 快照仍匹配，再一次交换 topology；不逐 slot 安装、不因单个 worker 完成反复更换 Generation。等待期间暂停依赖该表面的模拟，保留已有体积，不把 unavailable query 当成 miss。

身份或 mask 改变时取消旧 request，并在 worker 的有界循环检查 cancellation。旧 worker 即使晚完成也只能返回托管结果，不得安装进新 generation。失败输出明确的 unsupported/cancelled/budget 状态，继续使用现有受控发射门槛。

缓存至少区分实际文件 path/mtime/size、托管模型内容身份、resolved shape/visibility、partial bone-map signature 和 scope。原生 resource identity 只在 Framework 验证/cache 索引中使用。限制托管总字节和 topology 数量，并避免 active batch 尚未完成时逐项清空它自己的 cache。

成功安装后，按既有 face identity、mesh/index entry、resolved vertex IDs 和 barycentric 复绑已验证 lip profile。真实 redraw/model 替换失效；不把新模型按旧 vertex ID 当作可信唇缘。

## 验证

只通过真实游戏 controller/render 路径验证：冷 face/full 启动、重复缓存启动、shape 切换、redraw、关闭/重开及 scope 切换。分别记录 Framework snapshot/atomic install、worker decode/build、pose skin/query 的 p95/p99/max 与初次 hitch，并确认稳定唇标记、连续膜、所有八权重影响和守恒延迟。禁止以独立模拟脚本或计数替代游戏画面。
