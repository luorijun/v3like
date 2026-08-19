# 地形四叉树 LOD 实施方案

本文定义 Cube-sphere 地形 LOD 所需的静态数据、离线构造流程和运行时叶节点选择算法。

## 静态数据

### 配置与约束

资产配置：

```text
ReferenceRadius > 0
ChunkResolution >= 2
MaximumLod >= 0
```

运行时配置：

```text
0 <= MinimumLod <= MaximumLod
SplitThreshold > 0
```

- 地球采用 Cube-sphere 四叉树和真实高度比例。
- `ChunkResolution` 是每个 Chunk 单边的顶点数，每个 Chunk 包含 `(ChunkResolution - 1)²` 个网格单元。
- 所有 Chunk 使用相同的网格分辨率、三角形拓扑和对角线方向。
- 各级顶点网格相互嵌套；低 LOD 已有顶点直接使用对应的参考高程。

最高 LOD 在每个 Cube Face 边上的区间数为：

```text
(ChunkResolution - 1) × 2^MaximumLod
```

### 节点数据

```text
Level
X
Y
CenterDirection
AngularRadius
MinimumRadius
MaximumRadius
BoundingSphere
OccluderRadius（资产全局）
HorizonPointRadius
GeometricError
```

- `Level`、`X` 和 `Y` 标识节点在 Cube Face 四叉树中的位置。
- 所有空间范围必须保守包含节点自身及全部后代 LOD 三角面，并自底向上聚合。
- `CenterDirection` 和 `AngularRadius` 定义球面方向范围。
- `MinimumRadius` 和 `MaximumRadius` 定义径向范围；最小值必须包含三角形内部。
- `BoundingSphere` 用于视锥剔除，不要求是最小球。
- `OccluderRadius` 是完全位于所有候选 LOD 三角面内部的全局遮挡球半径。
- `HorizonPointRadius` 是虚拟地平线遮挡点到球心的距离；零表示禁用。
- `GeometricError` 用于 SSE 计算。

## 离线数据构造

### 参考高程

资产构建器通过以下 interface 获取参考高程：

```text
IReferenceElevationSource.GetElevation(Vector3 unitDirection) -> float
```

- `unitDirection` 是以地心为原点的单位方向。
- 返回值是相对 `ReferenceRadius` 的有限高程，二者使用相同长度单位。
- 相同方向在一次资产构建中必须返回相同结果。
- Cube Face 共边顶点使用规范化共享键，只采样一次并复用结果。
- 参考高程先按发布格式量化，再解码为 `float` 构造参考位置和全部 LOD 元数据。
- LOD 模块不感知上游源数据精度；离线几何计算可以将已经确定的 `float` 位置提升为 `double`。

节点局部顶点 `(localX, localY)` 对应的 Cube Face 最高 LOD 网格坐标为：

```text
scale = 2^(MaximumLod - node.Level)

finestX = (node.X × (ChunkResolution - 1) + localX) × scale
finestY = (node.Y × (ChunkResolution - 1) + localY) × scale
```

### LOD 误差

对每个非最高 LOD 节点，遍历其覆盖的全部最高 LOD 顶点。在相同 Cube Face 参数位置找到当前 LOD 的固定拓扑三角形，并对三角形三维位置做重心插值：

```text
DirectError(node) = max over finest vertices:
    length(ReferencePosition - CurrentTrianglePosition)

GeometricError(node) = max(
    DirectError(node),
    GeometricError(node.Children)
)
```

- 比较完整三维欧氏距离，以同时包含高程、Cube-sphere 投影和球面弦面误差。
- 固定、嵌套的参数网格和三角形拓扑保证最高 LOD 顶点足以界定分片线性参考面的最大误差。
- 父节点误差不得小于任一子节点；最高 LOD 的 `GeometricError` 为零。

### 地平线遮挡数据

首先从六个根节点已经聚合的 `MinimumRadius` 得到全局遮挡球：

```text
OccluderRadius = min(root.MinimumRadius)
```

该值必须包含所有 LOD 三角面内部，因此不能直接用 `ReferenceRadius` 或只检查顶点。每个节点再构建一个保守虚拟遮挡点，设：

```text
r = OccluderRadius

heightAngle = acos(clamp(r / node.MaximumRadius, 0, 1))

horizonAngle = node.AngularRadius + heightAngle
```

当 `horizonAngle < π / 2` 时：

```text
HorizonPointRadius = r / cos(horizonAngle)
```

否则保存 `HorizonPointRadius = 0`。

### 数据编码

