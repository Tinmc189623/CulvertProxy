# 贡献指南

CulvertProxy 是 Nexsteaduser 的 Windows 进程级代理客户端。欢迎提交缺陷修复、文档改进、测试和经过验证的功能。

## 环境

- Windows 11 24H2（build 26100）或更高，x64
- .NET 11 SDK
- PowerShell 7
- Visual Studio 2022，勾选「使用 C++ 的桌面开发」与 Windows SDK 10.0.26100

完整构建：

```powershell
pwsh -NoProfile -File .\build.ps1
```

原生 Hook 的冒烟测试会真实注入进程，建议在受控环境运行：

```powershell
pwsh -NoProfile -File .\build.ps1 -Smoke
```

## 分支与提交

- 分支名使用 `feat/`、`fix/`、`perf/`、`docs/` 或 `chore/` 前缀，例如 `fix/socks5-timeout`
- 一个 PR 只处理一个主题，避免把格式化、重构和功能混在一起
- 提交信息使用简短的英文 conventional 前缀，正文说明验证方式
- PR 建立后保持与 `main` 同步，冲突由提交者解决

## 代码要求

### .NET

- 启用 nullable，避免无理由的 `!` 和吞异常
- 代理路径上的 I/O 使用异步 API，并传递取消令牌
- 新增公共类型和行为需要函数级注释；注释写原因，不复述代码
- 代理决策失败时必须保持失败关闭，不允许为了可用性静默直连

### 原生 Hook

- 使用 C11，保持与 MinHook 的兼容
- 修改 `CulvertHook.c` 后必须运行原生探针和 `SmokeTest --release-test`
- 新增 Hook 时要说明失败时会回退到哪个原始函数
- 不引入会阻塞目标进程加载器的初始化逻辑

### 界面

- 使用 WinUI 3 与现有资源样式，保持深色、浅色和高对比度主题可用
- 界面线程只能由界面线程更新；后台事件通过 `DispatcherQueue` 回到 UI
- 提交界面改动时附上截图，并说明最小窗口尺寸下的表现

## 测试

```powershell
dotnet test .\tests\Nexsteaduser.CulvertProxy.Tests\Nexsteaduser.CulvertProxy.Tests.csproj -c Release
```

- SOCKS5 / HTTP 协议改动需要覆盖成功、拒绝、超时和半关闭
- 路由改动需要覆盖进程、域名、IP 段、端口和失败关闭
- DNS 改动需要覆盖 DoH 失败、静态 hosts、fake-IP 可用性门控
- 影响发布脚本时，运行 `build.ps1 -Publish` 并从解压后的包启动一次 GUI 与 CLI

## 安全

请不要在公开 Issue 中披露可利用漏洞、令牌或用户数据。安全问题发送到 `admin@nexsteaduser.com`，详细要求见 [SECURITY.md](SECURITY.md)。

## 许可与品牌

提交即表示你同意以 Apache-2.0 许可证贡献代码。产品名是 `CulvertProxy`，品牌名必须完整写作 `Nexsteaduser`。版权行保持：

```text
Copyright © 2026 Nexsteaduser. All Rights Reserved.
```
