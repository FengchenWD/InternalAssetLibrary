# 云汀素材管理工具

这是一个面向个人创作者和小型视频制作团队的素材管理工具，目前包含 **“本地素材”**、**“共享素材”**、**“播放器”** 与 **“临时编辑器”** 等模块，并提供上传/下载任务管理和素材库管理后台。当前版本 `v1.2.0` 为首个公开测试版，主要交付平台为 Windows x64。

> 本项目的设计、代码编写、测试排查和文档整理使用了 AI 辅助，并非所有代码均由作者逐行手写。
> 自动测试和工具判断不能覆盖所有实际使用场景，请在重要素材与服务端数据有备份的前提下使用。

## 本地素材模块介绍

本地素材模块可读取用户选择的文件夹，递归索引其中的音频、视频和图片，按文件夹、素材类型、标签及其他条件搜索与筛选。

工具支持缩略图、音频波形、标记管理、批量重命名、另存为和打开文件位置。外置硬盘暂时离线时，可保留已有索引，重新连接后继续使用；**本地素材管理不要求先上传文件到云端**。

<img width="1442" height="912" alt="image" src="https://github.com/user-attachments/assets/c65cdff7-e253-4d25-98fa-c0fb5ae09812" />


## 共享素材模块介绍

共享素材模块用于团队成员之间的素材共享，支持云端文件夹、标签、标记、LUT、素材上传与下载，并提供权限管理、回收站及素材变更实时刷新。

生产环境使用素材库服务端管理账号、元数据和访问权限，素材文件存储于私有腾讯云 COS。客户端通过短期签名地址直接传输文件，**COS 永久密钥只保留在服务端，不交给客户端或浏览器**。

使用共享功能需要连接已部署的服务端并登录；本仓库同时提供服务端源码，便于自行部署。

<img width="1442" height="1007" alt="image" src="https://github.com/user-attachments/assets/8fb1f5e5-2a31-40d3-a225-305c14013710" />


## 播放器模块介绍

播放器使用 libmpv，支持本地素材和云端素材预览，提供播放列表、多音轨选择、循环与随机播放、字幕、截图、逐帧查看及图片缩放等功能。

音视频可添加临时标记，方便记录剪辑时需要关注的位置。具体格式和编码是否可以正常播放，仍取决于文件本身及随软件提供的媒体组件。

<img width="1442" height="1007" alt="image" src="https://github.com/user-attachments/assets/8f3f8140-c4ed-4b90-92b3-8e5788ba81b3" />


## 临时编辑器模块介绍

临时编辑器面向**单个音视频文件的轻量处理**，提供单条主轨、入点/出点、音量调整、dB 精确输入、分贝表及 FFmpeg 导出。打开媒体后默认暂停，用户可先设置处理范围再播放或导出。

它不是完整的多轨剪辑软件，适合临时截取素材、调整音量后交给后续剪辑流程使用。

<img width="1442" height="1007" alt="image" src="https://github.com/user-attachments/assets/5f3bffd7-9ce4-4459-b348-a4f32362ede6" />


## 传输任务模块介绍

传输任务按**上传、下载两个列表**分别展示，支持对单个任务或当前列表中的任务暂停、继续与取消。

任务记录和断点按服务端与账号隔离，重启后默认暂停，避免自动消耗流量。符合条件的 COS 分片上传和 Range 下载支持断点续传；普通 PUT 上传暂停后继续时会重新上传该文件，**并非所有传输方式都能从断点接着传**。

## 管理后台与其他页面

管理员可通过客户端管理工作区或浏览器 `/admin` 管理团队用户、权限及服务端非敏感设置。服务端使用 ASP.NET Core，提供 SQLite 数据存储、迁移、备份和审计相关功能。

设置页支持中文与 English、浅色/深色及跟随系统主题、自定义颜色和字体，以及软件更新相关设置。客户端默认跟随系统主题，并保留系统托盘等桌面功能。

