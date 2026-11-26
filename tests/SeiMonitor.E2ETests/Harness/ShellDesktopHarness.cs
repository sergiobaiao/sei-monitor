using System;
using System.Collections.Generic;
using System.Threading;

namespace SeiMonitor.E2ETests.Harness;

public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right
}

public record TaskbarBounds(
    TaskbarEdge Edge,
    int Left,
    int Top,
    int Right,
    int Bottom
);

public class ShellDesktopHarness
{
    // Win32 Constants as per PROJECT.md & constitution §IV
    public const int GWLP_HWNDPARENT = -8;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;
    public const int HWND_BOTTOM = 1;

    public const string DefaultMutexName = @"Local\SEI_Monitor_Desktop";
    public const string DefaultShowEventName = @"Local\SEI_Monitor_Show";

    public static bool VerifyWindowStyleContracts(long windowExStyles)
    {
        bool hasToolWindow = (windowExStyles & WS_EX_TOOLWINDOW) != 0;
        bool hasNoActivate = (windowExStyles & WS_EX_NOACTIVATE) != 0;
        return hasToolWindow && hasNoActivate;
    }

    public static (int X, int Y) CalculateTaskbarWidgetPosition(TaskbarBounds taskbar, int widgetWidth, int widgetHeight, int trayNotifyWidth = 180)
    {
        // Positioned immediately to the left of the system notification tray area
        return taskbar.Edge switch
        {
            TaskbarEdge.Bottom => (taskbar.Right - trayNotifyWidth - widgetWidth - 10, taskbar.Top + (taskbar.Bottom - taskbar.Top - widgetHeight) / 2),
            TaskbarEdge.Top => (taskbar.Right - trayNotifyWidth - widgetWidth - 10, taskbar.Top + (taskbar.Bottom - taskbar.Top - widgetHeight) / 2),
            TaskbarEdge.Left => (taskbar.Left + 5, taskbar.Bottom - trayNotifyWidth - widgetHeight - 10),
            TaskbarEdge.Right => (taskbar.Left + 5, taskbar.Bottom - trayNotifyWidth - widgetHeight - 10),
            _ => (taskbar.Right - trayNotifyWidth - widgetWidth, taskbar.Top)
        };
    }

    public static (int X, int Y) CalculateFlyoutPosition(int widgetX, int widgetY, int flyoutWidth, int flyoutHeight, TaskbarBounds taskbar)
    {
        // Anchored right above the taskbar widget
        if (taskbar.Edge == TaskbarEdge.Bottom)
        {
            int x = widgetX;
            int y = taskbar.Top - flyoutHeight - 8;
            return (Math.Max(10, x), Math.Max(10, y));
        }
        else if (taskbar.Edge == TaskbarEdge.Top)
        {
            int x = widgetX;
            int y = taskbar.Bottom + 8;
            return (Math.Max(10, x), y);
        }
        return (widgetX, widgetY - flyoutHeight);
    }
}

public class HorizontalTickerSimulator
{
    private readonly Queue<string> _messages = new();
    public string? CurrentMessage { get; private set; }
    public double ScrollOffset { get; private set; }
    public bool IsPaused { get; private set; }
    public double ScrollSpeedPxPerSec { get; set; } = 40.0;
    public double ViewportWidth { get; set; } = 280.0;
    public double MessageTextEstimatedWidth { get; private set; } = 300.0;

    public void PushMessage(string message)
    {
        _messages.Enqueue(message);
        if (CurrentMessage == null)
        {
            AdvanceToNextMessage();
        }
    }

    public void Pause()
    {
        IsPaused = true;
    }

    public void Resume()
    {
        IsPaused = false;
    }

    public void AdvanceTime(double seconds)
    {
        if (IsPaused || CurrentMessage == null) return;

        ScrollOffset += ScrollSpeedPxPerSec * seconds;

        // When completely scrolled out of viewport, advance to next message
        if (ScrollOffset > (ViewportWidth + MessageTextEstimatedWidth))
        {
            AdvanceToNextMessage();
        }
    }

    private void AdvanceToNextMessage()
    {
        if (_messages.Count > 0)
        {
            CurrentMessage = _messages.Dequeue();
            ScrollOffset = 0;
            MessageTextEstimatedWidth = CurrentMessage.Length * 7.5; // Estimated character width in pixels
        }
        else
        {
            CurrentMessage = null;
            ScrollOffset = 0;
        }
    }

    public int QueuedCount => _messages.Count;
}

public class ExplorerWatchdogSimulator
{
    public int WatchdogIntervalMs => 1000;
    public bool IsRunning { get; private set; }
    public int ReanchorCount { get; private set; }
    public IntPtr CurrentProgmanHwnd { get; private set; } = new IntPtr(0x1001);

    public void Start() => IsRunning = true;
    public void Stop() => IsRunning = false;

    public void SimulateExplorerRestart(IntPtr newProgmanHwnd)
    {
        if (!IsRunning) return;
        if (newProgmanHwnd != CurrentProgmanHwnd)
        {
            CurrentProgmanHwnd = newProgmanHwnd;
            ReanchorCount++;
        }
    }
}
