using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace WeChatViewer.Desktop;

/// <summary>Application entry point.</summary>
public partial class App : Application
{
    private Window _window;

    public App()
    {
        StartupLogger.Log("App..ctor: InitializeComponent 开始");
        InitializeComponent();
        StartupLogger.Log("App..ctor: XAML 资源加载完成");

        // Global safety nets: never exit silently again.
        this.UnhandledException += OnXamlUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        StartupLogger.Log("App..ctor: 全局异常处理已注册");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            StartupLogger.Log("OnLaunched: 创建主窗口…");
            _window = new MainWindow();
            StartupLogger.Log("OnLaunched: 主窗口创建完成，准备激活");
            _window.Activate();
            StartupLogger.Log("OnLaunched: 主窗口已激活");
        }
        catch (Exception ex)
        {
            StartupLogger.Log("OnLaunched 失败: " + ex);
            Program.ShowFatal(ex, "主窗口初始化失败");
            throw;
        }
    }

    private void OnXamlUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        StartupLogger.Log("UI 线程未处理异常: " + e.Message + "\n" + e.Exception);
        Program.ShowFatal(e.Exception, "程序遇到未处理的异常");
        e.Handled = true; // keep the window alive where possible
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        StartupLogger.Log("AppDomain 未处理异常: " + e.ExceptionObject);
        Program.ShowFatal(e.ExceptionObject as Exception, "程序遇到致命错误");
    }

    private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
    {
        StartupLogger.Log("未观察的任务异常: " + e.Exception);
        e.SetObserved();
    }
}
