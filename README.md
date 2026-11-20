# 内网 SQL → 云同步代理（Intranet Sync Agent）

面向 Windows Server 2012 R2+ / **.NET Framework 4.5.1**（系统自带，无需再装 4.8）的无窗口 Windows 服务：按增量水位从 SQL Server 视图同步到云端 API，PDF 经内网下载后直传阿里云 OSS，**库与 JSON 只存云端 URL**。

## 同步要点

| 项 | 说明 |
|----|------|
| 增量键 | `updated_at` + `person_id` 复合水位游标 |
| 云端 upsert | 以 `person_id` 为业务主键覆盖 |
| PDF 失败 | 仅失败行不推送，写入失败队列；本轮末尾按 `person_id` 回查视图重试 |
| 定时同步 | 默认启用，按配置间隔跑整轮（查库 → PDF → 推云） |
| 安全 | 管理台仅 `127.0.0.1`；密码/Token DPAPI 或 AES 加密，日志打码 |

详细架构见仓库旁 `docs/architecture.md`（bundle 目录）或项目文档。

## 解决方案结构

```
intranet-sync-agent/
  IntranetSyncAgent.sln
  build/
    build-release.bat      # NuGet restore + MSBuild Release
    build-installer.bat    # Release + Inno Setup → Setup.exe
  installer/
    IntranetSyncAgent.iss  # Inno Setup（推荐 Server 2012）
    OpenAdmin.cmd
    InstallService.cmd
    UninstallService.cmd
  src/
    IntranetSync.Core/     # 配置加密、同步引擎、PDF 管道、云客户端
    IntranetSync.Admin/    # 127.0.0.1 HttpListener 管理台
    IntranetSync.Service/  # Windows Service + --console 调试
  dist/                    # 构建产物 IntranetSyncAgent-Setup.exe（gitignore）
```

相关文档（若使用完整 bundle）：

- `docs/architecture.md` — 架构与数据流
- `docs/api-contract.md` / `docs/openapi.yaml` — 云 API 契约
- `docs/deploy-security.md` — 部署与安全清单
- `docs/desktop-delivery.md` — Setup.exe / 源码交付说明
- `cloud-api-stub/` — 本地 Mock API（无需真实阿里云）

## 在 Windows 上构建

> 本仓库可在 macOS/Linux 编辑，但 **必须在 Windows + MSBuild/.NET 4.5.1 Targeting Pack** 上编译；**Setup.exe 也须在 Windows 上用 Inno Setup 编译**。

### 推荐：一键打安装包

1. 安装 Visual Studio 2019/2022（或 Build Tools）+ .NET Framework 4.5.1 Targeting Pack  
2. 安装 [Inno Setup](https://jrsoftware.org/isinfo.php) 5 或 6  
3. 在项目根目录运行：

```bat
build\build-installer.bat
```

产出：`dist\IntranetSyncAgent-Setup.exe`

| 环境变量 | 含义 |
|----------|------|
| `ISCC` | `ISCC.exe` 完整路径（未设则搜索 Inno Setup 5/6 默认目录） |
| `MSBUILD_PATH` | `MSBuild.exe` 完整路径 |
| `NUGET` / PATH 上的 `nuget` | NuGet CLI；缺失时脚本会尝试下载 `nuget.exe` |

仅编译 Release、不打安装包：

```bat
build\build-release.bat
```

### 手动命令行

```bat
nuget restore IntranetSyncAgent.sln
msbuild IntranetSyncAgent.sln /p:Configuration=Release /p:Platform="Any CPU"
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\IntranetSyncAgent.iss
```

## 安装（推荐 Setup.exe）

1. 目标机：**Windows Server 2012 R2+**（自带 .NET 4.5.1）  
2. 以**管理员**运行 `IntranetSyncAgent-Setup.exe`  
3. 安装到 Program Files，注册并启动服务 `IntranetSyncAgent`  
4. 开始菜单：**Open Admin UI**、**Uninstall**；可选桌面快捷方式  
5. **安装过程不要求填写云凭证** — 装完后在管理台配置  

默认管理台：`http://127.0.0.1:8787/`（若改过端口，快捷方式会读 `%ProgramData%\IntranetSyncAgent\config.json`）。

## 运行（无安装包时）

### 控制台调试

```bat
cd src\IntranetSync.Service\bin\Release
IntranetSync.Service.exe --console
```

### 手动安装为 Windows 服务

```bat
cd src\IntranetSync.Service\bin\Release
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\InstallUtil.exe IntranetSync.Service.exe
sc start IntranetSyncAgent
```

或复制 Release 目录后以管理员运行 `InstallService.cmd`。无 InstallUtil 时脚本会回退 `sc create`。

## 本地联调（Mock 云）

```bash
cd cloud-api-stub
node server.js
```

管理台填写：API Base `http://127.0.0.1:3900`，Token `dev-token`，勾选 Use STS。

## 管理台能力

1. SQL 连接测试  
2. 视图名 / 主键（默认 `person_id`）/ 增量列（`updated_at`）/ PDF URL 列  
3. 云 API Base + Token；OSS / STS  
4. 同步间隔、立即跑一轮、查看最近日志与 PDF 失败队列  

配置目录：`%ProgramData%\IntranetSyncAgent\`（密钥加密，日志打码）。

| 文件 | 用途 |
|------|------|
| `config.json` | 配置 |
| `watermarks.json` | 视图水位 |
| `pdf-hash-map.json` | SHA256 → OSS URL |
| `pdf-failures.json` | PDF 失败队列 |
| `row-hashes.json` | 已成功同步行哈希（按 `person_id`） |
| `sync.log` | 运行日志 |

## 进度对照

| 模块 | 状态 | 说明 |
|------|------|------|
| agent-shell | 完成 | Service + console + HttpListener 管理台 + 加密配置 |
| sync-engine | 完成 | 水位、分页 SELECT、batch upsert、失败队列重试 |
| pdf-pipeline | 完成 | 流式下载、SHA256 去重、STS/Mock OSS、URL 回写 |
| cloud-contract | 完成 | OpenAPI/Markdown + Node stub |
| security-ops | 完成 | deploy-security 清单 |
| windows-installer | 完成 | Inno Setup + build bats；Setup.exe 须本机 Windows 编译 |
