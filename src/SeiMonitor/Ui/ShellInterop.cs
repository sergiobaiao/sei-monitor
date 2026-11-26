using System;

namespace SeiMonitor.Ui;

/// <summary>
/// Implementação padrão de produção de IShellInterop conectada às chamadas Win32 nativas.
/// </summary>
public sealed class Win32ShellInterop : IShellInterop
{
    public static readonly Win32ShellInterop Instance = new();

    public IntPtr FindProgmanWindow() => Win32Taskbar.FindWindowW("Progman", null);

    public IntPtr FindTrayWindow() => Win32Taskbar.FindWindowW("Shell_TrayWnd", null);

    public IntPtr GetWindowOwner(IntPtr hwnd) => Win32Taskbar.GetWindow(hwnd, Win32Taskbar.GW_OWNER);

    public bool ApplyToolWindowStyles(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var exStyle = (long)Win32Taskbar.GetWindowLongPtr(hwnd, Win32Taskbar.GWL_EXSTYLE);
        var newStyle = (exStyle | Win32Taskbar.WS_EX_TOOLWINDOW | Win32Taskbar.WS_EX_NOACTIVATE) & ~Win32Taskbar.WS_EX_APPWINDOW;
        return Win32Taskbar.SetWindowLongPtr(hwnd, Win32Taskbar.GWL_EXSTYLE, (IntPtr)newStyle) != IntPtr.Zero;
    }

    public bool SetWindowOwner(IntPtr hwnd, IntPtr ownerHwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        return Win32Taskbar.SetWindowLongPtr(hwnd, Win32Taskbar.GWLP_HWNDPARENT, ownerHwnd) != IntPtr.Zero;
    }

    public bool SendToBottom(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        return Win32Taskbar.SetWindowPos(hwnd, Win32Taskbar.HWND_BOTTOM, 0, 0, 0, 0,
            Win32Taskbar.SWP_NOMOVE | Win32Taskbar.SWP_NOSIZE | Win32Taskbar.SWP_NOACTIVATE);
    }

    public bool SendToTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        return Win32Taskbar.SetWindowPos(hwnd, Win32Taskbar.HWND_TOPMOST, 0, 0, 0, 0,
            Win32Taskbar.SWP_NOMOVE | Win32Taskbar.SWP_NOSIZE | Win32Taskbar.SWP_NOACTIVATE);
    }

    public bool GetWindowRect(IntPtr hwnd, out Win32Taskbar.RECT rect)
    {
        return Win32Taskbar.GetWindowRect(hwnd, out rect);
    }

    public TaskbarInfo GetTaskbarInfo() => Win32Taskbar.GetTaskbarInfo();

    public bool EmbedInTaskbar(IntPtr hwnd, int physicalWidth, out int heightPhys)
        => Win32Taskbar.EmbedChildInTaskbar(hwnd, physicalWidth, out heightPhys);

    public void RestoreTaskbarLayout()
        => Win32Taskbar.RestoreTaskbarLayout();
}
