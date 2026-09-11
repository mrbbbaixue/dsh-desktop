# dsh-desktop

[![build](https://github.com/mrbbbaixue/dsh-desktop/actions/workflows/build.yml/badge.svg)](https://github.com/mrbbbaixue/dsh-desktop/actions/workflows/build.yml)
[![license](https://img.shields.io/github/license/mrbbbaixue/dsh-desktop)](LICENSE)

> DeepSeek Harness 的 Windows 桌面启动器:.NET Framework 4.8 WPF + WebView2,独立进程托管 dsh 服务,托盘管理,原生标题栏深浅色跟随系统。原版 Windows 11 即可启动,无需另装 .NET。

## 特性

- 🪟 **WPF + WebView2,HiDPI** — 多显示器不同缩放比例下自动适配,内存占用低
- ⚙️ **托管 dsh 子进程** — 自启自停,只管理自己的进程树,不接管、不误杀端口上其它进程,意外退出自动重启
- 🖥️ **系统托盘** — 右键菜单重启 dsh 服务、显示/隐藏 dsh 终端(后台控制台,默认隐藏)、打开窗口、退出
- 🔁 **插件市场的「立即重启」由壳执行** — 市场原本自己 kill dsh 再另起一个壳跟踪不到的替代进程,现在这个请求被壳接管,改由壳重启自己托管的子进程(安装进行中会照市场那样挡下)
- 🔌 **自动拉起 + 环境检测** — 服务未启动时自动启动并等待就绪;启动页显示 Node.js / npm / dsh 安装状态

## 安装 / 运行

Windows 11 自带 .NET Framework 4.8(22H2+ 为 4.8.1)与 WebView2 Runtime,直接运行 `DshDesktop.exe` 即可。

dsh 不必全局安装:优先用 PATH 中的 `dsh`,否则自动回退 `npx -y @deepseek-ai/dsh`。

## 从源码构建

```powershell
git clone https://github.com/mrbbbaixue/dsh-desktop.git
cd dsh-desktop
dotnet test                        # 单元测试
./Scripts/build.ps1                # 打包 zip + SHA256
```

## 常见问题

- **双击没反应 / 一直显示"正在启动 dsh 服务…"?** 从托盘菜单「显示 dsh 终端」查看 dsh 控制台输出;也可查看日志 `%USERPROFILE%\.dsh\desktop.log`,确认 Node.js 已安装。首次 npx 下载较慢时会在后台继续等待,也可设 `DSH_NPM_REGISTRY` 镜像后从托盘菜单重启。
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