<img width="1442" height="1007" alt="image" src="https://github.com/user-attachments/assets/04dff1cb-c5fa-4acf-a16e-405ba7ea208c" />

<img width="1442" height="1007" alt="image" src="https://github.com/user-attachments/assets/f08ae2d8-2157-4673-96ce-579b121b6e0b" />

<img width="1442" height="1007" alt="image" src="https://github.com/user-attachments/assets/79802600-4cb8-44c4-9f98-f5dc14850e47" />


## 下载与运行相关

> 当前版本为公开测试版 `v1.2.0`。macOS 尚不是本轮交付目标。
> 请先使用非关键素材验证自己的网络、文件格式和部署环境；自动测试通过不代表所有实际环境均已验收。

[项目主页](https://github.com/FengchenWD/InternalAssetLibrary) || [Windows 版本下载](https://github.com/FengchenWD/InternalAssetLibrary/releases) || [版本更新记录](CHANGELOG.md)

- Windows x64 使用 `InternalAssetLibrary.Client.Setup.exe` 安装，**不再提供便携包**。
- 正式安装包包含客户端及所需媒体运行库，无需为了运行客户端另行安装 .NET SDK。
- 升级前退出旧客户端并暂停传输，沿用原安装目录覆盖安装；不要先删除用户素材或服务端数据库。
- 安装包由维护者手动发布到 Releases，下载内容以该页面实际发布的文件为准。
- 本地管理功能可独立使用；团队共享和账号相关功能需要可访问的素材库服务端。

## 自行部署与源码构建

素材库服务端及管理后台的部署步骤见 [服务端通用部署教程](docs/DEPLOYMENT.md)。教程采用通用示例，部署时需填写自己的环境配置；**不要把生产密码、永久密钥、令牌或数据库提交到仓库**。

源码构建需要 `global.json` 指定的 .NET SDK。安装包构建另需 PowerShell 7、Inno Setup 6 和锁定版本的媒体运行库，见 [媒体运行库准备说明](docs/MEDIA_RUNTIME.md)。

在仓库根目录执行：

```powershell
dotnet restore InternalAssetLibrary.slnx --locked-mode
dotnet build InternalAssetLibrary.slnx -c Release --no-restore
dotnet run --project tests/InternalAssetLibrary.SelfTests -c Release --no-restore
```

构建脚本和正式测试随源码提供；不要将自测程序当作日常使用的客户端运行。

## 使用须知与许可

作者：**风尘WD（FengchenWD）**。使用者应确认自己有权存储、处理和分享相关素材，妥善保护团队账号与数据；重要操作前请保留备份。

本项目按材料类型分域授权，**不提供可任选其一的双重许可**：

- 作者拥有并有权授权的自研代码、XAML、项目与构建文件、测试及编译产物中的自研代码部分，适用 [GNU GPL v3.0 only](LICENSES/GPL-3.0-only.txt)。允许商用、修改与收费分发；分发修改版或融合代码的作品时，须履行适用的 GPL 义务并提供对应源码。
- 依据 GPLv3 第 7(b) 条，保留合理的原作者“风尘WD（FengchenWD）”署名，具体见 [原作者署名保留要求](LICENSES/AUTHOR_ATTRIBUTION.md)。**使用软件制作的视频无需为软件作者署名**。
- 作者拥有并有权授权的文档、Logo、头像、图像和其他美术资源，适用 [CC BY-NC-SA 4.0](LICENSES/CC-BY-NC-SA-4.0.txt)。代码允许商用，不代表这些非商业资源也可商用；商业再分发需替换相关资源或另行取得授权。
- 第三方依赖、字体和媒体组件继续适用各自许可，详见 [第三方组件许可与通知](THIRD_PARTY_NOTICES.md)。

完整范围与分域授权说明见 [授权边界](LICENSES/README.md)。发布二进制时，还需按适用许可提供第三方组件的对应源码和必要构建材料，不能只提供本项目自研源码代替。
