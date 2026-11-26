using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace SeiMonitor.Ui;

/// <summary>
/// Orientação da barra de tarefas do Windows.
/// </summary>
public enum TaskbarEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
    Unknown = 4
}

/// <summary>
/// Informações completas de posicionamento e geometria da barra de tarefas do Windows.
/// </summary>
public sealed record TaskbarInfo(
    TaskbarEdge Edge,
    Win32Taskbar.RECT Bounds,
    Win32Taskbar.RECT TrayBounds,
    bool HasTray,
    bool IsAutoHide
);

/// <summary>
/// Utilitário nativo de P/Invoke para integração e ancoragem na Barra de Tarefas e Shell do Windows.
/// Compatível com Windows 10 e Windows 11 (24H2+), multi-monitor e DPI PerMonitorV2.
/// </summary>
public static class Win32Taskbar
{
    public const uint ABM_GETSTATE = 0x00000004;
    public const uint ABM_GETTASKBARPOS = 0x00000005;

    public const uint ABE_LEFT = 0;
    public const uint ABE_TOP = 1;
    public const uint ABE_RIGHT = 2;
    public const uint ABE_BOTTOM = 3;

    public const int ABS_AUTOHIDE = 0x0000001;

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int GWLP_HWNDPARENT = -8;
    public const int GW_OWNER = 4;

    public const long WS_CHILD = 0x40000000L;
    public const long WS_POPUP = 0x80000000L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_APPWINDOW = 0x00040000L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;

