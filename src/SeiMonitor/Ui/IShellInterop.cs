using System;

namespace SeiMonitor.Ui;

/// <summary>
/// Abstração para operações nativas do Windows Shell (Progman, Taskbar, Z-order, Estilos de Janela).
/// Permite isolamento completo e testes herméticos sem dependência de HWNDs do sistema operacional.
/// </summary>
public interface IShellInterop
{
    IntPtr FindProgmanWindow();
    IntPtr FindTrayWindow();
    IntPtr GetWindowOwner(IntPtr hwnd);
    bool ApplyToolWindowStyles(IntPtr hwnd);
    bool SetWindowOwner(IntPtr hwnd, IntPtr ownerHwnd);
    bool SendToBottom(IntPtr hwnd);
    bool SendToTopmost(IntPtr hwnd);
    bool GetWindowRect(IntPtr hwnd, out Win32Taskbar.RECT rect);
    TaskbarInfo GetTaskbarInfo();
    bool EmbedInTaskbar(IntPtr hwnd, int physicalWidth, out int heightPhys) { heightPhys = 0; return false; }
    void RestoreTaskbarLayout() { }
}