`GeometricError`、包围外边界和 `HorizonPointRadius` 写入 `float` 时向外编码，包围内边界和 `OccluderRadius` 向内编码。中心量化后必须重新扩大对应范围；量化 `CenterDirection` 后必须重新计算地平线遮挡数据。

首版资产使用带版本的单一二进制容器和分区表：

```text
Header
ReferenceHeights  六个 Face 按固定顺序保存的 int16 高程
LodNodeMetadata   Face、Level、Morton 顺序的固定 48 bytes 节点记录
Checksums         最终参考几何 SHA-256
```

`ReferenceHeights` 的量化步长保存在 Header；运行时解码结果为 `float`。节点标识由分区顺序推导，不在每条记录中重复保存。

资产读取 adapter 位于 `Planet/Load`，interface 为 `TerrainLodAssetReader.Read(Stream) -> TerrainLodAsset`。输入流必须可读、可定位；读取器验证版本、分区范围、配置派生值、节点层级不变量和参考几何 SHA-256，格式损坏时抛出 `InvalidDataException`。

构建配置、三角形拓扑、Cube-sphere 投影或参考高程发生变化时，必须重建 LOD 资产。

## 运行时叶节点选择

选择模块的 interface 为：

```text
SelectionResult Select(View)

SelectionResult:
    SelectedLeafNodeIds
```

运行时输入：

```text
CameraPosition
VerticalFieldOfView
ViewportHeight
FrustumPlanes
```

投影焦距为：

```text
FocalLength =
    ViewportHeight × 0.5 / tan(VerticalFieldOfView × 0.5)
```

每个节点依次执行地平线剔除、视锥剔除、距离计算和 SSE 判断。

### 地平线剔除

设：

```text
r = OccluderRadius
A = CameraPosition
C = node.CenterDirection × node.HorizonPointRadius
V = C - A
vv = dot(V, V)
projection = -dot(A, V)
cameraHorizonSquared = dot(A, A) - r²
```

当相机位于遮挡球外、节点具有有效遮挡点且 `vv > 0` 时：

```text
0 < projection < vv
and
projection² > cameraHorizonSquared × vv
    => Cull
```

相切时保留节点；相机位于遮挡球内时禁用地平线剔除。

### 视锥剔除

使用 `BoundingSphere` 测试视锥的六个平面：

```text
完全位于任意视锥平面外 => Cull
相交或位于视锥内       => 继续
```

### 节点距离

使用节点球面方向范围和径向范围计算相机到节点全部候选几何的距离下界。设：

```text
D = length(CameraPosition)
CameraDirection = normalize(CameraPosition)
theta = angle(CameraDirection, node.CenterDirection)
alpha = node.AngularRadius

beta = max(0, theta - alpha)

radius = clamp(
    D × cos(beta),
    node.MinimumRadius,
    node.MaximumRadius
)

DistanceToChunk = sqrt(
    D² + radius² - 2 × D × radius × cos(beta)
)
```

距离接近零时使用一个仅用于数值稳定的正数下限。

### 误差判断

```text
ScreenSpaceError =
    node.GeometricError × FocalLength / DistanceToChunk
```

```text
SSE > SplitThreshold  => 细分
SSE <= SplitThreshold => 选择当前节点
```

完整流程：

```text
Select(node, view):
    if FullyBehindHorizon(node, view):
        return

    if FullyOutsideFrustum(node, view):
        return

    if node.Level == MaximumLod:
        select node
        return

    if node.Level < MinimumLod:
        Select(node.Children, view)
        return

    distance = DistanceToChunk(node, view)
    sse = node.GeometricError * view.FocalLength / distance

    if sse <= SplitThreshold:
        select node
        return

    Select(node.Children, view)
```

四个子节点独立执行相同流程。

## 验收条件

- 最高 LOD 节点的 `GeometricError` 为零。
- 父节点的 `GeometricError` 不小于任一子节点。
- 节点空间范围包含节点自身及其全部后代 LOD 几何。
- `OccluderRadius` 不大于任意候选 LOD 三角面到球心的距离。
- 视锥测试不得剔除任何可见几何。
- `DistanceToChunk` 不得大于相机到节点候选几何的真实最近距离。
- 所有选中叶节点满足 `Level >= MinimumLod`。
- 所有未达到最高 LOD 的选中叶节点满足 `SSE <= SplitThreshold`。
- 相同参考高程和视图产生相同的叶节点集合。
- Cube Face 共边顶点使用相同参考高程。
- 调整运行时配置不需要重建 LOD 资产。