    public static readonly IntPtr HWND_BOTTOM = new(1);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public RECT(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    /// <summary>
    /// Consulta os dados geométricos e o estado atual da barra de tarefas primária do Windows.
    /// </summary>
    public static TaskbarInfo GetTaskbarInfo()
    {
        var data = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>()
        };

        var result = SHAppBarMessage(ABM_GETTASKBARPOS, ref data);
        var stateData = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        var state = SHAppBarMessage(ABM_GETSTATE, ref stateData);
        bool isAutoHide = ((uint)state.ToUInt64() & ABS_AUTOHIDE) != 0;

        IntPtr shellTray = FindWindowW("Shell_TrayWnd", null);
        RECT taskbarRect = data.rc;

        if (shellTray != IntPtr.Zero && (taskbarRect.Width <= 0 || taskbarRect.Height <= 0))
        {
            GetWindowRect(shellTray, out taskbarRect);
        }

        IntPtr trayNotify = IntPtr.Zero;
        RECT trayRect = default;
        bool hasTray = false;

        if (shellTray != IntPtr.Zero)
        {
            trayNotify = FindWindowExW(shellTray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (trayNotify != IntPtr.Zero && GetWindowRect(trayNotify, out trayRect))
            {
                hasTray = trayRect.Width > 0 && trayRect.Height > 0;
            }
        }

        TaskbarEdge edge = data.uEdge switch
        {
            ABE_LEFT => TaskbarEdge.Left,
            ABE_TOP => TaskbarEdge.Top,
            ABE_RIGHT => TaskbarEdge.Right,
            ABE_BOTTOM => TaskbarEdge.Bottom,
            _ => TaskbarEdge.Bottom
        };

        return new TaskbarInfo(edge, taskbarRect, trayRect, hasTray, isAutoHide);
    }

    /// <summary>
    /// Calcula a posição (Left, Top) em DIPs para posicionar o TaskbarWidgetWindow.
    /// Função pura desacoplada de HWND para viabilizar testes unitários em CI.
    /// </summary>
    public static (double Left, double Top) CalculateWidgetPosition(
        TaskbarInfo info,
        double widgetWidthDip,
        double widgetHeightDip,
        double dpiScaleX,
        double dpiScaleY,
        Rect workAreaDip)
    {
        if (dpiScaleX <= 0) dpiScaleX = 1.0;
        if (dpiScaleY <= 0) dpiScaleY = 1.0;

        double physicalWidgetW = widgetWidthDip * dpiScaleX;
        double physicalWidgetH = widgetHeightDip * dpiScaleY;

        double targetPhysX;
        double targetPhysY;

        switch (info.Edge)
        {
            case TaskbarEdge.Top:
                targetPhysX = info.HasTray
                    ? info.TrayBounds.Left - physicalWidgetW - (12 * dpiScaleX)
                    : info.Bounds.Right - physicalWidgetW - (180 * dpiScaleX);
                targetPhysY = info.Bounds.Top + (info.Bounds.Height - physicalWidgetH) / 2.0;
                break;

            case TaskbarEdge.Left:
                targetPhysX = info.Bounds.Left + (info.Bounds.Width - physicalWidgetW) / 2.0;
                targetPhysY = info.Bounds.Bottom - physicalWidgetH - (100 * dpiScaleY);
                break;

            case TaskbarEdge.Right:
                targetPhysX = info.Bounds.Left + (info.Bounds.Width - physicalWidgetW) / 2.0;
                targetPhysY = info.Bounds.Bottom - physicalWidgetH - (100 * dpiScaleY);
                break;

            case TaskbarEdge.Bottom:
            default:
                targetPhysX = info.HasTray
                    ? info.TrayBounds.Left - physicalWidgetW - (12 * dpiScaleX)
                    : info.Bounds.Right - physicalWidgetW - (180 * dpiScaleX);
                targetPhysY = info.Bounds.Top + (info.Bounds.Height - physicalWidgetH) / 2.0;
                break;
        }

        // Conversão física -> DIPs
        double dipLeft = targetPhysX / dpiScaleX;
        double dipTop = targetPhysY / dpiScaleY;

        // Limita dentro da área útil de trabalho visível se workArea for válida
        if (workAreaDip.Width > 0 && workAreaDip.Height > 0)
        {
            if (dipLeft + widgetWidthDip > workAreaDip.Right)
                dipLeft = workAreaDip.Right - widgetWidthDip - 4;
            if (dipLeft < workAreaDip.Left)
                dipLeft = workAreaDip.Left + 4;
        }

        return (dipLeft, dipTop);
    }

    /// <summary>
    /// Calcula a posição (Left, Top) em DIPs para posicionar o TaskbarFlyoutWindow ancorado à barra/widget.
    /// </summary>
    public static (double Left, double Top) CalculateFlyoutPosition(
        TaskbarInfo info,
        double widgetLeftDip,
        double widgetTopDip,
        double widgetWidthDip,
        double widgetHeightDip,
        double flyoutWidthDip,
        double flyoutHeightDip,
        Rect workAreaDip)
    {
        double targetLeft;
        double targetTop;

        switch (info.Edge)
        {
            case TaskbarEdge.Top:
                targetTop = widgetTopDip + widgetHeightDip + 8;
                targetLeft = widgetLeftDip + widgetWidthDip - flyoutWidthDip;
                break;

            case TaskbarEdge.Left:
                targetLeft = widgetLeftDip + widgetWidthDip + 8;
                targetTop = widgetTopDip + (widgetHeightDip - flyoutHeightDip) / 2;
                break;

            case TaskbarEdge.Right:
                targetLeft = widgetLeftDip - flyoutWidthDip - 8;
                targetTop = widgetTopDip + (widgetHeightDip - flyoutHeightDip) / 2;
                break;

            case TaskbarEdge.Bottom:
            default:
                targetTop = widgetTopDip - flyoutHeightDip - 8;
                targetLeft = widgetLeftDip + widgetWidthDip - flyoutWidthDip;
                break;
        }

        // Clamp na WorkArea se válida
        if (workAreaDip.Width > 0 && workAreaDip.Height > 0)
        {
            if (targetLeft + flyoutWidthDip > workAreaDip.Right - 8)
                targetLeft = workAreaDip.Right - flyoutWidthDip - 8;
            if (targetLeft < workAreaDip.Left + 8)
                targetLeft = workAreaDip.Left + 8;

            if (targetTop + flyoutHeightDip > workAreaDip.Bottom - 8)
                targetTop = workAreaDip.Bottom - flyoutHeightDip - 8;
            if (targetTop < workAreaDip.Top + 8)
                targetTop = workAreaDip.Top + 8;
        }

        return (targetLeft, targetTop);
    }

    /// <summary>
    /// Embute a janela diretamente dentro da Shell_TrayWnd (WS_CHILD) e encolhe a área
    /// de botões de aplicativos (MSTaskSwWClass) para que o widget fique nativamente acoplado
    /// à barra de tarefas sem sobrepor outros ícones.
    /// </summary>
    public static bool EmbedChildInTaskbar(IntPtr widgetHwnd, int physicalWidth, out int heightPhys)
    {
        heightPhys = 0;
        if (widgetHwnd == IntPtr.Zero || physicalWidth <= 0) return false;

        IntPtr shellTray = FindWindowW("Shell_TrayWnd", null);
        if (shellTray == IntPtr.Zero) return false;

        if (!GetWindowRect(shellTray, out var trayRect) || trayRect.Width <= 0 || trayRect.Height <= 0)
            return false;

        IntPtr rebar = FindWindowExW(shellTray, IntPtr.Zero, "ReBarWindow32", null);
        if (rebar == IntPtr.Zero || !GetWindowRect(rebar, out var rebarRect) || rebarRect.Width <= physicalWidth + 60)
            return false;

        int widgetScreenLeft = rebarRect.Right - physicalWidth;
        int widgetScreenTop = rebarRect.Top;
        heightPhys = rebarRect.Height > 0 ? rebarRect.Height : trayRect.Height;

        IntPtr msTaskSw = FindWindowExW(rebar, IntPtr.Zero, "MSTaskSwWClass", null);
        if (msTaskSw != IntPtr.Zero && GetWindowRect(msTaskSw, out var msTaskRect))
        {
            int desiredWidth = (widgetScreenLeft - 2) - msTaskRect.Left;
            if (desiredWidth > 60 && Math.Abs(msTaskRect.Width - desiredWidth) > 2)
            {
                SetWindowPos(
                    msTaskSw,
                    IntPtr.Zero,
                    msTaskRect.Left - rebarRect.Left,
                    msTaskRect.Top - rebarRect.Top,
                    desiredWidth,
                    msTaskRect.Height,
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }

        if (GetParent(widgetHwnd) != shellTray)
        {
            long style = (long)GetWindowLongPtr(widgetHwnd, GWL_STYLE);
            style = (style & ~WS_POPUP) | WS_CHILD;
            SetWindowLongPtr(widgetHwnd, GWL_STYLE, (IntPtr)style);
            SetParent(widgetHwnd, shellTray);
        }

        int relLeft = widgetScreenLeft - trayRect.Left;
        int relTop = widgetScreenTop - trayRect.Top;

        SetWindowPos(
            widgetHwnd,
            IntPtr.Zero,
            relLeft,
            relTop,
            physicalWidth,
            heightPhys,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        return true;
    }

    /// <summary>
    /// Restaura a largura original da área de botões da barra de tarefas ao fechar o widget.
    /// </summary>
    public static void RestoreTaskbarLayout()
    {
        IntPtr shellTray = FindWindowW("Shell_TrayWnd", null);
        if (shellTray == IntPtr.Zero) return;

        IntPtr rebar = FindWindowExW(shellTray, IntPtr.Zero, "ReBarWindow32", null);
        if (rebar == IntPtr.Zero || !GetWindowRect(rebar, out var rebarRect)) return;

        IntPtr msTaskSw = FindWindowExW(rebar, IntPtr.Zero, "MSTaskSwWClass", null);
        if (msTaskSw != IntPtr.Zero && GetWindowRect(msTaskSw, out var msTaskRect))
        {
            int fullWidth = rebarRect.Right - msTaskRect.Left;
            if (fullWidth > msTaskRect.Width)
            {
                SetWindowPos(
                    msTaskSw,
                    IntPtr.Zero,
                    msTaskRect.Left - rebarRect.Left,
                    msTaskRect.Top - rebarRect.Top,
                    fullWidth,
                    msTaskRect.Height,
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }
    }
}
