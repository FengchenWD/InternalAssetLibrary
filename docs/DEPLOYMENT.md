# 素材库服务端通用部署教程

此教程仅适用于公开仓库中的素材库服务端与管理后台，不包含官网。所有地址、账号和目录均为示例，必须替换成自己的环境。

## 1. 配置

安装Docker Compose、Git和可用的构建网络。在自己的服务端目录获取源码，检出与客户端对应的标签：

```bash
git clone https://github.com/FengchenWD/InternalAssetLibrary.git
cd InternalAssetLibrary
git checkout v1.2.0
cp deployment/.env.example deployment/.env
```

编辑 `.env`，设置管理员临时密码、随机监控令牌和自己的COS地域、Bucket、永久密钥。监控令牌至少32字符。Bootstrap初次建好管理员后关闭，并及时修改临时密码。永久密钥只进入服务端受保护配置，不能提交Git、放入客户端或发给其他用户。

启动前验证配置（输出可能包含敏感值，不要转发完整输出）：

```bash
docker compose --project-directory deployment config --quiet
docker compose --project-directory deployment up -d --build
docker compose --project-directory deployment ps
curl --fail http://127.0.0.1:5019/healthz
```

默认只监听本机回环地址。公网访问必须配置自己的HTTPS反向代理，或使用可信私有隧道；不要将永久密钥暴露给浏览器。`Http__KnownProxy`按实际可信代理配置，不能信任任意来源的转发头。管理后台访问 `https://library.example.com/admin`。

## 2. 数据与更新

数据卷保存素材库数据库、头像及备份；COS保存素材对象。首次启动和版本升级由程序执行SQLite迁移，失败时停止启动并保留审计。不要手工改库，不运行 `docker compose down -v`。

部署升级前停写并做一致性备份，确认备份完整性及实际恢复流程。升级后检查health、登录、权限、素材列表、传输和管理后台，再允许用户使用。无损降级与可能丢数据的恢复是不同操作；后者必须停写、另存当前数据并明确确认。

客户端更新清单只接受项目固定公钥对应的有效签名。维护者离线签名私钥不能上传GitHub、服务器或COS。自行维护分支时必须使用自己的密钥配对并重新构建相关验证端，不能让生产服务跳过验签。

仅修改服务端不必推送新客户端；仅修改客户端不强制改变服务端版本，但须遵守API兼容性约束。GitHub Release由维护者手动发布，发布安装包时同时提供对应源码、许可与校验值。

## 3. 客户端更新

退出旧客户端及进行中的传输任务，运行对应版本安装包，沿用原安装目录覆盖安装；不要先删除用户素材或数据库。验证版本、登录、媒体运行库、上传/下载及更新流程。Windows未知发布者提示与更新清单签名是不同机制，须检查安装包来源及SHA-256。
