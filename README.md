# 云汀素材管理工具

InternalAssetLibrary 是面向小型视频制作团队的本地素材索引与云端共享工具。

当前版本：**1.2.0，首个公开测试版**。当前交付目标为 Windows x64；macOS 尚非本轮交付目标。

## 功能与源码范围

- 本地素材索引、文件夹筛选、标签和标记管理。
- 团队素材共享、权限管理、上传/下载任务及暂停、继续、取消。
- 音视频与图片预览、临时编辑器及兼容性转码。
- ASP.NET 素材库服务端和浏览器管理后台，SQLite数据迁移与备份，腾讯COS直传。
- 更新器、安装构建脚本及正式自动测试。

仓库不包含团队官网、官网工作台、官网内容、生产配置、用户素材或临时测试产物。浏览器素材库管理后台位于服务端 `/admin`。

## 构建与测试

需要 `global.json` 指定的 .NET SDK及可用的NuGet源。客户端使用Avalonia，正式安装包需要Inno Setup 6；第三方媒体运行库来源和校验值见 [媒体锁文件](packaging/windows/media-runtime.win-x64.lock.json)。

```powershell
dotnet restore InternalAssetLibrary.slnx --locked-mode
dotnet build InternalAssetLibrary.slnx -c Release --no-restore
pwsh -File scripts/test.ps1
```

开发环境启动服务端：

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DevelopmentBootstrap__Enabled = 'true'
$env:DevelopmentBootstrap__TemporaryPassword = Read-Host '输入仅用于本机开发的临时管理员密码'
dotnet run --project src/InternalAssetLibrary.Server --urls http://127.0.0.1:5019
```

服务端开发配置使用本机文件存储，不需要COS永久密钥。登录账号为 `admin`；不要把开发服务暴露到公网。生产部署见 [通用部署教程](docs/DEPLOYMENT.md)。

## 安装与发布

仅发布Windows x64安装包，不再提供便携包。安装包由维护者手动上传GitHub Releases，并标记Pre-release；仓库源码中不存储安装包或大型运行库二进制。

安装前退出旧软件，沿用原目录覆盖安装，无需先卸载，不删除用户素材。版本变化见 [CHANGELOG.md](CHANGELOG.md)。测试版仍需实际环境验收，不能将自动测试视为所有平台和网络场景已通过。

## 分域授权

- 自研代码、项目文件、构建脚本与测试：**GPL-3.0-only**，附合理的原作者“风尘WD（FengchenWD）”署名保留要求，见 [署名要求](LICENSES/AUTHOR_ATTRIBUTION.md)。允许商用、修改及收费分发，分发时履行适用GPL义务、提供对应源码；使用工具制作的视频无需给软件作者署名。
- 有权授权的文档、Logo、头像及其他美术：**CC BY-NC-SA 4.0**。代码商用许可不覆盖这些非商业资源；商业再分发需替换相关资源或另获授权。
- 第三方依赖、字体及媒体组件：保留原许可，见 [第三方声明](THIRD_PARTY_NOTICES.md)。

这是分域授权，不是二选一双重许可。许可全文及边界见 [LICENSES](LICENSES/README.md)。软件不提供适销性或特定用途保证，详见适用许可。
