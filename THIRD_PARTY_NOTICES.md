# 第三方组件与素材权利声明

本清单依据 `src/InternalAssetLibrary.Client/obj/project.assets.json` 的当前还原闭包，以及 `artifacts/nuget-offline/`、`artifacts/nuget-packages/` 中随包提供的 `.nuspec`、`LICENSE` 和 `THIRD-PARTY-NOTICES.txt` 编制。它只登记已有证据，不代表对任何第三方作品重新授权。

还原闭包含多平台包；最终安装包只应携带目标 RID 实际需要的文件。每次发布前须按 `publish` 输出复核本清单。

## NuGet 组件

| 组件 | 版本 | 包级许可证 | 版权/作者 | 本地证据 |
|---|---:|---|---|---|
| `Avalonia`、`Avalonia.Desktop`、`Avalonia.Controls.ColorPicker`、`Avalonia.Diagnostics`、`Avalonia.FreeDesktop`、`Avalonia.Native`、`Avalonia.Remote.Protocol`、`Avalonia.Skia`、`Avalonia.Themes.Fluent`、`Avalonia.Themes.Simple`、`Avalonia.Win32`、`Avalonia.X11` | 11.3.7 | MIT | Copyright 2013-2025 © The AvaloniaUI Project | 各包目录中的 `.nuspec`；`Avalonia.Diagnostics` 仅用于 Debug，不进入 Release |
| `Avalonia.BuildServices`（构建期） | 11.3.1 | MIT | Copyright 2023-2025 © The AvaloniaUI Project | `avalonia.buildservices/11.3.1/avalonia.buildservices.nuspec` |
| `Avalonia.Angle.Windows.Natives` | 2.1.25547.20250602 | 随包 BSD 三条款文本 | Copyright 2018 The ANGLE Project Authors | `avalonia.angle.windows.natives/2.1.25547.20250602/LICENSE` |
| `HarfBuzzSharp`、`HarfBuzzSharp.NativeAssets.{Win32,Linux,macOS,WebAssembly}` | 8.3.1.1 | 包级 MIT；原生库另含随包第三方声明 | © Microsoft Corporation. All rights reserved.；HarfBuzz 等上游作者 | 各包 `.nuspec`、`LICENSE.txt`、`THIRD-PARTY-NOTICES.txt` |
| `SkiaSharp`、`SkiaSharp.NativeAssets.{Win32,Linux,macOS,WebAssembly}` | 2.88.9 | 包级 MIT；原生库另含随包第三方声明 | © Microsoft Corporation. All rights reserved.；Skia 等上游作者 | 各包 `.nuspec`、`LICENSE.txt`、`THIRD-PARTY-NOTICES.txt` |
| `MicroCom.Runtime` | 0.11.0 | MIT | Copyright 2021 © Nikita Tsukanov | `microcom.runtime/0.11.0/microcom.runtime.nuspec` |
| `Tmds.DBus.Protocol` | 0.21.3 | MIT | Tom Deseyn | `tmds.dbus.protocol/0.21.3/tmds.dbus.protocol.nuspec` |

表中相对证据路径均位于 `artifacts/nuget-packages/`。Avalonia 各包的 `.nuspec` 记录仓库提交 `0834dbbbb9252406b08f2e74e8f328cc5ba502ee`；这能确定本次审计对应的 11.3.7 包来源。

`0.2.0-preview.5.1` 不再引用或分发 `Avalonia.Fonts.Inter`，界面改用操作系统字体回退链，因此发布物不包含 Inter 字体，也不产生 OFL-1.1 随附义务。

## FFmpeg 媒体运行库

Windows x64 媒体运行库固定采用 Gyan 发布的 `ffmpeg-8.0.1-essentials_build.zip`：

- 上游构建页：`https://www.gyan.dev/ffmpeg/builds/`
- 固定归档：`https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.0.1-essentials_build.zip`
- 归档 SHA-256：`e2aaeaa0fdbc397d4794828086424d4aaa2102cef1fb6874f6ffd29c0b88b673`
- `ffmpeg.exe` SHA-256：`5af82a0d4fe2b9eae211b967332ea97edfc51c6b328ca35b827e73eac560dc0d`
- `ffprobe.exe` SHA-256：`192a1d6899059765ac8c39764fc3148d4e6049955956dc2029f81f4bd6a8972d`

该构建的版本输出声明 `--enable-gpl --enable-version3`，且不含 `--enable-nonfree`；随包许可证为 GNU GPL version 3 or later。发布目录 `runtime/licenses/ffmpeg/` 原样保留该归档的 `LICENSE` 与 `README.txt`，`runtime/SOURCES.md` 记录固定来源、版本、哈希和实际检查结果。本项目的 `GPL-3.0-only` 自研代码不改变 FFmpeg 及其各上游库自身的许可声明。

## mpv / libmpv 媒体运行库

Windows x64 播放运行库固定采用 shinchiro 的 mpv-winbuild-cmake 构建：

- mpv 上游：`https://mpv.io/`、`https://github.com/mpv-player/mpv`
- Windows 构建脚本：`https://github.com/shinchiro/mpv-winbuild-cmake`
- 固定发布：`https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20260814`
- 归档：`mpv-dev-x86_64-v3-20260814-git-7b8915bc1d.7z`
- 归档 SHA-256：`d4d095c6c504a202ec31a3fca5132a53e5f72bfb6e484f960f4850c19f4d62cc`
- 运行时版本：`v0.41.0-923-g7b8915bc1`
- `libmpv-2.dll` SHA-256：`401d0a1e30233aecb5b5c1f1522c1f6e0f4d32e735ab18ea03e6094a9e07e824`

