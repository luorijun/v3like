# 离线自然地理数据构建方案

本文记录地形与水文数据的来源、清理方式、静态资产和视觉表达。全球逻辑网格固定为 `1,281,642` 个游戏地块；地形使用独立的等角 Cube-sphere 和四叉树 LOD，不与逻辑地块共享拓扑。

地形细分参数由资产构建配方决定，不属于设计常量。本文使用以下符号描述由参数决定的规模：

```text
C = ChunkResolution
L = MaximumLod
I = (C - 1) × 2^L       // 每个 Cube Face 在最高 LOD 的间隔数
R = I + 1               // 每个 Cube Face 的最高精度参考高程分辨率
Q = (4^(L + 1) - 1) / 3 // 每个 Cube Face 的四叉树节点数
```

运行时必须使用静态资产内记录的参数，而不是依赖代码外另写一份固定数值。

## 1. 输入数据

| 用途 | 数据源 | 获取量 | 使用方式 |
| --- | --- | ---: | --- |
| 陆地高程与海深 | [NOAA ETOPO 2022 60″ Ice Surface NetCDF](https://www.ngdc.noaa.gov/thredds/fileServer/global/ETOPO2022/60s/60s_surface_elev_netcdf/ETOPO_2022_v1_60s_N90W180_surface.nc) | 456 MiB | 主高程源；CC0，引用 DOI `10.25921/fd45-gt74` |
| 海陆边界 | [Natural Earth 1:10m Land / Ocean](https://www.naturalearthdata.com/downloads/10m-physical-vectors/) | 约 6 MiB | 生成海陆掩膜；公共领域 |
| 河网 | [HydroRIVERS v1](https://www.hydrosheds.org/products/hydrorivers) | 544 MB | 河道中心线、`NEXT_DOWN`、河序、上游面积和平均流量 |
| 湖泊 | [HydroLAKES v1](https://www.hydrosheds.org/products/hydrolakes) | 763 MB | 湖泊面、出水口、河湖关联、面积和类型；CC BY 4.0 |
| 重要海峡 | 项目内人工表 | 小于 1 MiB | 中心线、宽度和通航等级 |

[Marine Regions Gazetteer](https://www.marineregions.org/gazetteer.php?p=webservices) 只用于按需校验海峡名称和位置。OSM 不作为首版依赖。

源数据清单必须记录下载地址、SHA-256、坐标与垂直基准、许可和引用文本。数据无需追踪最新版本，但必须能够重现构建结果。

## 2. 处理流程

### 2.1 标准化

1. 保留源要素的稳定键，将经纬度统一转为地心单位方向。
2. 离线采样、滤波和坐标转换使用 `double`；不产生全量文本或中间 GeoTIFF。
3. 处理跨日期变更线的矢量要素，再分别投影到游戏地块拓扑和 Cube-sphere 地形。

### 2.2 构建地形与水体分类

1. 每个 Cube Face 的最高精度参考高程为 `R × R` 个样本。六个面在资产中分别保存，共 `6 × R²` 个样本；面边界在球面上必须得到一致结果，但资产格式不要求跨面去重存储。
2. 按每个目标顶点的球面足迹对 ETOPO 做面积加权低通滤波。
3. Natural Earth 定义海洋与陆地，HydroLAKES 补充内陆水体。高程正负不用于判定水体：负高程可能是陆地，正高程可能是湖泊，海洋高程则表示海床而非水面。
4. 海岸附近分别滤波陆地与海底样本，禁止直接混合平均。
5. 规则面坐标通过等角映射转换为球面方向。Cube Face 共边和角点必须基于相同球面位置得到相同的采样和量化结果，避免运行时出现裂缝。
6. 参考高程量化为有符号 `int16`。量化步长由资产记录的最大高程尺度推导，运行时从资产恢复相对球体半径的高程；海洋视觉几何固定为海平面。海深只映射到游戏地块：`≤ 200 m` 为浅海，`> 200 m` 为深海。

### 2.3 清理河流、湖泊与海峡

**河流**

1. 按 `DIS_AV_CMS`、`UPLAND_SKM` 和河序过滤 HydroRIVERS，只保留对画面或玩法有意义的河流。
2. 保留 `HYRIV_ID`、`NEXT_DOWN` 和入海/内流终点，不从重采样几何反推上下游。
3. 玩法河段吸附到地块边，在地块顶点汇流；视觉河流保留简化后的原始中心线。两者通过源要素键关联。
4. 河宽按流量或河序映射到 `Minor / Regional / Major` 三级。

**湖泊**

1. 使用湖泊面和 pour point 对接河网。湖面高度由 ETOPO 湖区与岸线样本稳健估计，HydroLAKES `Elevation` 只用于校验。
2. 默认保留面积至少约 20 km² 的湖泊；位于保留河网或具有玩法意义的小湖可作为例外。
3. `Lake_type = 2` 的人工水库默认排除；`Lake_type = 3` 保留天然湖形，不保留现代调蓄语义。
4. 湖面与地块求交得到覆盖率。大湖可生成水域格；小湖保留为陆地格上的稀疏水体接触。

**海峡**

1. 只为少量有玩法意义的海峡人工维护中心线、宽度和通航等级。
2. 海峡编译为双向 `WaterPassage`，用于在降采样后仍保持海上移动连通。
3. 海峡与河流共用带状水面构建器，但不共用玩法对象。河流是有向水系，海峡是双向航行连接。

### 2.4 生成资产

地形 LOD 静态资产的生成与读取已经实现。它是地形渲染的唯一参数和参考数据来源：

```text
TerrainLodAsset
- 格式版本、ChunkResolution、MaximumLod 和最大高程尺度
- 六个 Cube Face 的最高精度量化参考高程
- 每个四叉树节点的中心方向、角半径和最小/最大半径
- 每个节点的包围球、地平线裁剪参数和几何误差
- 参考几何完整性校验
```

静态资产不保存每一级现成的渲染 Mesh。运行时读取并验证资产，根据镜头选择活动 `Chunk`，再从参考高程按需构造对应顶点缓冲并进行缓存。

真实自然地理数据接入后，还需要生成以下玩法与水面渲染资产；它们与地形 LOD 资产职责分离：

```text
GameplayGeographyAsset
- 地块地形分类
- 水体、有向河网和航行连接
- 地块/边/顶点到水文对象的索引

WaterRenderAsset
- 海陆掩膜、湖面和带状水面
- 按 Cube Face 和 Chunk 切分的渲染数据
```

原始 NetCDF、Shapefile、geodatabase 和构建缓存不进入发布包。

## 3. 游戏数据结构

```text
WaterBody
- Id
- Kind: Ocean / Lake
- SurfaceElevation
- MemberWaterTileIndexRange

RiverSystem
- Id
- RiverIndexRange

River
- Id
- SystemId
- SegmentIndexRange

RiverSegment
- Id
- RiverId
- SourceVertexId
- TargetVertexId
- EdgeId
- NextSegmentId
- Size: Minor / Regional / Major

WaterPassage
- Id
- EndpointTileA
- EndpointTileB
- PathIndexRange
- WidthClass
- NavigationClass
- NameKey
```

结构约束：

- 一个 `RiverSegment` 占用一条地块边，方向为上游到下游。
- `WaterPassage` 是海上移动图的双向附加连接，不属于 `RiverSystem`。
- `WaterBody` 不保存原始多边形。
- `TileId -> WaterBodyId` 可使用紧凑数组；`EdgeId -> RiverSegmentId`、小湖接触和 `WaterPassage` 使用稀疏索引。
- 源数据的稳定键保存在资产映射表中，运行时使用紧凑数字 ID。

## 4. 视觉表达

| 对象 | 几何 | 高度与掩膜 |
| --- | --- | --- |
| 地形 | 每个活动 Chunk 为 `C × C` 顶点，按需创建并缓存 | 高度采样自静态资产；法线方案通过视觉测试确定 |
| 海洋 | 海平面球面 | 海陆掩膜限制显示；不生成海床几何 |
| 湖泊 | 按湖面多边形三角化的平面 | 湖面内的底层地形钳制到湖面以下；不构造湖床 |
| 河流 | 沿中心线的分段平面带 | 横向水平，纵向按下游单调拟合，略高于地表 |
| 海峡 | 必要时生成海平面带 | 修正窄水道掩膜；岸线本身已能表达时不生成额外几何 |

河流与海峡共用视觉中间格式：

```text
WaterRibbon
- SourceKey
- PathIndexRange
- WidthClass
- HeightMode: TerrainFollowingMonotone / SeaLevel
- MaterialClass: FreshWater / SeaWater
```

`WaterRibbon` 构建器负责折线简化、宽度展开、Cube Face/Chunk 切分和边界顶点规范化。材质细节法线使用可重复纹理，不来自高精度真实地貌。

## 5. 体量与验收

### 体量

| 阶段 | 体量 |
| --- | ---: |
| 已确定源数据 | 约 1.8 GiB |
| ETOPO 地形构建工作区 | 约 2～4 GiB，建议至少预留 5 GiB |
| `int16` 最高精度参考高程 | `12 × R²` bytes |
| LOD 节点元数据 | `6 × Q × 单节点记录大小`；当前格式单节点为 48 bytes |
| 独立 1-bit 顶点水体掩膜 | 若按参考高程样本保存，最多约 `6 × R² / 8` bytes |
| 128 万地块的浅海/深海分类 | 约 0.3～1.2 MiB |
| 水文对象和索引 | 暂按 20～100 MiB 预留 |
| 地形法线 | 视觉测试后确定 |

### 验收与待测参数

- 六个 Cube Face 共边无高程、法线和水体掩膜裂缝。
- 资产中的细分参数、派生分辨率、节点数量和数据段长度相互一致；损坏或版本不兼容的资产必须在加载时被拒绝。
- 相机移动时，视锥、地平线和屏幕空间误差选择能够稳定工作，分裂与合并阈值不会造成明显抖动。
- 河网无断流、逆流或错误汇流；湖泊出水口与河网一致。
- 重要海峡在地块降采样后仍保持通航连通。
- 在目标最低相机高度和显示分辨率下比较候选法线方案。只有轮廓仍不足时才调整地形细分配方。
- 通过实际数据分布确定河流过滤阈值、湖泊最小面积、湖泊水域格覆盖阈值和河宽等级。

任一输入数据校验和、投影算法、重采样规则、量化规则、水体分类规则或派生元数据语义变化，都必须重新生成资产；格式或字段语义变化还必须提升对应资产版本。
