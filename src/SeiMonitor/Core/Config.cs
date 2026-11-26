using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SeiMonitor.Core;

/// <summary>
/// Modelo de configurações reativas do SEI Monitor sincronizado com a tabela 'settings' do SQLite.
/// Dispara eventos de alteração para atualização dinâmica de componentes da interface e do motor de polling.
/// </summary>
public sealed class Config : INotifyPropertyChanged
{
    private readonly Database _db;

    public Config(Database db)
    {
        _db = db;
    }

    public Database Db => _db;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string>? SettingChanged;

    private void OnChanged([CallerMemberName] string? propName = null)
    {
        if (propName == null) return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        SettingChanged?.Invoke(propName);
    }

    // --- Helpers de conversão ---
    private string Str(string key, string def) => _db.GetSetting(key, def);
    private void SetStr(string key, string val, [CallerMemberName] string? prop = null)
    {
        _db.SetSetting(key, val);
        OnChanged(prop);
    }

    private bool Bool(string key, bool def) =>
        Str(key, def ? "true" : "false").Equals("true", StringComparison.OrdinalIgnoreCase);

    private void SetBool(string key, bool val, [CallerMemberName] string? prop = null) =>
        SetStr(key, val ? "true" : "false", prop);

    private int Int(string key, int def) =>
        int.TryParse(_db.GetSetting(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;

    private void SetInt(string key, int val, [CallerMemberName] string? prop = null) =>
        SetStr(key, val.ToString(CultureInfo.InvariantCulture), prop);

    private double Dbl(string key, double def) =>
        double.TryParse(_db.GetSetting(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

    private void SetDbl(string key, double val, string format = "0.00", [CallerMemberName] string? prop = null) =>
        SetStr(key, val.ToString(format, CultureInfo.InvariantCulture), prop);

    public static List<string> SplitList(string raw) =>
        (raw ?? string.Empty)
           .Replace(';', ',')
           .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Distinct(StringComparer.OrdinalIgnoreCase)
           .ToList();

    // ==========================================
    // 1. Polling e Comunicação de Rede
    // ==========================================
    /// <summary>Intervalo de polling leve em minutos (mínimo 1 minuto, padrão 5 min).</summary>
    public int CheckIntervalMinutes
    {
        get => Math.Max(1, Int("check_interval_minutes", 5));
        set => SetInt("check_interval_minutes", Math.Max(1, value));
    }

    /// <summary>Timeout máximo em segundos por chamada HTTP ao SEI (padrão 120s).</summary>
    public int SeiTimeoutSeconds
    {
        get => Math.Max(10, Int("sei_timeout_seconds", 120));
        set => SetInt("sei_timeout_seconds", Math.Max(10, value));
    }

    // ==========================================
    // 2. Endpoints e Unidade do SEI
    // ==========================================
    public string SeiWsseiBaseUrl
    {
        get => Str("sei_wssei_base_url", "https://sei.pi.gov.br/api/v2");
        set => SetStr("sei_wssei_base_url", value.Trim().TrimEnd('/'));
    }

    public string SeiWsseiUsuario
    {
        get => Str("sei_wssei_usuario", "").Trim();
        set => SetStr("sei_wssei_usuario", value.Trim());
    }

    public string SeiWsseiOrgao
    {
        get => Str("sei_wssei_orgao", "3");
        set => SetStr("sei_wssei_orgao", value.Trim());
    }

    public string SeiSeadBaseUrl
    {
        get => Str("sei_sead_base_url", "https://api.sei.pi.gov.br");
        set => SetStr("sei_sead_base_url", value.Trim().TrimEnd('/'));
    }

    public string SeiSeadOrgao
    {
        get => Str("sei_sead_orgao", "PGE-PI");
        set => SetStr("sei_sead_orgao", value.Trim());
    }

    public string SeiSeadUsuario
    {
        get => Str("sei_sead_usuario", "");
        set => SetStr("sei_sead_usuario", value.Trim());
    }

    public string PrimaryApi
    {
        get => Str("sei_primary_api", "WSSEI") == "SEAD" ? "SEAD" : "WSSEI";
        set => SetStr("sei_primary_api", value == "SEAD" ? "SEAD" : "WSSEI");
    }

    /// <summary>ID numérico da unidade SEI de leitura e consulta (ex: 110000000).</summary>
    public string SeiUnidade
    {
        get => Str("sei_unidade", "");
        set => SetStr("sei_unidade", value.Trim());
    }

    /// <summary>Sigla da unidade para exibição amigável na interface (ex: GINFO).</summary>
    public string SeiUnidadeSigla
    {
        get => Str("sei_unidade_sigla", "GINFO");
        set => SetStr("sei_unidade_sigla", value.Trim());
    }

    // ==========================================
    // 3. Identidade do Usuário e Palavras-Chave
    // ==========================================
    /// <summary>Nome de usuário ou login utilizado para identificar visualizações de terceiros (AbertoPorTerceiro).</summary>
    public string SeiUsuarioNome
    {
        get => Str("sei_usuario_nome", "");
        set => SetStr("sei_usuario_nome", value.Trim());
    }

    /// <summary>String crua de palavras-chave separadas por vírgula ou ponto-e-vírgula.</summary>
    public string UserKeywordsRaw
    {
        get => Str("user_keywords", "");
        set => SetStr("user_keywords", value.Trim());
    }

    /// <summary>Lista estruturada de termos-chave do usuário para a heurística DestinadoAoUsuario.</summary>
    public List<string> UserKeywords => SplitList(UserKeywordsRaw);

    // ==========================================
    // 4. Widget Desktop Fences
    // ==========================================
    public bool DesktopFenceEnabled
    {
        get => Bool("desktop_fence_enabled", true);
        set => SetBool("desktop_fence_enabled", value);
    }

    /// <summary>Posição X em pixels físicos da tela.</summary>
    public int? FenceX
    {
        get => int.TryParse(_db.GetSetting("desktop_fence_x"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        set => SetStr("desktop_fence_x", value?.ToString(CultureInfo.InvariantCulture) ?? "");
    }

    /// <summary>Posição Y em pixels físicos da tela.</summary>
    public int? FenceY
    {
        get => int.TryParse(_db.GetSetting("desktop_fence_y"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        set => SetStr("desktop_fence_y", value?.ToString(CultureInfo.InvariantCulture) ?? "");
    }

    public int FenceW
    {
        get => Math.Clamp(Int("desktop_fence_w", 380), 280, 800);
        set => SetInt("desktop_fence_w", Math.Clamp(value, 280, 800));
    }

    public int FenceH
    {
        get => Math.Clamp(Int("desktop_fence_h", 600), 200, 1200);
        set => SetInt("desktop_fence_h", Math.Clamp(value, 200, 1200));
    }

    public double FenceAlpha
    {
        get => Math.Clamp(Dbl("desktop_fence_alpha", 0.92), 0.20, 1.00);
        set => SetDbl("desktop_fence_alpha", Math.Clamp(value, 0.20, 1.00));
    }

    public bool FenceCollapsed
    {
        get => Bool("desktop_fence_collapsed", false);
        set => SetBool("desktop_fence_collapsed", value);
    }

    public string FenceActiveGroup
    {
        get => Str("desktop_fence_active_group", "");
        set => SetStr("desktop_fence_active_group", value.Trim());
    }

    /// <summary>Alias para FenceX.</summary>
    public int? FenceLeft
    {
        get => FenceX;
        set { FenceX = value; OnChanged(); }
    }

    /// <summary>Alias para FenceY.</summary>
    public int? FenceTop
    {
        get => FenceY;
        set { FenceY = value; OnChanged(); }
    }

    /// <summary>Alias para FenceW.</summary>
    public int FenceWidth
    {
        get => FenceW;
        set { FenceW = value; OnChanged(); }
    }

    /// <summary>Alias para FenceH.</summary>
    public int FenceHeight
    {
        get => FenceH;
        set { FenceH = value; OnChanged(); }
    }

    /// <summary>Alias para FenceAlpha.</summary>
    public double FenceOpacity
    {
        get => FenceAlpha;
        set { FenceAlpha = value; OnChanged(); }
    }

    /// <summary>Alias para FenceActiveGroup.</summary>
    public string FencePinnedGroup
    {
        get => FenceActiveGroup;
        set { FenceActiveGroup = value; OnChanged(); }
    }

    /// <summary>Tema visual da aplicação ("Dark", "Light" ou "System"). Padrão: "Dark".</summary>
    public string Theme
    {
        get => Str("app_theme", "Dark");
        set => SetStr("app_theme", string.IsNullOrWhiteSpace(value) ? "Dark" : value.Trim());
    }

    public string FenceTheme
    {
        get => Str("fence_theme", "Dark");
        set => SetStr("fence_theme", value == "Light" ? "Light" : "Dark");
    }

    // ==========================================
    // 5. Taskbar Widget e Ciclo de Vida
    // ==========================================
    public bool TaskbarWidgetEnabled
    {
        get => Bool("taskbar_widget_enabled", true);
        set => SetBool("taskbar_widget_enabled", value);
    }

    public bool StartMinimized
    {
        get => Bool("start_minimized", false);
        set => SetBool("start_minimized", value);
    }

    public bool CloseToTray
    {
        get => Bool("close_to_tray", true);
        set => SetBool("close_to_tray", value);
    }

    public bool MinimizeToTray
    {
        get => Bool("minimize_to_tray", true);
        set => SetBool("minimize_to_tray", value);
    }

    /// <summary>
    /// Configuração de início com o Windows gravada diretamente no Registro (HKCU\Run).
    /// </summary>
    public static bool StartWithWindows
    {
        [SupportedOSPlatform("windows")]
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey);
                return key?.GetValue(RunRegistryValue) is string;
            }
            catch
            {
                return false;
            }
        }
        [SupportedOSPlatform("windows")]
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunRegistryKey);
                if (value)
                {
                    key?.SetValue(RunRegistryValue, $"\"{Environment.ProcessPath}\"");
                }
                else
                {
                    key?.DeleteValue(RunRegistryValue, throwOnMissingValue: false);
                }
            }
            catch
            {
                // Silencia eventuais falhas de política restritiva de registro
            }
        }
    }

    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunRegistryValue = "SEI Monitor Desktop";
}
