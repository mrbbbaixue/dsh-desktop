using System.IO;

namespace DshDesktop.Infrastructure;

/// <summary>
/// 简单文件日志:%USERPROFILE%\.dsh\desktop.log。
/// 启动时若超过 2MB 则轮转为 desktop.log.old;写入失败静默(日志绝不影响主流程)。
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");

    private static string Path_ => Path.Combine(Dir, "desktop.log");

    /// <summary>应用启动时调用一次:建目录、超限轮转。</summary>
    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var file = Path_;
            if (File.Exists(file) && new FileInfo(file).Length > 2 * 1024 * 1024)
            {
                File.Copy(file, file + ".old", overwrite: true);
                File.Delete(file);
            }
        }
        catch { /* ignore */ }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(Path_,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { /* ignore */ }
    }
}
