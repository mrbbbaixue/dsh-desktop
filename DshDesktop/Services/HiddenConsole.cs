using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DshDesktop.Services;

/// <summary>
/// 隐藏的真实控制台:用 CREATE_NEW_CONSOLE + SW_HIDE 拉起子进程,
/// 默认不出现在任务栏;之后用 ShowWindow 切换显示/隐藏。
/// AttachConsole 仅用于查找 HWND 和读取屏幕缓冲(抓 launch-token),用完立即 FreeConsole,
/// 避免本进程一直挂在子进程控制台上(关控制台会把壳一起带走)。
/// </summary>
internal static class HiddenConsole
{
    internal const string Title = "dsh 终端";

    private const uint CREATE_NEW_CONSOLE = 0x00000010;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint SC_CLOSE = 0xF060;
    private const uint MF_BYCOMMAND = 0;
    private const uint MF_GRAYED = 1;
    private const uint MF_DISABLED = 2;
    private const uint CTRL_C_EVENT = 0;
    private const uint CTRL_BREAK_EVENT = 1;
    private const uint CTRL_CLOSE_EVENT = 2;

    private static readonly object AttachGate = new();
    // 必须钉在静态字段上,否则 SetConsoleCtrlHandler 的委托会被 GC 掉
    private static readonly HandlerRoutine IgnoreCtrl = type =>
        type is CTRL_C_EVENT or CTRL_BREAK_EVENT or CTRL_CLOSE_EVENT;

    static HiddenConsole()
    {
        SetConsoleCtrlHandler(IgnoreCtrl, true);
    }

    /// <summary>
    /// 以隐藏控制台启动进程。extraEnv 为空时继承当前环境;
    /// 非空时在完整环境块上覆盖(CreateProcess 不支持"只追加几项")。
    /// </summary>
    public static Process Start(
        string fileName, string arguments, IReadOnlyDictionary<string, string> extraEnv)
    {
        var exe = Path.IsPathRooted(fileName)
            ? fileName
            : Path.Combine(Environment.SystemDirectory, fileName);
        var line = new StringBuilder(1024);
        line.Append('"').Append(exe).Append('"');
        if (!string.IsNullOrEmpty(arguments))
            line.Append(' ').Append(arguments);

        var titlePtr = Marshal.StringToHGlobalUni(Title);
        var si = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            lpTitle = titlePtr,
            dwFlags = STARTF_USESHOWWINDOW,
            wShowWindow = SW_HIDE,
        };
        var env = IntPtr.Zero;
        if (extraEnv.Count > 0)
            env = BuildEnvironmentBlock(extraEnv);

        PROCESS_INFORMATION pi;
        bool ok;
        try
        {
            ok = CreateProcess(
                exe, line, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_NEW_CONSOLE | CREATE_UNICODE_ENVIRONMENT,
                env, null, ref si, out pi);
        }
        finally
        {
            Marshal.FreeHGlobal(titlePtr);
            if (env != IntPtr.Zero) Marshal.FreeHGlobal(env);
        }
        if (!ok)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess 启动隐藏控制台失败");

