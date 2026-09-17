<img src="./DshDesktop/Resources/deepseek.png" width="256" height="256" alt="icon" />

# dsh-desktop

[![build](https://github.com/mrbbbaixue/dsh-desktop/actions/workflows/build.yml/badge.svg)](https://github.com/mrbbbaixue/dsh-desktop/actions/workflows/build.yml)
[![license](https://img.shields.io/github/license/mrbbbaixue/dsh-desktop)](LICENSE)

> DeepSeek Harness 的 Windows 桌面启动器:.NET Framework 4.8 WPF + WebView2,独立进程托管 dsh 服务,托盘管理,原生标题栏深浅色跟随系统。原版 Windows 11 即可启动,无需另装 .NET。

## 特性

- 🪟 **WPF + WebView2,HiDPI** — 多显示器不同缩放比例下自动适配,内存占用低
- ⚙️ **托管 dsh 子进程** — 自启自停,只管理自己的进程树,不接管、不误杀端口上其它进程,意外退出自动重启
- 🖥️ **系统托盘** — 右键菜单重启 dsh 服务、显示/隐藏 dsh 终端(后台控制台,默认隐藏)、打开窗口、打开诊断窗口、在浏览器中打开 dsh 页面、退出
- 🔌 **自动拉起 + 环境检测** — 服务未启动时自动启动并等待就绪;启动页显示 Node.js / npm / dsh 安装状态
- 🩺 **诊断窗口** — 一屏看清 Node.js / npm / WebView2 / dsh 的安装情况,缺什么一键装(Node 走 winget,国内可用 npmmirror 镜像),进度同时显示在窗口与任务栏按钮上;首次运行会自动弹出

## 安装 / 运行

Windows 11 自带 .NET Framework 4.8(22H2+ 为 4.8.1)与 WebView2 Runtime,直接运行 `DshDesktop.exe` 即可。

dsh 不必全局安装:优先用 PATH 中的 `dsh`,否则自动回退 `npx -y @deepseek-ai/dsh`。

## 诊断窗口

托盘菜单「诊断窗口」打开;首次运行(配置文件 `%USERPROFILE%\.dsh\desktop.xml` 里没有 `firstRunDone`)会自动弹一次。

- **环境诊断** — Node.js / npm / WebView2 Runtime / dsh 四项,显示版本与路径,「重新检测」随时刷新。dsh 服务状态不在其中:壳会自己拉起服务,拿到启动链接即视为成功
- **一键安装** — 只装缺的,一律装到系统(Node.js 用 winget 装 `OpenJS.NodeJS.LTS`,npm/npx 随附;dsh 用 `npm install -g` 装到 `%APPDATA%\npm`)。系统级安装会弹一次 UAC,点「是」即可;装完自动重新检测并重启 dsh 服务,无需重启本程序
- **镜像源** — `安装源` 可选「官方源(winget)」或「国内镜像(npmmirror)」:后者从 npmmirror 下载 Node MSI 并安装(下载阶段有真实百分比进度),dsh 也走 npmmirror registry
- **进度** — 窗口内进度条 + 实时日志,任务栏按钮同步显示:下载阶段是绿色百分比条,其余阶段是滚动条,失败变红,取消或完成即清空;空闲时进度条整条收起

## 从源码构建

```powershell
git clone https://github.com/mrbbbaixue/dsh-desktop.git
cd dsh-desktop
dotnet test                        # 单元测试
./Scripts/build.ps1                # 打包 zip + SHA256
./Scripts/build.ps1 -IconSet deepseek   # 第二套图标(程序图标 + 任务栏图标),托盘图标不变
```

发布包:`dsh-desktop-<版本>-win-x64.zip` 与 `dsh-desktop-deepseek-<版本>-win-x64.zip` 仅图标集不同。

## 常见问题

- **双击没反应 / 一直显示"正在启动 dsh 服务…"?** 从托盘菜单打开「诊断窗口」看环境是否齐全(缺 Node.js / WebView2 会直接标红),一键安装即可;也可从托盘菜单「显示 dsh 终端」查看 dsh 控制台输出,或查看日志 `%USERPROFILE%\.dsh\desktop.log`。首次 npx 下载较慢时会在后台继续等待,也可设 `DSH_NPM_REGISTRY` 镜像后从托盘菜单重启。
- **提示缺少 .NET Framework 4.8?** Windows 11 自带;精简系统或旧版 Windows 10 需自行安装。

## 环境变量

| 变量 | 作用 |
| --- | --- |
| `DSH_WEB_URL` | 覆盖目标地址(默认 `http://127.0.0.1:3080`);设置后视为外部托管服务,壳不再自动拉起 / 停止 dsh |
| `DSH_NPM_REGISTRY` | 指定 npm 镜像源(仅 npx 回退路径生效,如 `https://registry.npmmirror.com`) |

## 免责声明

本仓库是**独立的第三方工具**,与 DeepSeek / DeepSeek AI 官方无关。[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)(`dsh`)是官方项目(MIT)。窗口图标使用了 DeepSeek 品牌标识,版权归 DeepSeek 所有,如构成侵权请联系我删除。

## 许可证

[MIT](LICENSE) © mrbbbaixue
