using MemoryKeeper.Contracts;

namespace MemoryKeeper.Core.Services;

/// <summary>
/// V1 手动游戏检测：由用户开关驱动。
/// 后续可替换为全屏检测/启动器检测实现，接口不变。
/// </summary>
public sealed class ManualGameDetector : IGameDetector
{
    public bool IsGameRunning { get; private set; }

    public event EventHandler<bool>? GameStateChanged;

    public void SetGameRunning(bool running)
    {
        if (IsGameRunning == running) return;
        IsGameRunning = running;
        GameStateChanged?.Invoke(this, running);
    }
}