该 DLL 按 mpv 默认的 GNU GPL version 2 or later 边界保守处理；本次组合分发选择 GPL version 3，与项目的 `GPL-3.0-only` 兼容。发布目录 `runtime/licenses/mpv/` 随附 GPL v2 正文与来源说明，`runtime/SOURCES.md` 记录版本、哈希、AMD64 架构和实际加载检查。mpv、MPlayer/mplayer2、FFmpeg 及静态链接的其他上游组件继续保留各自权利；本项目不对它们重新授权。

## Inno Setup 安装器

Windows 安装包由 Inno Setup `6.7.3` 生成。Inno Setup Copyright (C) 1997-2026 Jordan Russell；部分 Copyright (C) 2000-2026 Martijn Laan。其许可允许为任何目的使用、修改和再分发，完整原文随发布物保存为 `Licenses/third-party/Inno-Setup-6.7.3-LICENSE.txt`。安装器及卸载器中属于 Inno Setup 的代码继续适用该许可，不纳入本项目 GPL 自研代码的授权声明。

安装向导使用 `packaging/windows/languages/ChineseSimplified.isl`。该文件是项目维护的修改版简体中文翻译，基于签名有效的 Inno Setup `6.7.3` 安装所附 `Default.isl`（SHA-256 `42a5f6f7dbbddf26cc278f67db5d894235ce1d126a6856702e31bd02023a1316`），并已按上游许可明确标记为修改版。由于构建环境未附带简体中文社区翻译且本次无法通过受控网络取得可审计副本，本项目没有把无法核实来源的社区 ISL 写成官方译文。

## 发布时必须保留

- Windows 安装包至少随附 `Avalonia.Angle.Windows.Natives` 的 `LICENSE`，以及 Win32 版 SkiaSharp、HarfBuzzSharp 的 `LICENSE.txt` 和 `THIRD-PARTY-NOTICES.txt`；不得翻译或删减这些上游声明。
- macOS 版发布时改用并随附相应 macOS 原生包中的声明。不要把未进入目标发布输出的平台包写成已分发组件。
- MIT 包应随附完整 MIT 许可证文本及上表版权信息；OFL 字体应随附完整 OFL-1.1 正文。此文件是审计索引，不能替代缺失的许可证全文。
- Windows self-contained 发布必须随附实际锁定的 Microsoft.NETCore.App Runtime 许可证与 `THIRD-PARTY-NOTICES`；发布前按输出版本复核，不以 SDK 目录中的另一版本替代。
- `Microsoft.Data.Sqlite` 10.0.10、`SQLitePCLRaw` 2.1.11 和腾讯云 COS SDK 5.4.51 已进入当前还原闭包；
  更新器自身不引入额外运行时依赖。`Microsoft.Data.Sqlite` 的 MIT 正文与 `SQLitePCLRaw` 的
  Apache-2.0 正文已归档到 `Licenses/third-party/`。COS SDK 的许可限制见下节。

## SQLite 服务端依赖

`Microsoft.Data.Sqlite` 10.0.10 的 NuGet 元数据声明 `MIT`，Copyright (c) Microsoft
Corporation，完整正文随发布物保存为 `Licenses/third-party/Microsoft.Data.Sqlite-MIT.txt`。

`SQLitePCLRaw` 2.1.11 的当前还原闭包（`bundle_e_sqlite3`、`core`、`lib.e_sqlite3` 和
`provider.e_sqlite3`）的 NuGet 元数据均声明 `Apache-2.0`，Copyright 2014-2024
SourceGear, LLC，完整正文随发布物保存为
`Licenses/third-party/SQLitePCLRaw-Apache-2.0.txt`。

## 腾讯云 COS SDK

`Tencent.QCloud.Cos.Sdk` 5.4.51 来自用户提供的离线 NuGet 包
`artifacts/nuget-offline/tencent.qcloud.cos.sdk.5.4.51.nupkg`（SHA-256
`C07837D57B120BC407BF15F6F0548EAF9B9E31470F1F5CF980F3F35FF3D5DD0C`）。包内程序集为
`COSXML.dll`，目标框架为 `netstandard2.0`。该包的 `.nuspec` 没有声明 license，也没有随包附带
许可证正文。已核对腾讯官方发布仓库 `https://github.com/tencentyun/qcloud-sdk-dotnet`：其 README 明确声明 COS XML .NET SDK 以 MIT 发布，LICENSE 为 Copyright (c) 2018 腾讯云；NuGet 官方页面确认使用的包版本为 5.4.51。许可全文归档于 `LICENSES/third-party/Tencent-COS-SDK-MIT.txt`，不按本项目 GPL 重新授权。本项目只在服务端运行时引用该包，
不会把 COS 密钥写入客户端、源码包或镜像。

## 测试素材

`测试/` 内的音频、视频和 CSV 仅作为本机兼容性测试输入。当前项目没有记录这些第三方素材的授权证明；它们不适用本项目的 GPL-3.0-only 或 CC-BY-NC-SA-4.0，也不得进入安装包、源码发布包或其他对外分发物。
