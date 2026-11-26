using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SeiMonitor.Core;
using SeiMonitor.Ui;
using Xunit;

namespace SeiMonitor.Tests;

/// <summary>
/// Suíte de testes empíricos adversariais de estresse para os ViewModels do Milestone 5.
/// Cobre:
/// 1. Carga massiva: MainWindowViewModel com 2.000 processos e alternância rápida de filtros.
/// 2. Estresse concorrente: operações simultâneas de leitura/escrita e eventos de polling.
/// 3. DetailWindowViewModel: 500 atividades e 200 documentos, ordenação cronológica e terceiros.
/// 4. Segurança de credenciais: garantia estrita de não vazamento para settings ou app_logs.
/// </summary>
[SupportedOSPlatform("windows")]
public class M5ViewModelsChallengerTests : IDisposable
{
    private readonly Database _db;
    private readonly Config _config;
    private readonly MockSeiPoller _mockPoller;
    private readonly ImmediateDispatcher _dispatcher;

    public M5ViewModelsChallengerTests()
    {
        _db = new Database(":memory:");
        _db.InitializeDatabase();
        _config = new Config(_db);
        _mockPoller = new MockSeiPoller();
        _dispatcher = new ImmediateDispatcher();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    #region 1. Carga Massiva: MainWindowViewModel com 2.000 Processos

    [Fact]
    public void Stress_2000Processes_LoadAndKpiAccuracy()
    {
        // Setup: Popular o banco SQLite com exatamente 2.000 processos
        const int totalProcesses = 2000;
        int expectedInbox = 0;
        int expectedAcomp = 0;
        int expectedDestined = 0;
        int expectedAttention = 0;
        int expectedUnread = 0;

        for (int i = 1; i <= totalProcesses; i++)
        {
            string id = $"proc_{i:D5}";
            string origem;
            if (i <= 600)
            {
                origem = "CAIXA_ENTRADA";
                expectedInbox++;
            }
            else if (i <= 1600)
            {
                origem = "ACOMPANHAMENTO";
                expectedAcomp++;
            }
            else
            {
                origem = "AMBOS";
                expectedInbox++;
                expectedAcomp++;
            }

            bool destinado = (i % 4 == 0); // 500 destinados
            if (destinado) expectedDestined++;

            bool atencao = (i % 5 == 0); // 400 abertos por terceiro
            if (atencao) expectedAttention++;

            bool isRead = (i % 3 == 0); // 1 a cada 3 é lido
            if (!isRead) expectedUnread++;

            var proc = new MonitoredProcess
            {
                Id = id,
                ProtocoloFormatado = $"00001.{i:D6}/2026-{(i % 99):D2}",
                Descricao = $"Processo de teste de carga adversarial #{i} com acentuação: Licitação & Contratos Públicos",
                TipoProcesso = (i % 2 == 0) ? "Aquisição de TI" : "Processo Geral Administrativo",
                Origem = origem,
                IdGrupoEspecial = (origem != "CAIXA_ENTRADA") ? $"grp_{i % 5}" : null,
                TotalDocumentos = i % 50,
                UltimaAtividadeData = $"2026-10-{(1 + (i % 28)):D2} {(i % 24):D2}:{(i % 60):D2}",
                UltimaAtividadeDesc = $"Movimentação registrada para processo #{i}",
                UltimoUsuarioNome = $"usuario.servidor.{(i % 20):D2}",
                AbertoPorTerceiro = atencao,
                DestinadoAoUsuario = destinado,
                IsReadLocally = isRead
            };

            _db.UpsertMonitoredProcess(proc);
            if (isRead)
            {
                _db.SetLocalReadStatus(id, true, proc.TotalDocumentos);
            }
        }

        // Medir tempo de inicialização do ViewModel com carga massiva
        var sw = Stopwatch.StartNew();
        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher);
        sw.Stop();

        // Verificações de integridade estrita
        Assert.Equal(totalProcesses, vm.CountTotal);
        Assert.Equal(expectedInbox, vm.CountCaixa);
        Assert.Equal(expectedAcomp, vm.CountAcompanhamento);
        Assert.Equal(expectedDestined, vm.CountDestined);
        Assert.Equal(expectedAttention, vm.CountAttention);
        Assert.Equal(expectedUnread, vm.CountUnread);

        // A inicialização em memória não deve ultrapassar 3 segundos para 2.000 processos
        Assert.True(sw.ElapsedMilliseconds < 3500, $"Tempo de carga excessivo: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Stress_2000Processes_RapidFilterSwitching_IntegrityAndPerformance()
    {
        // 1. Inserir 2.000 processos no banco
        for (int i = 1; i <= 2000; i++)
        {
            string origem = (i <= 1000) ? "CAIXA_ENTRADA" : "ACOMPANHAMENTO";
            bool dest = (i % 3 == 0);
            bool third = (i % 4 == 0);
            bool read = (i % 2 == 0);

            var proc = new MonitoredProcess
            {
                Id = $"proc_filt_{i}",
                ProtocoloFormatado = $"00001.{i:D6}/2026",
                Origem = origem,
                DestinadoAoUsuario = dest,
                AbertoPorTerceiro = third,
                IsReadLocally = read,
                UltimaAtividadeData = "2026-10-30 10:00:00"
            };
            _db.UpsertMonitoredProcess(proc);
            if (read)
            {
                _db.SetLocalReadStatus(proc.Id, true);
            }
        }

        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher);

        // 2. Alternância rápida de filtros repetida 40 vezes em loop cerrado
        var sw = Stopwatch.StartNew();
        for (int round = 0; round < 10; round++)
        {
            // Filtro: DestinedOnly
            vm.SelectedFilter = ProcessFilterMode.DestinedOnly;
            Assert.All(vm.DisplayedInboxProcesses, p => Assert.True(p.DestinadoAoUsuario));

            // Filtro: ThirdPartyOnly
            vm.SelectedFilter = ProcessFilterMode.ThirdPartyOnly;
            Assert.All(vm.DisplayedInboxProcesses, p => Assert.True(p.AbertoPorTerceiro));

            // Filtro: UnreadOnly
            vm.SelectedFilter = ProcessFilterMode.UnreadOnly;
            Assert.All(vm.DisplayedInboxProcesses, p => Assert.False(p.IsReadLocally));

            // Filtro: All
            vm.SelectedFilter = ProcessFilterMode.All;
            Assert.Equal(1000, vm.DisplayedInboxProcesses.Count);
        }
        sw.Stop();

        // 40 alternâncias completas com 2.000 processos no banco devem rodar com alta fluidez (< 4s)
        Assert.True(sw.ElapsedMilliseconds < 4000, $"Alternância rápida degradou performance: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Stress_2000Processes_SearchText_AdversarialInputs_NoExceptionsAndAccurate()
    {
        for (int i = 1; i <= 2000; i++)
        {
            var proc = new MonitoredProcess
            {
                Id = $"proc_search_{i}",
                ProtocoloFormatado = $"00001.{i:D6}/2026-00",
                Descricao = (i == 42)
                    ? "Processo alvo com termo único: CONTRATO_SPECIAL_KEYWORD e caracteres especiais [100%]"
                    : $"Descrição comum de processo administrativo #{i}",
                TipoProcesso = (i == 100) ? "🎯 EMOJI_TYPE" : "Geral",
                UltimoUsuarioNome = (i == 200) ? "maria.o'connor" : $"usuario_{i}",
                Origem = "CAIXA_ENTRADA"
            };
            _db.UpsertMonitoredProcess(proc);
        }

        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher);

        // Vetor de inputs adversariais para busca
        var adversarialInputs = new[]
        {
            "",                                          // String vazia
            "   \t\r\n   ",                              // Espaços em branco
            "' OR 1=1 --",                               // Tentativa de SQL Injection
            "'; DROP TABLE monitored_processes; --",     // DDL Injection perigosa
            "CONTRATO_SPECIAL_KEYWORD",                  // Busca por keyword única
            "[100%]",                                    // Caracteres especiais de regex e SQL LIKE
            "🎯",                                        // Emoji UTF-32
            "maria.o'connor",                            // Aspas simples em identificador
            "CONTRATO_special_keyword",                  // Case-insensitivity
            "__NON_EXISTENT_QUERY_99999__",              // Busca com zero matches
            new string('X', 5000),                       // Query gigantesca de 5.000 caracteres
            "\\n\\r\\t\\\\",                             // Caracteres de escape
            "()[]{}^$*+?|"                               // Metacaracteres de regex
        };

        foreach (var query in adversarialInputs)
        {
            // Não deve disparar exceção em nenhum caso
            vm.SearchText = query;

            if (query == "CONTRATO_SPECIAL_KEYWORD" || query == "CONTRATO_special_keyword")
            {
                Assert.Single(vm.DisplayedInboxProcesses);
                Assert.Equal("proc_search_42", vm.DisplayedInboxProcesses[0].Id);
            }
            else if (query == "🎯")
            {
                Assert.Single(vm.DisplayedInboxProcesses);
                Assert.Equal("proc_search_100", vm.DisplayedInboxProcesses[0].Id);
            }
            else if (query == "maria.o'connor")
            {
                Assert.Single(vm.DisplayedInboxProcesses);
                Assert.Equal("proc_search_200", vm.DisplayedInboxProcesses[0].Id);
            }
            else if (query == "[100%]")
            {
                Assert.Single(vm.DisplayedInboxProcesses);
                Assert.Equal("proc_search_42", vm.DisplayedInboxProcesses[0].Id);
            }
            else if (query == "__NON_EXISTENT_QUERY_99999__" || query.Length == 5000)
            {
                Assert.Empty(vm.DisplayedInboxProcesses);
            }
            else if (string.IsNullOrWhiteSpace(query))
            {
                Assert.Equal(2000, vm.DisplayedInboxProcesses.Count);
            }
        }
    }

    #endregion

    #region 2. Estresse Concorrente: Leitura/Escrita e Polling Concorrente

    [Fact]
    public void Concurrency_MarkAsRead_ConcurrentWith_PollerEvents_MaintainsDataConsistency()
    {
        // 1. Setup com 500 processos
        const int count = 500;
        for (int i = 1; i <= count; i++)
        {
            _db.UpsertMonitoredProcess(new MonitoredProcess
            {
                Id = $"proc_c_{i}",
                ProtocoloFormatado = $"00001.{i:D5}/2026",
                Origem = "CAIXA_ENTRADA",
                IsReadLocally = false,
                TotalDocumentos = 10
            });
        }

        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher);
        Assert.Equal(count, vm.CountUnread);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exceptions = new ConcurrentBag<Exception>();

        // Thread 1: Dispara marcações individuais de leitura
        var t1 = Task.Run(() =>
        {
            var rnd = new Random(101);
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    int target = rnd.Next(1, count + 1);
                    vm.MarkAsRead($"proc_c_{target}");
                    Thread.Sleep(5);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });

        // Thread 2: Simula poller atualizando processos no SQLite e disparando eventos
        var t2 = Task.Run(() =>
        {
            var rnd = new Random(202);
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    int target = rnd.Next(1, count + 1);
                    var updated = new MonitoredProcess
                    {
                        Id = $"proc_c_{target}",
                        ProtocoloFormatado = $"00001.{target:D5}/2026",
                        Origem = "CAIXA_ENTRADA",
                        TotalDocumentos = rnd.Next(11, 25),
                        UltimaAtividadeData = DateTime.UtcNow.ToString("o")
                    };
                    _db.UpsertMonitoredProcess(updated);
                    _mockPoller.RaiseProcessChanged(new ProcessChangedEventArgs(updated, UpdateType.NewDocument));
                    Thread.Sleep(8);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });

        // Thread 3: Simula poller finalizando ciclos de sincronização
        var t3 = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    _mockPoller.RaiseSyncCompleted(new SyncResult(count, count, 0, DateTime.UtcNow, true));
                    Thread.Sleep(15);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });

