# dsh-desktop

[![build](https://github.com/mrbbbaixue/dsh-desktop/actions/workflows/build.yml/badge.svg)](https://github.com/mrbbbaixue/dsh-desktop/actions/workflows/build.yml)
[![license](https://img.shields.io/github/license/mrbbbaixue/dsh-desktop)](LICENSE)

> DeepSeek Harness 的 Windows 桌面启动器:.NET Framework 4.8 WPF + WebView2,独立进程托管 dsh 服务,托盘管理,原生标题栏深浅色跟随系统。原版 Windows 11 即可启动,无需另装 .NET。

## 特性

- 🪟 **WPF + WebView2,HiDPI** — PerMonitorV2 感知,多显示器不同缩放比例下自动适配;窗口约 50–150MB,关窗即释放
- 🔌 **本地直连,绕过系统代理** — WebView2 访问 127.0.0.1 / DSH 地址默认直连,不被公司代理、Clash 等系统代理拦截
- ⚙️ **独立子进程托管 dsh** — 壳自己拉起、自己停止,只管理这一棵进程树;不接管、不误杀端口上其它进程;启动参数与页面访问地址同源;意外退出自动重启(节流防循环)
- 🖥️ **系统托盘** — 右键托盘菜单可随时 **启动 / 重启 / 停止** dsh 服务;关窗隐藏到托盘,服务常驻
- 🎨 **原生标题栏深浅色** — 自动跟随 Windows 深浅色主题,无需手动切换
- 🔌 **自动拉起** — 服务没开时自动启动并等待就绪;就绪后窗口自动加载
- 🔎 **环境检测** — 启动时在窗口与日志中显示一行"已安装/未安装":Node.js、npm、dsh;Node.js 缺失时直接提示安装,不再只报笼统的"未能就绪"
- 📋 **日志** — `%USERPROFILE%\.dsh-desktop.log`

## 安装 / 运行

需要 **.NET Framework 4.8**(Windows 11 21H2 自带;22H2+ 为 4.8.1)与 WebView2 Runtime(Windows 10/11 自带)。原版 Windows 11 无需另装运行时。

> dsh 不必全局安装:启动器优先用 PATH 中的 `dsh`,否则自动回退 `npx -y @deepseek-ai/dsh`。

**换 npm 镜像(可选)**:npx 回退下载过慢时,设置环境变量 `DSH_NPM_REGISTRY` 即可免改代码换源:

```powershell
$env:DSH_NPM_REGISTRY = "https://registry.npmmirror.com"
DshDesktop.exe
```

## 从源码构建

```powershell
git clone https://github.com/mrbbbaixue/dsh-desktop.git
cd dsh-desktop
dotnet test                        # 单元测试(ShellLogic 策略)
./Scripts/build.ps1                # 打包:zip + SHA256(Costura 单文件 exe + .config,依赖系统自带的 .NET Framework 4.8)
```

产物在 `build\`:
- `dsh-desktop-<版本>-win-x64.zip` — 依赖 Windows 自带的 .NET Framework 4.8 / 4.8.1。解压后是 `DshDesktop.exe` 与 `.config`(Costura 已把 WebView2 等托管 DLL 嵌进 exe),原版 Windows 11 可直接运行。

推送 `vX.Y.Z` 标签会走 GitHub Actions:测试 → 打包 zip → 创建 [GitHub Release](https://github.com/mrbbbaixue/dsh-desktop/releases)。`main` / PR 也会上传产物到 workflow artifacts。编译需要 [.NET SDK](https://dotnet.microsoft.com/download)(10.x 即可)。

```powershell
git tag v1.1.0
git push origin v1.1.0
```

## 常见问题

**Q:双击没反应?**
查看日志 `%USERPROFILE%\.dsh-desktop.log`,确认 Node.js 已安装。

**Q:提示缺少 .NET Framework 4.8?**
Windows 11 自带 4.8/4.8.1,一般不会出现。若在精简系统或极旧 Windows 10 上,请安装 [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48) 后重试。

**Q:窗口一直显示"正在启动 dsh 服务…"?**
等待页下方有一行环境检测(如 `Node.js 已安装 (v22.14.0) · npm 已安装 · dsh 未安装(将自动用 npx 启动)`)。若显示 `Node.js 未安装`,请先安装 Node.js 后从托盘菜单重试。服务 90 秒内未就绪(常见于 npx 首次下载慢)时,壳会在后台继续等待,下载完成会自动加载页面;也可设置 `DSH_NPM_REGISTRY` 镜像后,托盘菜单「重启 dsh 服务」。

**Q:窗口显示 `dsh web authentication required; reopen the URL printed by dsh web`?**
新版 dsh(0.1.2+)用一次性 launch-token 换 cookie,直接打开 `http://127.0.0.1:3080` 会 401。壳会从子进程输出里读取 `dsh web:` 那条完整 URL 再导航;若仍看到此提示,从托盘菜单「重启 dsh 服务」。

**Q:网页里"选择目录"报 `directory picker failed: spawn C:\Program Files\nodejs\node.exe ENOENT`,或日志写"端口已被占用"?**
3080 端口被其它程序(或以前残留的 dsh)占用时,壳不会接管、也不会去杀那个进程。请先手动结束占用端口的程序,再从托盘菜单「启动 / 重启 dsh 服务」。确认 Node.js 已安装后重试。

## 环境变量

| 变量 | 作用 |
| --- | --- |
| `DSH_WEB_URL` | 覆盖目标地址(默认 `http://127.0.0.1:3080`);设置后视为外部托管服务,壳不再自动拉起/停止 dsh |
| `DSH_NPM_REGISTRY` | 指定 npm 镜像源(仅 npx 回退路径生效,如 `https://registry.npmmirror.com`) |

## 免责声明

本仓库是**独立的第三方工具**,与 DeepSeek / DeepSeek AI 官方无关。[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)(`dsh`)是官方项目(MIT)。窗口图标使用了 DeepSeek 品牌标识,版权归 DeepSeek 所有,如构成侵权请联系我删除。

## 许可证

[MIT](LICENSE) © mrbbbaixue

---

## English

> A Windows desktop launcher for DeepSeek Harness: .NET Framework 4.8 WPF + WebView2, dsh hosted in a managed child process with a system-tray controller, and the native title bar follows the system dark/light theme. Runs on a stock Windows 11 install with no extra .NET runtime.

### Features

- 🪟 **WPF + WebView2, HiDPI** — PerMonitorV2-aware, adapts across monitors with mixed scaling; window ~50–150MB, freed on close
- 🔌 **Loopback bypasses the system proxy** — WebView2 talks to 127.0.0.1 / DSH directly, not through corporate proxies or Clash
- ⚙️ **Managed child process** — the shell starts and stops its own dsh child tree only (no VBS); it will not adopt or kill other processes on the port; launch args share the window's resolved port; auto-restarts on unexpected exit (throttled)
- 🖥️ **System tray** — right-click to **start / restart / stop** the dsh service; closing the window hides to tray, the service keeps running
- 🎨 **Native title bar theme** — automatically follows the Windows dark/light mode
- 🔌 **Auto-launch** — starts the service if not running and waits until ready
- 📋 **Logging** — `%USERPROFILE%\.dsh-desktop.log`

### Requirements

[.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48) (inbox on Windows 11: 4.8 on 21H2, 4.8.1 from 22H2) and WebView2 Runtime (built into Windows 10/11). Node.js 18+ is required for the dsh service. A global dsh install is optional — the launcher falls back to `npx -y @deepseek-ai/dsh` automatically; set `DSH_NPM_REGISTRY` (e.g. `https://registry.npmmirror.com`) to use a faster npm registry.

### Build from source

Building requires a [.NET SDK](https://dotnet.microsoft.com/download) (10.x is fine). The app itself targets .NET Framework 4.8.

```powershell
git clone https://github.com/mrbbbaixue/dsh-desktop.git
cd dsh-desktop
dotnet test
./Scripts/build.ps1
```

Artifacts land in `build\`:
- `dsh-desktop-<version>-win-x64.zip` — unzip and run `DshDesktop.exe` (Costura embeds WebView2 managed DLLs; keep the `.config` beside the exe)

Pushing a `vX.Y.Z` tag runs GitHub Actions: test → zip → [GitHub Release](https://github.com/mrbbbaixue/dsh-desktop/releases). `main` / PRs also upload the zip as a workflow artifact.

### Disclaimer & License

Independent third-party tool, not affiliated with DeepSeek / DeepSeek AI. The app icon uses the DeepSeek brand mark, whose copyright belongs to DeepSeek; if this constitutes infringement, please contact me to have it removed. [MIT](LICENSE) © mrbbbaixue.
