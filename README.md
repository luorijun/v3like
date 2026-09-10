## 运行

开发环境使用 .NET SDK 10.0.401（允许同一功能版本内的补丁更新），目标框架为 .NET 9 Windows。

项目使用运行时生成的无高程单位球面，不需要预先生成地形资产。

```powershell
dotnet run
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
