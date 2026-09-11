## 运行

开发环境使用 .NET SDK 10.0.401（允许同一功能版本内的补丁更新），目标框架为 .NET 9 Windows。

项目使用运行时生成的无高程单位球面，默认进入地形模式，同时使用 `Content/surface.bin` 的陆地、海洋和湖泊归属与 `Content/terrain.bin` 的每格高程，在运行时分类并使用固定颜色。两种资产保留原始归属和高程，不与地图模式一一对应；随源码和发布包提供，普通运行无需安装 GMT。

数字键 `0` 切换原始编号调试模式，`1` 切换地形模式。

当前陆地暂按中心高程分档：低于 200 m 为平原（绿色），200 m 至不足 500 m 为丘陵（黄褐色），500 m 及以上为山地（棕色）。海洋高程不低于 −200 m 为浅水（浅蓝色），否则为深水（深蓝色）。湖泊统一显示为青蓝色，不以湖面海拔推断深度。归属始终优先，负高程陆地仍是陆地，高海拔湖泊仍是湖泊；后续再完善陆地起伏分类。

```powershell
dotnet run
```

## 生成海陆归属资产

使用已安装的 GMT，在项目根目录下载并裁取全球 GSHHG 1 角分节点配准掩膜：

```powershell
New-Item -ItemType Directory -Force artifacts/surface | Out-Null
gmt grdcut "@earth_mask_01m_g" -R-180/180/-90/90 "-Gartifacts/surface/earth-mask-01m.nc" -V
gmt grdinfo artifacts/surface/earth-mask-01m.nc
Get-FileHash artifacts/surface/earth-mask-01m.nc -Algorithm SHA256
```

应得到经度 ±180°、纬度 ±90°、21601 × 10801 节点的全球栅格，源类别为 0～4。产品来自 GSHHG 完整分辨率数据，省略约 3 km² 以下的地物；数据说明见 [GMT Earth Mask](https://www.generic-mapping-tools.org/remote-datasets/earth-mask.html)。

使用本地文件生成资产：

```powershell
dotnet run -c Debug -- --build-surface artifacts/surface/earth-mask-01m.nc
```

Writer 每次重新导出固定地块中心，用 GMT 最近邻采样。源值 0 映射为海洋，1、3 映射为陆地，2、4 映射为湖泊；非法分类直接拒绝，不做插值或舍入。全部 TileId 校验成功后，以三类编码（Ocean=0、Land=1、Lake=2）写入 `Content/surface.bin`。格式为小端 `SURF` 标识、UInt32 单一资产版本 1、按 TileId 排列的 Byte 分类，当前共 1,281,650 字节。版本同时约束格式、分类编码、地块编号和坐标约定。

生成命令不启动游戏，失败保留原资产；生成后再次构建或运行，将资产复制到输出目录。Writer、共享 GMT 采样代码及命令入口仅存在于 Debug，Release 直接携带已生成资产。

`artifacts/surface/tiles.tsv` 保存经度、纬度、TileId，`kinds.tsv` 追加原始 GSHHG 类别。这些中间文件与源栅格不纳入版本控制，正式 `Content/surface.bin` 随代码提交。归属以中心样本决定，不保证保留小岛、小湖或狭窄海峡，也不提供独立湖泊 ID 与水文关系。

当前输入由 GMT 6.7.0 使用上述 `grdcut` 命令生成，`earth-mask-01m.nc` 的 SHA-256 为：

```text
051A97A17D868C8C55002861DBFC8526D3B842A4D7B55C43D1BC0476D17FCACD
```

更新数据时保留源文件，并记录 GMT 版本与校验和，以便重现。

## 生成地形资产

开发时安装 GMT 并加入 PATH，在项目根目录准备全球高程源文件：

```powershell
New-Item -ItemType Directory -Force artifacts/terrain | Out-Null
gmt grdconvert "@earth_relief_06m_g" -Gartifacts/terrain/earth-relief-06m.nc -V
```

使用本地源文件生成资产：

```powershell
dotnet run -c Debug -- --build-terrain artifacts/terrain/earth-relief-06m.nc
```

命令按 tile 中心进行最近邻采样，校验全部编号和高度后，以整米 Int16 写入 `Content/terrain.bin`。成功后替换原资产；生成命令不启动游戏。生成后再次构建或运行，资产会复制到输出目录。

`artifacts/terrain/tiles.tsv` 和 `heights.tsv` 分别保存经度、纬度、TileId，以及追加的采样高度，供检查使用；源文件和中间文件不纳入版本控制。生成的 `Content/terrain.bin` 应随代码提交。

仅 Debug 编译包含生成命令和 Writer，Release 直接使用已生成的资产。更新源数据时记录来源、GMT 版本及 `Get-FileHash artifacts/terrain/earth-relief-06m.nc -Algorithm SHA256` 的结果，保留对应源文件以便重现。

当前资产使用 GMT 6.7.0 的 `earth_relief_06m_g`，源文件元数据标注为 SRTM15_V2.7 经 31.5 km 高斯滤波生成（Tozer et al., 2019，<https://doi.org/10.1029/2019EA000658>）。本地 `earth-relief-06m.nc` 的 SHA-256 为：

```text
08FEB427A4BB72DD56848E5A7B5B3FC4C914DBB3201AFC9227AD1A9C77B93F52
```

## 下载预览

[下载 Windows x64 预览包](https://github.com/luorijun/v3like/releases/download/preview/v3like-win-x64.zip) · [构建记录](https://github.com/luorijun/v3like/actions/workflows/preview.yml)

解压整个 ZIP 后运行 `monogame.exe`，不要单独移动 exe。包内包含 .NET 运行时，无需安装 SDK 或 .NET。需要支持 DirectX 11 的 Windows x64 环境；`Esc` 退出，`F5` 切换调试面板。`version.txt` 记录对应提交和构建时间。

推送到 `main` 后，GitHub Actions 自动构建并更新 `preview` 预发布版本；首次成功发布后下载链接生效。也可以在 Actions 的 Windows preview 页面选择 `main`，手动执行 Run workflow。构建失败不会替换已有预览。近期构建以提交 SHA 命名保存在 Actions Artifacts 中，保留 14 天，下载需要登录 GitHub。

工作流使用内置 `GITHUB_TOKEN`，不需要配置个人令牌。仓库须允许 Actions 运行及 `contents: write` 权限，并允许更新 `preview` 标签；滚动预览不支持不可变 Release。发布步骤失败时仍可从本次运行的 Artifact 下载构建包。

本地生成相同的发布目录：

```powershell
dotnet tool restore
dotnet publish monogame.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
```
