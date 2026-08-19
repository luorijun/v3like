# 离线数据规划与地形获取方案

本文记录当前世界地图所需离线数据的范围、体量估算，以及首版地形数据的获取和构建方案。估算基于当前设计：全球球面、约 128 万个游戏地块，以及 Cube-sphere 地形 `ChunkResolution = 17`、`MaximumLod = 7`。除地形外，具体上游数据集尚未全部选定，因此对应数字是容量规划范围，不是最终承诺值。

## 当前结论

- 正式地形源使用 **NOAA ETOPO 2022 60″、Ice Surface、NetCDF**。
- 海洋不表现海底几何，水面固定为海平面；海深只用于以 **200 m** 为阈值区分浅海和深海。河流使用平面水面。
- 河网使用 HydroRIVERS，湖泊使用 HydroLAKES，海陆基底使用 Natural Earth；少量有玩法意义的海峡人工保存为 `WaterPassage`，视觉上与河流共用带状水面构建管线。
- `MaximumLod = 7` 暂不调整。最低相机高度约 15 km，先用平滑顶点法线、较高密度的地形法线和可复用细节法线验证偏卡通效果，再决定是否增加几何 LOD。
- 运行时只发布重采样后的 Cube-sphere 高程、海陆/水体分类和 LOD 元数据；原始 NetCDF、构建缓存和质量检查数据不进入游戏发布包。
- 海陆不能仅按高程正负判断。低于海平面的陆地仍是陆地；水面几何也不能直接使用负的海底高程。海岸线和内陆水体必须由独立水体掩膜或后续水文数据确定。

## 项目内最高地形网格

最高 LOD 在每个 Cube Face 边上的区间数为：

```text
(ChunkResolution - 1) × 2^MaximumLod
= 16 × 128
= 2048
```

因此每面边长顶点数 `n = 2049`。六面共边和角点去重后的独立顶点数为：

```text
6 × n² - 12 × n + 8
= 6 × 2049² - 12 × 2049 + 8
= 25,165,826
```

全球表面积约 5.10 亿 km²，最高层共有 `6 × 2048² = 25,165,824` 个面元，平均每个面元约 20.3 km²，等效边长约 **4.5 km**。

仅保存一个高程值时：

| 存储类型 | 精确字节数 | 约合 |
| --- | ---: | ---: |
| `int16` 米 | 50,331,652 bytes | 48 MiB |
| `float32` | 100,663,304 bytes | 96 MiB |

地球最高点和最深海沟都在 `int16` 米的范围内。当前几何尺度不需要亚米精度，因此发布资产默认使用 `int16`；离线滤波、重采样和误差计算使用 `double`。

LOD 0～7 的节点总数为：

```text
6 × (1 + 4 + ... + 4^7) = 131,070
```

按每节点 44～64 bytes 保存中心方向、角半径、最小/最大半径、包围球、几何误差和必要索引，LOD 元数据约 **5.5～8 MiB**。因此地形核心资产约为 53.5～56 MiB；若另存一份逐顶点 1-bit 水体掩膜，再增加约 3 MiB，总计约 **56.5～59 MiB**，尚未计算通用文件头和块索引。

## 最低高度下的视觉表达

最高 LOD Chunk 含 `17 × 17` 个顶点和 `16 × 16` 个网格单元，平均宽度约 **72 km**。当前相机垂直视场角为 45°；在 15 km 高度俯视时，海平面附近画面纵向覆盖约 12.4 km，最高山峰附近约 4.5～5.1 km，即画面内约有 1～3 个最高 LOD 几何单元。

当前几何密度不足以表达写实微地貌，但配合平滑顶点法线可以保持连续曲面。首轮采用以下组合：

```text
17 × 17 几何顶点
+ 平滑顶点法线
+ 约 64 × 64 / 最高 LOD Chunk 的地形法线
+ 可复用的材质细节法线
```

这套组合预计足以支持介于低多边形与写实之间的偏卡通风格。地形法线只负责坡向、山脊和沟谷的中尺度光照，细节法线负责岩石、土壤和积雪；法线不能改变山峰剪影、遮挡和碰撞。实际测试先比较无地形法线、`64 × 64` 和 `128 × 128` 三档，只有轮廓仍明显不足时才提高几何 LOD。

