using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace WeChatViewer.Desktop;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN).
/// Every startup step is logged, and any fatal exception is shown in a
/// native MessageBox instead of exiting silently.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        StartupLogger.Log("========== 应用启动 ==========");
        StartupLogger.Log("OS: " + Environment.OSVersion.VersionString);
        StartupLogger.Log("进程: " + Environment.ProcessPath);
        StartupLogger.Log("工作目录: " + Environment.CurrentDirectory);
        StartupLogger.Log("命令行: " + string.Join(" ", args ?? Array.Empty<string>()));

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            StartupLogger.Log("ComWrappers 初始化完成");

            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                // WinUI 3 does not install a SynchronizationContext on the UI thread by
                // default. Without it, await continuations resume on thread-pool threads
                // and any UI access throws COMException 0x8001010E (RPC_E_WRONG_THREAD).
                // Install the official DispatcherQueueSynchronizationContext here.
                try
                {
                    if (System.Threading.SynchronizationContext.Current == null)
                    {
                        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                        System.Threading.SynchronizationContext.SetSynchronizationContext(
                            new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(dq));
                    }
                }
                catch (Exception syncEx)
                {
                    StartupLogger.Log("安装 UI SynchronizationContext 失败: " + syncEx);
                }
                StartupLogger.Log("UI 线程 SynchronizationContext: " +
                    (System.Threading.SynchronizationContext.Current?.GetType().FullName ?? "null"));

                StartupLogger.Log("Application.Start: 创建 App 实例");
                new App();
            });

            StartupLogger.Log("应用正常退出");
            return 0;
        }
        catch (Exception ex)
        {
            StartupLogger.Log("启动失败: " + ex);
            ShowFatal(ex, "程序启动失败");
            return 1;
        }
    }

    /// <summary>Shows a native Win32 message box (works even before XAML is up).</summary>
    internal static void ShowFatal(Exception ex, string title)
    {
        try
        {
            var msg = ex?.ToString() ?? "未知错误";
            if (msg.Length > 1600)
            {
                msg = msg.Substring(0, 1600) + "\n…（完整信息见日志文件）";
            }
            MessageBoxW(IntPtr.Zero,
                msg + "\n\n日志目录:\n" + StartupLogger.LogDir,
                title, 0x10 /* MB_ICONERROR */);
        }
        catch
        {
            // never throw while reporting a failure
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}

/// <summary>
/// Lightweight startup logger. Writes to "logs" next to the exe when writable,
/// otherwise falls back to %LOCALAPPDATA%\WeChatViewer.Desktop\logs.
/// Logging must never throw.
/// </summary>
internal static class StartupLogger
{
    public static readonly string LogDir = ResolveLogDir();

    private static readonly object Gate = new();
    private static string _file;

    private static string ResolveLogDir()
    {
        try
        {
            var exe = Assembly.GetEntryAssembly()?.Location;
            var dir = string.IsNullOrEmpty(exe)
                ? AppContext.BaseDirectory
                : Path.GetDirectoryName(exe);
            var path = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, "logs");
            Directory.CreateDirectory(path);
            // verify actually writable
            var probe = Path.Combine(path, ".probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return path;
        }
        catch
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WeChatViewer.Desktop", "logs");
                Directory.CreateDirectory(path);
                return path;
            }
            catch
            {
                return Path.GetTempPath();
            }
        }
    }

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                _file ??= Path.Combine(LogDir, "app-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                File.AppendAllText(_file,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n", Encoding.UTF8);
            }
        }
        catch
        {
            // never throw from logging
        }
    }

    public static void Log(Exception ex) => Log(ex?.ToString() ?? "null exception");
}
