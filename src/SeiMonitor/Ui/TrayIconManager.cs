using System;
using System.IO;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using WinDrawing = System.Drawing;

namespace SeiMonitor.Ui;

/// <summary>
/// Ações expostas pelo menu de contexto e interações da bandeja do sistema.
/// </summary>
public sealed class TrayIconCallbacks
{
    public Action? OpenMainWindow { get; init; }
    public Action? TriggerSync { get; init; }
    public Action? ToggleDesktopFence { get; init; }
    public Action? ToggleTaskbarWidget { get; init; }
    public Action? OpenSettings { get; init; }
    public Action? ExitApplication { get; init; }
}

/// <summary>
/// Gerencia o NotifyIcon nativo do Windows Forms encapsulado com segurança em ambiente WPF.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly TrayIconCallbacks _callbacks;
    private readonly IUiDispatcher _dispatcher;
    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ContextMenuStrip? _contextMenu;
    private bool _disposed;

    public bool IsVisible => _notifyIcon?.Visible ?? false;

    public TrayIconManager(TrayIconCallbacks callbacks, Dispatcher dispatcher)
        : this(callbacks, new WpfDispatcher(dispatcher))
    {
    }

    public TrayIconManager(TrayIconCallbacks callbacks, IUiDispatcher dispatcher)
    {
        _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>
    /// Inicializa o ícone e menu de contexto na bandeja do sistema.
    /// </summary>
    public void Initialize()
    {
        if (_notifyIcon != null) return;

        _contextMenu = CreateContextMenu();

        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "SEI Monitor Desktop",
            Icon = LoadAppIcon(),
            ContextMenuStrip = _contextMenu,
            Visible = true
        };

        // Duplo clique restaura e foca a janela principal
        _notifyIcon.DoubleClick += (s, e) =>
        {
            _dispatcher.BeginInvoke(() => _callbacks.OpenMainWindow?.Invoke());
        };
    }

    private WinForms.ContextMenuStrip CreateContextMenu()
    {
        var menu = new WinForms.ContextMenuStrip();

        // 1. Abrir Painel Principal (Negrito / Default)
        var itemOpen = new WinForms.ToolStripMenuItem("Abrir Painel Principal", null, (s, e) =>
        {
            _dispatcher.BeginInvoke(() => _callbacks.OpenMainWindow?.Invoke());
        })
        {
            Font = new WinDrawing.Font(menu.Font, WinDrawing.FontStyle.Bold)
        };
        menu.Items.Add(itemOpen);

        // 2. Sincronizar Agora
        var itemSync = new WinForms.ToolStripMenuItem("Sincronizar Agora", null, (s, e) =>
        {
            _dispatcher.BeginInvoke(() => _callbacks.TriggerSync?.Invoke());
        });
        menu.Items.Add(itemSync);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        // 3. Alternar Desktop Fence
        var itemFence = new WinForms.ToolStripMenuItem("Alternar Desktop Fence", null, (s, e) =>
        {
            _dispatcher.BeginInvoke(() => _callbacks.ToggleDesktopFence?.Invoke());
        });
        menu.Items.Add(itemFence);

        // 4. Alternar Widget Barra
        var itemTaskbar = new WinForms.ToolStripMenuItem("Alternar Widget Barra", null, (s, e) =>
        {
            _dispatcher.BeginInvoke(() => _callbacks.ToggleTaskbarWidget?.Invoke());
        });
        menu.Items.Add(itemTaskbar);

        // 5. Configurações
        var itemSettings = new WinForms.ToolStripMenuItem("Configurações", null, (s, e) =>
        {
            _dispatcher.BeginInvoke(() => _callbacks.OpenSettings?.Invoke());
        });
        menu.Items.Add(itemSettings);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        // 6. Sair
        var itemExit = new WinForms.ToolStripMenuItem("Sair", null, (s, e) =>
        {
            _dispatcher.BeginInvoke(() => _callbacks.ExitApplication?.Invoke());
        });
        menu.Items.Add(itemExit);

        return menu;
    }

    private static WinDrawing.Icon LoadAppIcon()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                var extracted = WinDrawing.Icon.ExtractAssociatedIcon(exePath);
                if (extracted != null) return extracted;
            }
        }
        catch
        {
            // Silencia e recorre ao fallback
        }

        return WinDrawing.SystemIcons.Application;
    }

    /// <summary>
    /// Exibe uma notificação balão na bandeja.
    /// </summary>
    public void ShowBalloon(string title, string text, WinForms.ToolTipIcon icon = WinForms.ToolTipIcon.Info, int timeoutMs = 3000)
    {
        _notifyIcon?.ShowBalloonTip(timeoutMs, title, text, icon);
    }

    /// <summary>
    /// Atualiza o texto do tooltip da bandeja (máximo 63 caracteres no Win32 clássico / 127 no Windows 10/11).
    /// </summary>
    public void UpdateTooltip(string text)
    {
        if (_notifyIcon != null && !string.IsNullOrWhiteSpace(text))
        {
            _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_notifyIcon != null)
        {
            // Essencial: ocultar antes do dispose para evitar ícone fantasma na barra de tarefas
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        if (_contextMenu != null)
        {
            _contextMenu.Dispose();
            _contextMenu = null;
        }
    }
}
