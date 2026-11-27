using System.Threading;

namespace PhoneDesktop.Core;

/// <summary>
/// 单实例 + 进程间“优雅退出”信号。
/// 第二个实例带 --exit 启动时，通过命名事件通知第一个实例退出，
/// 用于验收脚本与开发调试（不是面向用户的退出入口）。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\PhoneDesktop.Instance.v1";
    private const string ExitEventName = @"Local\PhoneDesktop.Exit.v1";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _exitEvent;
    private readonly Thread? _waitThread;
    private volatile bool _stopping;
    private bool _disposed;

    public bool IsFirstInstance { get; }

    /// <summary>在后台线程触发，订阅方需要自行切回 UI 线程。</summary>
    public event Action? ExitRequested;

    public SingleInstance()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            IsFirstInstance = createdNew;
        }
        catch
        {
            IsFirstInstance = true;
        }

        try
        {
            _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        }
        catch
        {
            _exitEvent = null;
        }

        if (_exitEvent is not null)
        {
            _waitThread = new Thread(WaitLoop)
            {
                IsBackground = true,
                Name = "PhoneDesktop.ExitSignal",
            };
            _waitThread.Start();
        }
    }

    public static bool TrySignalExit()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ExitEventName);
            handle.Set();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void WaitLoop()
    {
        var handle = _exitEvent;
        if (handle is null)
        {
            return;
        }

        while (!_stopping)
        {
            try
            {
                if (handle.WaitOne(500))
                {
                    ExitRequested?.Invoke();
                }
            }
            catch
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;

        try
        {
            _exitEvent?.Set();
        }
        catch
        {
            // 忽略
        }

        _waitThread?.Join(500);

        try
        {
            _exitEvent?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            if (IsFirstInstance)
            {
                _mutex?.ReleaseMutex();
            }

            _mutex?.Dispose();
        }
        catch
        {
            // 忽略
        }
    }
}
