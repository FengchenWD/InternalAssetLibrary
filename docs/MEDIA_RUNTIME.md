# Windows媒体运行库构建输入

媒体组件保留原许可，不由本项目重新授权。正式发布只使用 [锁文件](../packaging/windows/media-runtime.win-x64.lock.json) 中的版本和SHA-256。

准备 `artifacts/media-runtime/win-x64`，包含 `libmpv-2.dll`、`ffmpeg.exe`、`ffprobe.exe`及相应上游许可文件；该目录是本机构建输入，不上传Git仓库。

- FFmpeg/ffprobe：8.0.1 essentials build，发行方为 https://www.gyan.dev/ffmpeg/builds/ ，原归档为 https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.0.1-essentials_build.zip 。保留归档里的LICENSE和README到 `licenses/ffmpeg/`；不得使用 `--enable-nonfree` 构建替代。
- libmpv：`v0.41.0-923-g7b8915bc1`，Windows构建来源为 https://github.com/shinchiro/mpv-winbuild-cmake ，上游为 https://mpv.io/ 。须取得锁文件对应字节的AMD64构建，保留GPL及其他上游声明到 `licenses/mpv/`。不能仅以“最新版本”替代锁定文件。
- 安装编译器：Inno Setup 6，构建脚本验证其Windows签名和版本。

准备完毕后运行：

```powershell
pwsh -File scripts/publish-client.ps1 -Version 1.2.0
```

脚本检查锁定哈希、PE架构、运行库版本和许可文件，生成安装包、对应源码和校验清单，不生成便携包。

手动发布Release前，还应确认GPL媒体组件对应上游源码及必要构建材料的提供方式满足适用许可；本项目自研源码不能替代第三方组件的对应源码。Git仓库不缓存媒体二进制或第三方整份源码。
