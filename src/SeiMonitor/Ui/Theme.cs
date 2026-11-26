using System;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace SeiMonitor.Ui;

/// <summary>
/// Sistema de temas e tokens visuais do SEI Monitor (Fluent Dark/Light + DWM Accent).
/// Suporta injeção dinâmica de recursos em Application.Current.Resources ou dicionários específicos.
/// </summary>
public static class Theme
{
    private static Color? _cachedAccent;

    public static Color Accent => _cachedAccent ??= ReadAccent();
    public static string AccentHex => $"#{Accent.R:x2}{Accent.G:x2}{Accent.B:x2}";

    public static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    [SupportedOSPlatform("windows")]
    public static Color ReadAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int a)
            {
                return Color.FromRgb((byte)(a & 0xFF), (byte)((a >> 8) & 0xFF), (byte)((a >> 16) & 0xFF));
            }
            if (key?.GetValue("ColorizationColor") is int c)
            {
                return Color.FromRgb((byte)((c >> 16) & 0xFF), (byte)((c >> 8) & 0xFF), (byte)(c & 0xFF));
            }
        }
        catch
        {
            // Fallback seguro em ambientes sem DWM / registro restrito
        }
        return Color.FromRgb(0x00, 0x78, 0xD4); // Azul padrão Fluent
    }

    public static void Apply(ResourceDictionary res, string themeMode = "Dark")
    {
        ArgumentNullException.ThrowIfNull(res);

        bool isDark = !string.Equals(themeMode, "Light", StringComparison.OrdinalIgnoreCase);

        // Backgrounds Fluent
        res["Bg0"] = isDark ? Brush("#09090b") : Brush("#f8fafc"); // Canvas / base
        res["Bg1"] = isDark ? Brush("#18181b") : Brush("#ffffff"); // Cartão / Superfície
        res["Bg2"] = isDark ? Brush("#202024") : Brush("#f1f5f9"); // Header / Barra superior
        res["Bg3"] = isDark ? Brush("#27272a") : Brush("#e2e8f0"); // Inputs / Hover / Pills
        res["OverlaySurface"] = isDark ? Brush("#E618181b") : Brush("#E6ffffff");

        // Foregrounds / Tipografia
        res["Fg"] = isDark ? Brush("#f4f4f5") : Brush("#0f172a");       // Texto primário
        res["FgMuted"] = isDark ? Brush("#a1a1aa") : Brush("#475569");  // Texto secundário
        res["FgDim"] = isDark ? Brush("#71717a") : Brush("#94a3b8");    // Texto terciário / placeholder

        // Bordas e Divisores
        res["BorderBrush"] = isDark ? Brush("#27272a") : Brush("#cbd5e1");
        res["BorderSubtle"] = isDark ? Brush("#1f1f23") : Brush("#e2e8f0");

        // DWM Accent
        res["AccentBrush"] = new SolidColorBrush(Accent);
        res["AccentHoverBrush"] = new SolidColorBrush(Color.FromRgb(
            (byte)(Accent.R * 0.85), (byte)(Accent.G * 0.85), (byte)(Accent.B * 0.85)));

        // Badges e Pincéis Semânticos de Negócio do SEI
        res["PillDestinedBrush"] = Brush("#10b981");    // 🎯 Para Você (Verde Esmeralda)
        res["PillAttentionBrush"] = Brush("#f59e0b");   // ⚠️ Aberto por Terceiro (Âmbar)
        res["PillNewDocsBrush"] = Brush("#3b82f6");     // ⚡ Novos Andamentos (Azul Elétrico)
        res["PillMonitoredBrush"] = Brush("#8b5cf6");   // 📂 Monitorados (Roxo / Especial)
        res["UrgentBrush"] = Brush("#ef4444");          // 🚨 Urgência Máxima (Vermelho)
    }
}
