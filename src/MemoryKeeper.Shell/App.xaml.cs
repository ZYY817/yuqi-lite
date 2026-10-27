using System.Windows;
using MemoryKeeper.Contracts;
using MemoryKeeper.Core;
using MemoryKeeper.Modules.Cleaner;
using MemoryKeeper.Modules.Whitelist;
using MemoryKeeper.Modules.Disabled;
using Microsoft.Extensions.DependencyInjection;

namespace MemoryKeeper.Shell;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, "MemoryKeeper_SingleInstance_Mutex", out var createdNew);
        if (!createdNew)
        {
            // 另一实例可能正在让位（如提权重启）：短暂等待互斥锁释放后接管；
            // 若是用户重复启动则超时后正常退出
            try { createdNew = _singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { createdNew = true; }
            catch { }
            if (!createdNew)
            {
                Shutdown();
                return;
            }
        }

        base.OnStartup(e);

        var services = new ServiceCollection();
        services.AddMemoryKeeperCore();

        // 模块注册（V1 显式注册；后续可换目录扫描式插件加载）
        services.AddSingleton<IModule, CleanerModule>();
        services.AddSingleton<IModule, WhitelistModule>();
        services.AddSingleton<IModule, DisabledModule>();

        _services = services.BuildServiceProvider();

        var window = new MainWindow(
            _services.GetServices<IModule>().OrderBy(m => m.Order),
            _services.GetRequiredService<IEventBus>(),
            _services.GetRequiredService<ISettingsStore>(),
            _services.GetRequiredService<IModeEngine>());
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