        try
        {
            if (pi.hThread != IntPtr.Zero)
            {
                CloseHandle(pi.hThread);
                pi.hThread = IntPtr.Zero;
            }
            // net48 的 GetProcessById 没有 Start 那套句柄,ExitCode 不可用;
            // HasExited / EnableRaisingEvents / Kill 可用,壳只用这三项。
            var p = Process.GetProcessById(pi.dwProcessId);
            if (pi.hProcess != IntPtr.Zero)
            {
                CloseHandle(pi.hProcess);
                pi.hProcess = IntPtr.Zero;
            }
            return p;
        }
        catch
        {
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero)
            {
                TerminateProcess(pi.hProcess, 1);
                CloseHandle(pi.hProcess);
            }
            throw;
        }
    }

    /// <summary>等到子进程控制台 HWND 出现;超时返回 Zero。</summary>
    public static async Task<IntPtr> WaitForWindowAsync(int pid, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var hwnd = FindWindow(pid);
            if (hwnd != IntPtr.Zero)
            {
                // 先藏再交给调用方按意图 Show,压掉 CREATE_NEW_CONSOLE 可能的闪窗
                Hide(hwnd);
                DisableCloseButton(hwnd);
                return hwnd;
            }
            await Task.Delay(50);
        }
        return IntPtr.Zero;
    }

    /// <summary>AttachConsole 取子进程控制台 HWND;失败返回 Zero。</summary>
    public static IntPtr FindWindow(int pid)
    {
        if (pid <= 0) return IntPtr.Zero;
        lock (AttachGate)
        {
            if (!AttachConsole((uint)pid))
                return IntPtr.Zero;
            try
            {
                var hwnd = GetConsoleWindow();
                if (hwnd == IntPtr.Zero) return IntPtr.Zero;
                TryPrepare();
                return hwnd;
            }
            finally
            {
                FreeConsole();
            }
        }
    }

    public static bool IsAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    public static bool IsVisible(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindowVisible(hwnd);

    public static void Show(IntPtr hwnd)
    {
        if (!IsAlive(hwnd)) return;
        DisableCloseButton(hwnd);
        ShowWindow(hwnd, IsIconic(hwnd) ? SW_RESTORE : SW_SHOW);
        SetForegroundWindow(hwnd);
    }

    public static void Hide(IntPtr hwnd)
    {
        if (!IsAlive(hwnd)) return;
        ShowWindow(hwnd, SW_HIDE);
    }

    /// <summary>去掉控制台标题栏关闭按钮,避免点 X 把 dsh 一起杀掉(隐藏请走托盘)。</summary>
    public static void DisableCloseButton(IntPtr hwnd)
    {
        if (!IsAlive(hwnd)) return;
        var menu = GetSystemMenu(hwnd, false);
        if (menu == IntPtr.Zero) return;
        DeleteMenu(menu, SC_CLOSE, MF_BYCOMMAND);
        EnableMenuItem(menu, SC_CLOSE, MF_BYCOMMAND | MF_GRAYED | MF_DISABLED);
        DrawMenuBar(hwnd);
    }

    /// <summary>
    /// 读取控制台屏幕缓冲区全文(含滚动历史)。附加失败返回 null。
    /// 缓冲区是宽×高字符矩阵,逐行 TrimEnd 后拼接,便于从中解析 `dsh web:` URL。
    /// </summary>
    public static string? ReadSnapshot(int pid)
    {
        if (pid <= 0) return null;
        lock (AttachGate)
        {
            if (!AttachConsole((uint)pid))
                return null;
            try
            {
                var handle = GetStdHandle(STD_OUTPUT_HANDLE);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                    return null;
                if (!GetConsoleScreenBufferInfo(handle, out var info))
                    return null;
                var width = info.dwSize.X;
                var rows = info.dwSize.Y;
                if (width <= 0 || rows <= 0 || rows > 20000)
                    return null;
                var chars = new char[width * rows];
                if (!ReadConsoleOutputCharacter(handle, chars, (uint)chars.Length, new COORD(), out var read)
                    || read == 0)
                    return null;
                var sb = new StringBuilder((int)read);
                var line = new StringBuilder(width);
                for (var i = 0; i < read; i++)
                {
                    var c = chars[i];
                    if (c == '\0') c = ' ';
                    line.Append(c);
                    if ((i + 1) % width != 0) continue;
                    var trimmed = line.ToString().TrimEnd();
                    if (trimmed.Length > 0)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(trimmed);
                    }
                    line.Clear();
                }
                return sb.ToString();
            }
            finally
            {
                FreeConsole();
            }
        }
    }

    /// <summary>首次附加时设标题、加长滚动缓冲(URL 与 npx 日志别被 80×300 默认缓冲卷掉)。</summary>
    private static void TryPrepare()
    {
        try
        {
            SetConsoleTitle(Title);
            var handle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
            if (!GetConsoleScreenBufferInfo(handle, out var info)) return;
            var winW = (short)Math.Max(1, info.srWindow.Right - info.srWindow.Left + 1);
            var winH = (short)Math.Max(1, info.srWindow.Bottom - info.srWindow.Top + 1);
            // 加宽缓冲,避免 launch-token URL 在 80 列折行后解析失败
            var w = info.dwSize.X < 500 ? (short)500 : info.dwSize.X;
            if (w < winW) w = winW;
            var h = info.dwSize.Y < 9000 ? (short)9000 : info.dwSize.Y;
            if (h < winH) h = winH;
            if (w != info.dwSize.X || h != info.dwSize.Y)
                SetConsoleScreenBufferSize(handle, new COORD { X = w, Y = h });
        }
        catch { /* 准备失败不影响启动 */ }
    }

    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string> extra)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key || key.Length == 0) continue;
            map[key] = entry.Value as string ?? "";
        }
        foreach (var kv in extra)
            map[kv.Key] = kv.Value;

        var sb = new StringBuilder();
        foreach (var kv in map)
        {
            sb.Append(kv.Key);
            sb.Append('=');
            sb.Append(kv.Value);
            sb.Append('\0');
        }
        sb.Append('\0');
        var bytes = Encoding.Unicode.GetBytes(sb.ToString());
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    private delegate bool HandlerRoutine(uint dwCtrlType);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMALL_RECT
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize;
        public COORD dwCursorPosition;
        public ushort wAttributes;
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetConsoleTitle(string lpConsoleTitle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(
        IntPtr hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleScreenBufferSize(IntPtr hConsoleOutput, COORD dwSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ReadConsoleOutputCharacter(
        IntPtr hConsoleOutput, char[] lpCharacter, uint nLength, COORD dwReadCoord, out uint lpNumberOfCharsRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool add);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    private static extern bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

    [DllImport("user32.dll")]
    private static extern bool DeleteMenu(IntPtr hMenu, uint uPosition, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DrawMenuBar(IntPtr hWnd);
}