## 地形源决策

### 正式主源：ETOPO 2022 60″ Ice Surface

[NOAA ETOPO 产品页](https://www.ncei.noaa.gov/products/etopo-global-relief-model)说明 ETOPO 2022 是融合陆地地形、海底地形和岸线数据的全球地形模型，并提供 Ice Surface 和 Bedrock 两种版本。项目表现现实世界表面，应选择 Ice Surface，而不是格陵兰和南极冰盖下的 Bedrock。

[ETOPO 2022 官方元数据](https://www.ncei.noaa.gov/access/metadata/landing-page/bin/iso?id=gov.noaa.ngdc.mgg.dem%3Aetopo_2022)给出全球覆盖、EPSG:4326 水平坐标和 EPSG:3855（EGM2008）垂直基准。数据由 NOAA 以 [CC0-1.0](https://creativecommons.org/publicdomain/zero/1.0/) 方式贡献到公有领域；应按 NOAA 给出的 DOI `10.25921/fd45-gt74` 标注来源，且不得用于导航。

[ETOPO 2022 User Guide](https://www.ngdc.noaa.gov/mgg/global/relief/ETOPO2022/docs/1.2%20ETOPO%202022%20User%20Guide.pdf)说明 30″ 和 60″ 是由 15″ 高程瓦片降采样得到的全球单文件，均提供 GeoTIFF 和 NetCDF；ETOPO 高程以 EGM2008 为基准。本项目的地形、水面和海深使用同一正高体系，无需转换为椭球高。

下载对象：

```text
ETOPO_2022_v1_60s_N90W180_surface.nc
```

- 网格：`21600 × 10800`，共 233,280,000 个 `float32` 高程值。
- 未考虑 NetCDF 压缩时，高程数组本身约 890 MiB。
- [NOAA THREDDS 文件页](https://www.ngdc.noaa.gov/thredds/catalog/global/ETOPO2022/60s/60s_surface_elev_netcdf/catalog.html?dataset=globalDatasetScan%2FETOPO2022%2F60s%2F60s_surface_elev_netcdf%2FETOPO_2022_v1_60s_N90W180_surface.nc)公布的文件大小为 `478,290,125 bytes`，约 **456 MiB**。
- [HTTP 直接下载](https://www.ngdc.noaa.gov/thredds/fileServer/global/ETOPO2022/60s/60s_surface_elev_netcdf/ETOPO_2022_v1_60s_N90W180_surface.nc)不要求账户或 API 凭据。

60″ 在赤道约为 1.85 km，相对项目平均 4.5 km 最高面元有约 2.4 倍线性过采样，足以生成宏观几何和中尺度法线。更细的法线细节采用风格化材质法线，不要求来自真实高程。只有后续明确要求亚公里级真实地貌法线时，才重新评估 GEBCO 15″。

## 河流、湖泊与海峡

### 映射原则

水文数据必须一次离线编译为两套结果：

```text
源水文要素
    ├─ GameplayHydrologyAsset：地块、地块边、地块顶点和航行连接
    └─ WaterRenderAsset：海面、湖面和带状水面
```

- 玩法资产映射到约 20 km 尺度的二十面体对偶地块拓扑。
- 视觉资产映射到最高约 4.5 km 面元的 Cube-sphere 地形，并按 Cube Face 和 Chunk 切分。
- 两套资产保留相同的源要素键，但不共用几何。玩法河段可以沿地块边折转，视觉河流仍沿简化后的原始中心线绘制。
- 离线模块只向调用方暴露一个构建接口：输入地块拓扑、高程采样器、海陆掩膜、河流、湖泊和人工海峡定义，返回上述两套资产。坐标转换、拓扑吸附、高度拟合和 Chunk 切分都隐藏在该模块内。

### 高程与海陆掩膜

ETOPO 高程只表示表面或海床相对垂直基准的高度，不表示该位置是否被水覆盖：

- 低于海平面的盆地和圩田仍是陆地。
- 里海、死海的水面高程为负，高原湖泊和河流的高程可以为正。
- 海洋负高程表示海床，而渲染所需的海面固定在海平面。
- 海岸附近的源像元和降采样足迹可以同时包含陆地与海底，平均后的正负号不再是稳定分类。

因此 `elevation < 0` 只能作为近似，不能作为海陆定义。Natural Earth 海陆面和 HydroLAKES 湖泊面在离线阶段生成独立水体分类；发布时可直接编译进地块地形类型、水体 ID 和渲染掩膜，不需要携带原始海陆文件。渲染海平面时也必须使用该掩膜，否则海面会覆盖低于海平面的陆地。

### 运行时玩法资产

水体对象表示海洋或湖泊的身份，不保存原始多边形：

```text
WaterBody
- Id
- Kind: Ocean / Lake
- SurfaceElevation
- MemberWaterTileIndexRange
```

河网保留有向拓扑。一个玩法河段占用一条地块边，起点和终点是地块顶点：

```text
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
```

海峡不属于河网。它是海上移动图中的显式附加连接，可以穿过一个以陆地为主的混合地块：

```text
WaterPassage
- Id
- EndpointTileA
- EndpointTileB
- PathIndexRange
- WidthClass
- NavigationClass
- NameKey
```

需要的派生查询索引使用紧凑数组或稀疏键值对：

- 水域地块 `TileId -> WaterBodyId`。
- 河段 `EdgeId -> RiverSegmentId`。
- 小湖和河岸对邻近陆地格的接触索引。
- `WaterPassage` 对海上移动图增加的邻接关系。

湖泊多边形与地块求交后先计算覆盖率。覆盖足够大且需要支持水上移动的部分才成为水域格；小湖所在格仍可保持陆地，通过接触索引参与丰容度等机制。具体覆盖率阈值留待实际地块分布测试。

### 视觉资产

- **海洋**：使用海平面球面，由海陆掩膜限制显示范围；海床不生成几何。
- **湖泊**：按湖泊多边形生成固定高度平面；湖面内的底层地形只做高度上限钳制，防止穿出，不构造湖床。
- **河流**：沿视觉中心线生成分段平面带。横向保持水平，纵向高度根据地形样本做下游单调拟合，并略高于地表以避免深度冲突。
- **海峡**：岸线掩膜已能表达时不生成额外几何；若在 4.5 km 地形尺度上消失，则按人工中心线生成海平面带并修正掩膜。

河流和海峡共用一种视觉中间格式：

```text
WaterRibbon
- SourceKey
- PathIndexRange
- WidthClass
- HeightMode: TerrainFollowingMonotone / SeaLevel
- MaterialClass: FreshWater / SeaWater
```

共用范围只包括折线简化、宽度展开、Cube Face/Chunk 切分、边界顶点规范化和水面材质生成。河流仍编译为有向 `RiverSegment`，海峡仍编译为双向 `WaterPassage`，避免运行时用大量可空字段和分支区分两种语义。

### 数据源决策

| 用途 | 决策 | 覆盖、尺度与获取 | 许可 |
| --- | --- | --- | --- |
| 河网及上下游 | **HydroRIVERS v1 主源** | [官方页](https://www.hydrosheds.org/products/hydrorivers)提供全球 Shapefile 544 MB；由 15″ HydroSHEDS 提取，含约 850 万条平均 4.2 km 的河段、`NEXT_DOWN`、河序、上游面积和平均流量。60°N 以北的底层高程较粗，需重点抽查。 | 可科学、教育和商业使用；保留要求的署名，不原样再分发源包。 |
| 湖泊面及河湖连接 | **HydroLAKES v1 主源** | [官方页](https://www.hydrosheds.org/products/hydrolakes)提供全球 geodatabase 763 MB；约 142 万个面积至少 10 ha 的湖泊/水库，含湖面、出水口、河段关联、面积、类型和参考高程。 | CC BY 4.0；不原样再分发完整源包。 |
| 海陆与主要地名 | **Natural Earth 1:10m 辅助源** | [物理矢量下载](https://www.naturalearthdata.com/downloads/10m-physical-vectors/)中 Land 约 3.12 MB、Ocean 约 3.04 MB、河流中心线约 1.98 MB、湖泊约 2.24 MB、海域名称约 0.92 MB；适合本项目尺度的海陆掩膜、主要名称和人工校验，不替代水文拓扑。 | [公共领域](https://www.naturalearthdata.com/about/terms-of-use/)，可修改和商用。 |
| 海峡名称校验 | **Marine Regions / SeaVoX 可选** | [Marine Gazetteer](https://www.marineregions.org/gazetteer.php?p=webservices)可按 `Strait` 类型查询；只用于校验少量人工 `WaterPassage` 的名称和位置，不作为首版必需下载。 | [CC BY](https://www.marineregions.org/disclaimer.php)，不得用于导航。 |

不把 OSM 作为首版依赖。[官方 Planet PBF](https://planet.openstreetmap.org/)是约 88 GB 的完整数据库，水体标注的一致性也不足以直接生成全球水文拓扑；[ODbL](https://www.openstreetmap.org/copyright)还要求署名，并对公开分发的派生数据库施加同许可要求。Geofabrik 仅适合以后按区域补漏。

当前没有必要为少量重要海峡引入完整数据源。首版直接人工维护 `WaterPassage` 中心线、宽度和通航等级；Natural Earth 或 Marine Regions 只用于名称和位置校验。

### 河流构造

1. 按 `DIS_AV_CMS`、`UPLAND_SKM` 和河序过滤 HydroRIVERS；首版不发布全部 850 万河段，只保留影响画面、移动或聚落的主干及重要支流。
2. 保留 `HYRIV_ID`、`NEXT_DOWN` 和入海/内流终点形成有向水系图。玩法河段吸附到地块边，在地块顶点汇流；原始中心线只用于视觉层，两者通过源河段 ID 关联，不从栅格化结果反推上下游。
3. 河宽不来自真实岸线，而按流量或河序分为少量风格化宽度等级。视觉中心线贴附地形，沿下游对 ETOPO 采样高度做单调平滑；渲染为横向平、纵向缓降且略高于地表的带状水面，不切割地形网格。
4. 多数河流远窄于 4.5 km 地形面元，必须作为独立覆盖层渲染；连续性由水系图保证，不要求几何网格自身表达河谷。

### 湖泊构造

1. HydroLAKES 多边形负责湖泊身份和边界，pour point 负责与 HydroRIVERS 对接。湖面使用单一高度；为统一到 EGM2008，正式高度从湖区及岸线的 ETOPO 样本稳健估计，HydroLAKES `Elevation` 只用于校验。
2. 默认发布面积至少约 **20 km²**（接近一个最高 LOD 面元）的湖泊；名称明确、位于保留河网或具有玩法意义的小湖作为例外。多边形在切分 Cube Face 后三角化为平面水面；湖面范围内的底层地形只做高度上限钳制，防止穿出水面，不构造湖床。
3. `Lake_type = 2` 的明确水库默认排除；`Lake_type = 3` 保留天然湖形但去除调蓄语义。官方文档说明部分未识别的小型人工水体仍被记为普通湖，因此重要区域仍需人工例外表。

### 海峡与海洋连通

1. 将 Natural Earth 的海陆面在高于逻辑地块的临时分辨率栅格化，再降采样得到陆地、海洋和海岸；在高分辨率掩膜上计算海水连通分量，避免仅按地块中心判断导致狭窄水道消失。
2. 人工配置每个重要海峡的中心线、宽度和通航等级，并由中心线端点定位两侧的海上移动节点。
3. 小于 4.5 km、或降采样后闭合的海峡仍由 `WaterPassage` 穿过混合/陆地格保持逻辑连通，无需改写地块的主地形分类。
4. 视觉上需要补齐窄水道时，将同一份人工中心线以 `SeaLevel` 模式交给河流共用的 `WaterRibbon` 构建管线。

### 可推导与人工维护边界

- 可推导：海陆/湖泊掩膜、河段经过的地块与边、上下游图、湖泊出水河段、海水连通分量和河宽等级。
- 必须人工规则：河流与湖泊的保留阈值及例外、人工水库处理、重要水体名称绑定、少量 `WaterPassage` 的路径与通航等级。运河属于人文系统，不进入默认自然世界资产。

## 首版获取与构建流程

1. 下载 ETOPO 2022、HydroRIVERS、HydroLAKES 和 Natural Earth 1:10m 物理矢量；Marine Regions 只在校验海峡名称时按需查询。
2. 在源数据清单中记录下载 URL、SHA-256、水平和垂直基准、许可与引用文本；不要求追踪最新版本。
3. 分块读取 NetCDF。源值先以 `double` 进入滤波，不创建一份无必要的全量文本或中间 GeoTIFF。
4. 为每个 Cube-sphere 最高 LOD 顶点计算其球面采样足迹，对足迹内源像元做确定性的面积加权低通滤波。
5. 海岸附近分别统计陆地和水下样本，禁止直接把正负高程混合平均。海陆身份来自 Natural Earth，湖泊身份来自 HydroLAKES，不能只用 `elevation < 0`。
6. Cube Face 共边和八个角点使用统一的球面位置标识，只计算并量化一次，然后写入各面的引用位置，确保位级一致。
7. 陆地保存带正负号的 `int16` 米高程；海洋几何固定为海平面。对每个海洋逻辑地块统计水深，`≤ 200 m` 为浅海，`> 200 m` 为深海。
8. 按前述规则生成河流带状水面、平面湖面和显式 `WaterPassage`；河流不切割地形，湖面内只做高程上限钳制。
9. 从最终几何生成父层、几何误差、包围元数据和平滑顶点法线；地形法线分辨率先测试 `64 × 64 / Chunk`。
10. 检查六面接缝、典型山地以及 15 km 最低高度下的视觉效果；原始源数据不随游戏发布。

## 三级容量估算

### 1. 获取的源数据

正式地形下载为 ETOPO 2022 60″ Ice Surface NetCDF，约 **456 MiB**。其他自然地理源尚未选型，按使用全球粗分辨率栅格和公开矢量数据的方向，规划如下：

| 类别 | 源数据下载范围 | 说明 |
| --- | ---: | --- |
| 地形与海深 | 约 456 MiB | 已确定：ETOPO 2022 60″ |
| 地块拓扑 | 接近 0 | 项目自行生成，不依赖外部栅格 |
| 气候 | 2～20 GiB | 月度温度、降水及派生气候分类；正式源待定 |
| 生物群落/地表覆盖 | 接近 0～1 GiB | 首版优先从气候、地形派生自然生物群落，不下载现代全球土地覆盖 |
| 水文与水体 | 约 1.3 GiB | HydroRIVERS 544 MB、HydroLAKES 763 MB，Natural Earth 为小型辅助包 |
| 矿藏与地质辅助 | 0.1～5 GiB | 点、面和低分辨率地质栅格；正式源待定 |
| 游戏模板 | 小于 10 MiB | 人工维护的文本/表格 |

在其余数据源完成选型前，首版全部源数据下载可按 **约 4～30 GiB** 规划；其中已经明确的地形与水文源合计约 **1.8 GiB**。

气候源必须在实现前先解决发行许可。[WorldClim 官方许可](https://worldclim.org/about.html)不允许未经许可的商业使用和再分发，因此只能用于非商业原型或取得单独授权后使用。正式版本优先评估 [CHELSA climatologies](https://www.chelsa-climate.org/datasets/chelsa_climatologies)（CC0）或 [ERA5-Land monthly means](https://cds.climate.copernicus.eu/datasets/reanalysis-era5-land-monthly-means)（CC-BY 4.0）。

不建议获取 ESA WorldCover 一类现代土地覆盖作为首版硬依赖：设计要求默认世界从自然平衡开始，而现代耕地、城市和人工覆盖会把现实人类活动错误固化为自然生物群落。生物群落应优先由气候、高程、坡度和水文离线分类。

### 2. 离线构建工作区

ETOPO 地形构建约需 **2～4 GiB** 工作空间，建议预留 **5 GiB**。

加入尚未选型的气候、生物群落、水文和矿藏预处理后，整个世界构建工作区先按 **20～120 GiB** 规划。

### 3. 最终发布包中的静态世界数据

| 类别 | 发布体量估算 | 依据 |
| --- | ---: | --- |
| 地形高程 | 48 MiB | 25,165,826 个 `int16` |
| 地形 LOD 元数据 | 5.5～8 MiB | 131,070 节点，44～64 bytes/节点 |
| 水体掩膜 | 0～3 MiB | 可复用地块/水文分类；独立逐顶点 1-bit 时约 3 MiB |
| 浅海/深海分类 | 0.3～1.2 MiB | 128 万逻辑地块使用 2 bit 或 1 byte |
| 地形法线 | 待视觉测试 | 先测试 `64 × 64 / 最高 LOD Chunk`；细节法线复用材质纹理 |
| 地块拓扑 | 40～80 MiB | 128 万地块的中心、邻接、稳定 ID 和标志 |
| 气候与生物群落静态属性 | 8～30 MiB | 紧凑模板 ID 与少量固定修正值 |
| 水文与水体对象 | 20～100 MiB | 稀疏河段、河流、水系、湖泊和索引；范围估算 |
| 矿藏 | 10～100 MiB | 稀疏对象，数量和属性尚未定案 |
| 模板和稳定键表 | 小于 5 MiB | 地形、气候、生物群落、资源、作物等模板 |

不含待测试的地形法线时，最终静态世界数据约 **130～330 MiB**，发布包预算为 **150～400 MiB**。地形法线方案确定后再单独计入。

作为快速换算，全球约 128 万地块每增加一个逐地块字段，大约增加：

```text
byte    1.22 MiB
uint16  2.44 MiB
uint32  4.88 MiB
float   4.88 MiB
```

因此普遍属性应继续采用连续紧凑数组，河流、湖泊和矿藏保持稀疏对象。

## 非地形离线数据范围

以下数据同样属于世界离线资产，但当前不阻塞地形获取：

- **地块拓扑**：约 128 万个五/六边形地块的稳定 ID、中心、邻接、边索引和陆水标志。由项目确定性生成。
- **气候**：月度温度、降水等原始气候量，最终归并为固定 `ClimateTemplate` 及月度移动/军事系数、基础丰容度和恢复率。
- **生物群落**：固定 `BiomeTemplate`、自然资源和作物适应系数；源数据只参与离线分类，不在运行时保留高分辨率栅格。
- **水文与水体**：海岸线、海洋/湖泊身份、河网、汇流关系、河段规模，以及地块边到河段的派生索引。它也负责解决低于海平面陆地和内陆湖泊不能靠高程符号区分的问题。
- **矿藏**：矿种、初始储量和天然开采难度的稀疏地块对象；具体真实世界源或程序化生成规则尚未决定。
- **模板与稳定键**：地形、气候、生物群落、自然资源、作物、水系修正和矿种模板。持久化保存稳定字符串键，构建后映射为紧凑数字 ID。

城市、省份、人口、经济、政治边界和玩家产生的土地利用不属于固定离线自然地理数据；它们由游戏过程或特定剧本初始化。

## 版本和复现要求

数据是否最新不影响游戏世界；清单只用于复现构建结果，至少包含：

```text
ChunkResolution
MaximumLod
TriangleTopology
CubeSphereProjection
ElevationDataset + source checksum
HorizontalDatum
VerticalDatum
WaterMaskDataset + source checksum
ElevationResamplingRule
ElevationQuantizationRule
TerrainClassificationRule
Builder version
```

上述任一影响参考几何或静态地块属性的内容变化，都生成新的世界资产版本。运行时阈值如 SSE split/merge threshold 不属于离线数据版本。

## 后续执行顺序

1. 下载并登记 ETOPO、HydroRIVERS、HydroLAKES 和 Natural Earth，完成最小 Cube-sphere 转换器。
2. 构建海陆/湖泊掩膜和筛选后的有向河网；人工定义首批 `WaterPassage`。
3. 构建最高 LOD 参考面、LOD 元数据和平滑顶点法线；海洋几何固定为海平面，同时生成 200 m 阈值的浅海/深海分类。
4. 在 15 km 高度同时测试地形法线、河宽等级和最小湖泊面积；只有轮廓不足时才增加几何 LOD。
5. 再确定其余自然地理数据源，并收紧对应的范围估算。
