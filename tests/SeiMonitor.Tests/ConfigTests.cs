using System.Runtime.Versioning;
using SeiMonitor.Core;
using Xunit;

namespace SeiMonitor.Tests;

[SupportedOSPlatform("windows")]
public class ConfigTests : IDisposable
{
    private readonly Database _db;
    private readonly Config _config;

    public ConfigTests()
    {
        _db = new Database(":memory:");
        _config = new Config(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public void DefaultValues_OnCleanDatabase_MatchExpectedDefaults()
    {
        Assert.Equal(5, _config.CheckIntervalMinutes);
        Assert.Equal(120, _config.SeiTimeoutSeconds);
        Assert.True(_config.DesktopFenceEnabled);
        Assert.Equal(0.92, _config.FenceAlpha, 2);
        Assert.Equal(380, _config.FenceW);
        Assert.Equal(600, _config.FenceH);
        Assert.False(_config.FenceCollapsed);
        Assert.True(_config.TaskbarWidgetEnabled);
        Assert.False(_config.StartMinimized);
        Assert.True(_config.CloseToTray);
        Assert.True(_config.MinimizeToTray);
        Assert.Equal("https://sei.pi.gov.br/api/v2", _config.SeiWsseiBaseUrl);
        Assert.Equal("https://api.sei.pi.gov.br", _config.SeiSeadBaseUrl);
        Assert.Equal("PGE-PI", _config.SeiSeadOrgao);
        Assert.Equal("GINFO", _config.SeiUnidadeSigla);
        Assert.Null(_config.FenceX);
        Assert.Null(_config.FenceY);
        Assert.Empty(_config.UserKeywords);
    }

    [Fact]
    public void SeiWsseiUsuario_TrimsExistingSetting()
    {
        _db.SetSetting("sei_wssei_usuario", " usuario.teste\t");

        Assert.Equal("usuario.teste", _config.SeiWsseiUsuario);
    }

    [Fact]
    public void SettingValues_FiresEvents_AndPersistsToSQLite()
    {
        string? changedSetting = null;
        string? changedProperty = null;

        _config.SettingChanged += name => changedSetting = name;
        _config.PropertyChanged += (_, e) => changedProperty = e.PropertyName;

        _config.CheckIntervalMinutes = 15;

        Assert.Equal("CheckIntervalMinutes", changedSetting);
        Assert.Equal("CheckIntervalMinutes", changedProperty);
        Assert.Equal(15, _config.CheckIntervalMinutes);
        Assert.Equal("15", _db.GetSetting("check_interval_minutes"));
    }

    [Fact]
    public void Clamping_EnforcesValidRanges()
    {
        // Intervalo mínimo é 1 minuto
        _config.CheckIntervalMinutes = -5;
        Assert.Equal(1, _config.CheckIntervalMinutes);

        // Alpha entre 0.20 e 1.00
        _config.FenceAlpha = 0.05;
        Assert.Equal(0.20, _config.FenceAlpha, 2);

        _config.FenceAlpha = 1.85;
        Assert.Equal(1.00, _config.FenceAlpha, 2);

        // Largura entre 280 e 800
        _config.FenceW = 100;
        Assert.Equal(280, _config.FenceW);

        _config.FenceW = 2000;
        Assert.Equal(800, _config.FenceW);

        // Altura entre 200 e 1200
        _config.FenceH = 50;
        Assert.Equal(200, _config.FenceH);

        _config.FenceH = 3000;
        Assert.Equal(1200, _config.FenceH);
    }

    [Fact]
    public void UserKeywords_ParsesAndDeduplicates_Correctly()
    {
        _config.UserKeywordsRaw = "almir, GINFO; joao ; almir, SILVA,  ";

        var keywords = _config.UserKeywords;
        Assert.Equal(4, keywords.Count);
        Assert.Equal("almir", keywords[0]);
        Assert.Equal("GINFO", keywords[1]);
        Assert.Equal("joao", keywords[2]);
        Assert.Equal("SILVA", keywords[3]);
    }

    [Fact]
    public void FenceCoordinates_NullableHandling()
    {
        Assert.Null(_config.FenceX);
        Assert.Null(_config.FenceY);

        _config.FenceX = 1920;
        _config.FenceY = 1080;

        Assert.Equal(1920, _config.FenceX);
        Assert.Equal(1080, _config.FenceY);

        _config.FenceX = null;
        _config.FenceY = null;

        Assert.Null(_config.FenceX);
        Assert.Null(_config.FenceY);
    }

    [Fact]
    public void ConstitutionRule_NoPasswordStoredInConfigOrSettings()
    {
        // Assegurar que nenhuma propriedade no Config expõe senhas ou tokens
        var props = typeof(Config).GetProperties();
        foreach (var p in props)
        {
            var name = p.Name.ToLowerInvariant();
            Assert.DoesNotContain("password", name);
            Assert.DoesNotContain("senha", name);
            Assert.DoesNotContain("token", name);
            Assert.DoesNotContain("secret", name);
        }

        // Assegurar que a tabela settings do SQLite não possui senhas
        _config.SeiWsseiUsuario = "usuario.teste";
        _config.SeiSeadUsuario = "usuario.teste";

        var allSettings = _db.GetAllSettings();
        foreach (var key in allSettings.Keys)
        {
            var k = key.ToLowerInvariant();
            Assert.DoesNotContain("password", k);
            Assert.DoesNotContain("senha", k);
            Assert.DoesNotContain("token", k);
        }
    }
}