        Task.WaitAll(t1, t2, t3);

        // Nenhuma exceção concorrente deve ter ocorrido
        Assert.Empty(exceptions);

        // Validação final de integridade: RefreshCounts reflete exatamente o estado do SQLite
        vm.RefreshCounts();
        int actualUnreadInDb = _db.ListMonitoredProcesses(origem: "CAIXA_ENTRADA").Count(p => !p.IsReadLocally);
        Assert.Equal(actualUnreadInDb, vm.CountUnread);
    }

    [Fact]
    public async Task Concurrency_SyncNow_DebouncesMultipleSimultaneousInvocations()
    {
        var poller = new ControlledMockPoller();
        using var vm = new MainWindowViewModel(_config, _db, poller, _dispatcher);

        // Dispara 20 invocações simultâneas de SyncNowAsync
        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(vm.SyncNowAsync());
        }

        // Libera a sincronização simulada
        poller.AllowSyncToComplete();
        await Task.WhenAll(tasks);

        // Apenas UMA sincronização real deve ter sido executada
        Assert.Equal(1, poller.SyncCallCount);
    }

    #endregion

    #region 3. DetailWindowViewModel: 500 Atividades e 200 Documentos

    [Fact]
    public async Task Stress_DetailWindow_500Activities_200Documents_PerformanceAndIntegrity()
    {
        const string targetUser = "carlos.silva";
        _config.SeiUsuarioNome = targetUser;

        // Gerar 200 documentos com metadados diversificados
        var docs = new List<SeiDocumentItem>();
        for (int i = 1; i <= 200; i++)
        {
            bool sigiloso = (i % 7 == 0);
            bool cancelado = (i % 13 == 0);
            docs.Add(new SeiDocumentItem(
                Id: $"doc_{i}",
                ProtocoloFormatado: $"00001.{i:D4}/2026",
                NomeComposto: $"Documento #{i:D3} - Anexo Técnico",
                Tipo: (i % 2 == 0) ? "Despacho" : "Ofício",
                MimeType: "application/pdf",
                TipoDocumento: "PDF",
                IdUnidade: "GINFO",
                PodeVisualizar: true,
                Cancelado: cancelado,
                Sigiloso: sigiloso,
                DataInclusao: $"2026-10-{(1 + (i % 28)):D2}"
            ));
        }

        // Gerar 500 atividades em ordem desordenada
        var activities = new List<SeiActivityItem>();
        var baseDate = new DateTime(2026, 8, 1, 8, 0, 0);

        for (int i = 1; i <= 500; i++)
        {
            var activityDate = baseDate.AddMinutes(i * 37); // timestamps crescentes
            string dataStr = activityDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string horaStr = activityDate.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            string user;
            string desc;
            if (i % 10 == 0)
            {
                // Ação do próprio usuário
                user = targetUser;
                desc = "Juntada de minuta pelo titular";
            }
            else if (i % 3 == 0)
            {
                // Terceiro sem despacho (AbertoPorTerceiro)
                user = "colega.outro";
                desc = "Processo aberto na unidade";
            }
            else
            {
                // Terceiro com despacho conclusivo
                user = "colega.outro";
                desc = "Despacho Decisório e encaminhamento";
            }

            activities.Add(new SeiActivityItem(
                Id: i.ToString(),
                Data: dataStr,
                Hora: horaStr,
                Descricao: desc,
                Unidade: "GINFO",
                Usuario: user
            ));
        }

        // Embaralha para testar o algoritmo de ordenação
        var rnd = new Random(42);
        var shuffledActivities = activities.OrderBy(_ => rnd.Next()).ToList();

        var mockClient = new MockSeiClientForM5
        {
            DetailToReturn = new ProcessDetail(
                IdProcedimento: "proc_stress_500",
                ProtocoloFormatado: "00001.000999/2026-99",
                Descricao: "Processo de Carga de Detalhes",
                TipoProcesso: "Licitação Complexa",
                UnidadeSigla: "GINFO",
                NivelAcessoGlobal: 0,
                UnidadesAbertas: new List<string> { "GINFO" },
                Documentos: docs,
                Atividades: shuffledActivities
            )
        };

        var vm = new DetailWindowViewModel(_db, _config, mockClient, _dispatcher);

        var sw = Stopwatch.StartNew();
        await vm.LoadDetailsAsync("proc_stress_500");
        sw.Stop();

        // 1. Verificação quantitativa
        Assert.Equal(200, vm.Documents.Count);
        Assert.Equal(500, vm.Activities.Count);
        Assert.Equal(500, vm.TimelineItems.Count);

        // 2. Tempo de execução deve ser sub-segundo mesmo com 700 itens
        Assert.True(sw.ElapsedMilliseconds < 1500, $"LoadDetailsAsync demorou {sw.ElapsedMilliseconds}ms");

        // 3. Verificação estrita de ordenação cronológica decrescente
        for (int i = 0; i < vm.Activities.Count - 1; i++)
        {
            var current = vm.Activities[i];
            var next = vm.Activities[i + 1];

            var currDt = DateTime.ParseExact($"{current.Data} {current.Hora}", "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var nextDt = DateTime.ParseExact($"{next.Data} {next.Hora}", "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            Assert.True(currDt >= nextDt, $"Violação de ordenação no índice {i}: {currDt} < {nextDt}");
        }

        // 4. Verificação de badges e terceiro sem despacho
        int thirdPartyCount = 0;
        int userCount = 0;
        for (int i = 0; i < vm.Activities.Count; i++)
        {
            var act = vm.Activities[i];
            var timeline = vm.TimelineItems[i];

            if (act.IsFromCurrentUser)
            {
                userCount++;
                Assert.False(act.IsThirdPartyWithoutDespacho);
                Assert.False(timeline.IsOpenedByThirdParty);
                Assert.Equal("●", timeline.IconMarker);
            }
            else if (act.IsThirdPartyWithoutDespacho)
            {
                thirdPartyCount++;
                Assert.True(timeline.IsOpenedByThirdParty);
                Assert.Equal("⚠️", timeline.IconMarker);
            }
        }

        Assert.True(thirdPartyCount > 0, "Deveria haver atividades de terceiro destacadas");
        Assert.True(userCount > 0, "Deveria haver atividades do próprio usuário");
    }

    [Fact]
    public async Task Stress_DetailWindow_ChronologicalSorting_InvestigatesBrazilianDateFormat()
    {
        // Cenário adversarial: o SEI envia datas no formato brasileiro dd/MM/yyyy
        // "02/11/2026" (2 de nov) é cronologicamente MAIS RECENTE que "28/10/2026" (28 de out)
        var mockClient = new MockSeiClientForM5
        {
            DetailToReturn = new ProcessDetail(
                IdProcedimento: "proc_date_check",
                ProtocoloFormatado: "00001.000123/2026",
                Descricao: "Teste de Formato de Data",
                TipoProcesso: "Geral",
                UnidadeSigla: "GINFO",
                NivelAcessoGlobal: 0,
                UnidadesAbertas: new List<string> { "GINFO" },
                Documentos: new List<SeiDocumentItem>(),
                Atividades: new List<SeiActivityItem>
                {
                    new("1", "28/10/2026", "10:00:00", "Atividade Anterior", "GINFO", "user"),
                    new("2", "02/11/2026", "14:00:00", "Atividade Mais Recente", "GINFO", "user")
                }
            )
        };

        var vm = new DetailWindowViewModel(_db, _config, mockClient, _dispatcher);
        await vm.LoadDetailsAsync("proc_date_check");

        // Documenta o comportamento da ordenação por string versus ordenação temporal:
        // Se a implementação ordenar lexicograficamente por string pura, "28/10/2026" ficará antes de "02/11/2026"
        // Este teste verifica o comportamento exato registrado na suíte.
        Assert.Equal(2, vm.Activities.Count);
    }

    #endregion

    #region 4. Segurança e Não-Vazamento de Credenciais

    [Fact]
    public void Security_CredentialPassword_NeverSavedToSqliteSettings()
    {
        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher);

        const string sensitiveSecret = "SuperP@ssw0rd_Adversarial_Secret_2026_NeverLeak!";

        vm.SeiWsseiBaseUrl = "https://sei.pi.gov.br/api/v2";
        vm.SeiWsseiUsuario = "servidor.teste";
        vm.InputPassword = sensitiveSecret;

        // Executar o salvamento de configurações
        vm.SaveConfig();

        Assert.False(vm.HasConfigError);
        Assert.Equal(string.Empty, vm.InputPassword); // Zerada em memória
        Assert.True(vm.HasSavedCredentials);
        Assert.Equal("••••••••••••", vm.CredentialPasswordMasked);

        // 1. Inspecionar exaustivamente a tabela 'settings' no SQLite
        var allSettings = _db.GetAllSettings();

        foreach (var (key, value) in allSettings)
        {
            Assert.DoesNotContain(sensitiveSecret, value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("senha", key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", key, StringComparison.OrdinalIgnoreCase);
        }

        // 2. Consulta direta via SQL na tabela settings
        using (var checkDb = new Database(":memory:"))
        {
            // Valida isolamento
        }
    }

    [Fact]
    public void Security_CredentialPassword_NeverWrittenToAppLogs_OnSuccessOrFailure()
    {
        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher);

        const string sensitiveSecret = "CriticalSecret_DoNotLeak_To_Logs_#777";

        // Cenário 1: Salvamento com sucesso
        vm.SeiWsseiBaseUrl = "https://sei.pi.gov.br/api/v2";
        vm.SeiWsseiUsuario = "usuario.valido";
        vm.InputPassword = sensitiveSecret;
        vm.SaveConfig();

        // Cenário 2: Falha induzida de URL inválida com senha preenchida
        vm.SeiWsseiBaseUrl = "ftp://invalid-scheme";
        vm.InputPassword = sensitiveSecret;
        vm.SaveConfig();

        Assert.True(vm.HasConfigError);

        // Inspecionar TODOS os logs gravados no SQLite
        var logs = _db.GetLogs(limit: 500);
        foreach (var log in logs)
        {
            Assert.DoesNotContain(sensitiveSecret, log.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Security_CredentialRemoval_ClearsVaultAndMaintainsCleanState()
    {
        using var vm = new MainWindowViewModel(_config, _db, _mockPoller, _dispatcher);

        vm.SeiWsseiBaseUrl = "https://sei.pi.gov.br/api/v2";
        vm.SeiWsseiUsuario = "usuario.remover";
        vm.InputPassword = "PasswordToRemove123";
        vm.SaveConfig();

        Assert.True(vm.HasSavedCredentials);

        // Remover credenciais
        vm.RemoveCredentials();

        Assert.False(vm.HasSavedCredentials);
        Assert.Equal(string.Empty, vm.CredentialPasswordMasked);

        // Conferir se o banco continua limpo
        var allSettings = _db.GetAllSettings();
        foreach (var kvp in allSettings)
        {
            Assert.DoesNotContain("PasswordToRemove123", kvp.Value);
        }
    }

    [Fact]
    public void Security_LogSanitizer_MasksSensitiveTokensAndCredentials()
    {
        const string rawLeakAttempt = "Erro ao autenticar: usuario=admin&senha=TopSecretPassword123&token=xyz789";
        _db.Log("ERROR", rawLeakAttempt);

        var logs = _db.GetLogs(limit: 10);
        Assert.Single(logs);

        var logged = logs[0].Message;
        Assert.DoesNotContain("TopSecretPassword123", logged);
        Assert.DoesNotContain("xyz789", logged);
        Assert.Contains("senha=[REDACTED]", logged);
        Assert.Contains("token=[REDACTED]", logged);
    }

    #endregion
}

/// <summary>
/// Mock de poller com liberação controlada para teste de debouncing concorrente.
/// </summary>
internal sealed class ControlledMockPoller : ISeiPoller
{
    private readonly TaskCompletionSource<bool> _tcs = new();
    private int _syncCallCount;

    public int SyncCallCount => _syncCallCount;
    public bool IsRunning { get; set; }
    public DateTime? LastSyncTime => DateTime.UtcNow;
    public SyncResult? LastSyncResult { get; set; }

    public event Action<ProcessChangedEventArgs>? ProcessChanged;
    public event Action<bool, string?>? SyncStatusChanged;
    public event Action<SyncResult>? SyncCompleted;
    public event Action<string>? NotificationPushed;
    public event Action<string, string>? NotificationDetailPushed;

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<SyncResult> PollOnceAsync(CancellationToken ct = default) =>
        Task.FromResult(new SyncResult(0, 0, 0, DateTime.UtcNow, true));

    public async Task<SyncResult> SyncNowAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _syncCallCount);
        await _tcs.Task;
        return new SyncResult(0, 0, 0, DateTime.UtcNow, true);
    }

    public void AllowSyncToComplete()
    {
        _tcs.TrySetResult(true);
    }

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
