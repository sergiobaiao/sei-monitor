using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SeiMonitor.Core;
using Xunit;

namespace SeiMonitor.Tests;

/// <summary>
/// Suite de testes empíricos adversariais do agente m3_challenger_1.
/// Validação empírica rigorosa de:
/// 1. Zero overhead: se TotalDocumentos e UltimaAtividadeData são idênticos, GetProcessDetailsAsync NUNCA é chamado.
/// 2. Detecção precisa de deltas: novo documento aciona GetProcessDetailsAsync, atualiza banco e reseta is_read_locally para false.
/// 3. Preservação de status de leitura local quando a ação é praticada pelo próprio usuário logado (is_read_locally = true).
/// 4. Estresse de concorrência com 30 invocações paralelas (SemaphoreSlim serializa sem deadlocks).
/// 5. Resiliência a falhas caóticas de rede (erros individuais não abortam lote).
/// 6. Lote massivo com 500 processos (250 estáveis com 0 chamadas de detalhe, 250 com deltas).
/// 7. Regra constitucional contra overflow de payloads ou corpo bruto.
/// </summary>
public class M3SeiPollerStressTests : IDisposable
{
    private readonly Database _db;
    private readonly Config _config;
    private readonly TestSeiClient _client;
    private readonly SeiPoller _poller;

    public M3SeiPollerStressTests()
    {
        _db = new Database(":memory:");
        _config = new Config(_db)
        {
            SeiUnidade = "110000000",
            SeiUnidadeSigla = "GINFO",
            SeiUsuarioNome = "victor.lima",
            UserKeywordsRaw = "Victor; GINFO/Sistemas; Urgente",
            CheckIntervalMinutes = 1
        };
        _client = new TestSeiClient();
        _poller = new SeiPoller(_config, _db, _client);
    }

    public void Dispose()
    {
        _poller.Dispose();
        _client.Dispose();
        _db.Dispose();
    }

    // =========================================================================
    // 1. COMPROVAÇÃO EMPÍRICA: ZERO OVERHEAD (NO DELTA -> GETPROCESSDETAILS NEVER CALLED)
    // =========================================================================

    [Fact]
    public async Task Challenger_EmpiricalProof_WhenCountAndDateMatch_GetProcessDetailsIsNeverCalled()
    {
        // Arrange: 50 processos cadastrados no banco com contagens e datas exatas
        const int totalProcesses = 50;
        var searchItems = new List<SeiProcessItem>();

        for (int i = 1; i <= totalProcesses; i++)
        {
            string id = $"PROC_{i:D4}";
            string proto = $"00001.{i:D6}/2026-00";
            int docCount = i * 2;
            string actDate = $"2026-10-29 10:{i % 60:D2}";
            bool initialRead = i % 2 == 0; // Metade lido, metade não lido

            _db.UpsertMonitoredProcess(new MonitoredProcess
            {
                Id = id,
                ProtocoloFormatado = proto,
                TotalDocumentos = docCount,
                UltimaAtividadeData = actDate,
                UltimaAtividadeDesc = $"Andamento {i}",
                UltimoUsuarioNome = "servidor.outro",
                Origem = "CAIXA_ENTRADA"
            });
            _db.SetLocalReadStatus(id, isRead: initialRead, docCount: docCount);

            searchItems.Add(new SeiProcessItem(id, proto, "Desc", "Tipo", "GINFO", null, null));

            // Resumo leve idêntico ao banco
            _client.LightSummaries[id] = new LightProcessSummary(
                id,
                TotalDocumentos: docCount,
                UltimaAtividadeData: actDate,
                UltimaAtividadeDesc: $"Andamento {i}",
                UltimoUsuarioNome: "servidor.outro",
                ProtocoloFormatado: proto
            );
        }

        _client.SearchResult = new ProcessListResult(totalProcesses, searchItems);

        int eventFiredCount = 0;
        _poller.ProcessChanged += _ => Interlocked.Increment(ref eventFiredCount);

        // Act
        var result = await _poller.PollOnceAsync();

        // Assert: Comprovação empírica absoluta
        Assert.Equal(totalProcesses, _client.LightSummaryCalls.Count);
        Assert.Empty(_client.ProcessDetailsCalls); // NUNCA chamado para nenhum dos 50 processos!
        Assert.Equal(0, eventFiredCount); // Nenhum evento de alteração disparado
        Assert.Equal(totalProcesses, result.TotalChecked);
        Assert.Equal(totalProcesses, result.UnchangedCount);
        Assert.Equal(0, result.ChangedCount);
        Assert.Equal(0, result.NewDocumentsCount);
        Assert.Equal(0, result.NewActivitiesCount);

        // Verifica que todos os 50 preservaram rigorosamente seus status de leitura
        for (int i = 1; i <= totalProcesses; i++)
        {
            string id = $"PROC_{i:D4}";
            bool expectedRead = i % 2 == 0;
            Assert.Equal(expectedRead, _db.GetLocalReadStatus(id));
        }
    }

    // =========================================================================
    // 2. COMPROVAÇÃO EMPÍRICA: NOVO DOCUMENTO RESETA IS_READ_LOCALLY PARA FALSE
    // =========================================================================

    [Theory]
    [InlineData(1, 2)]   // Adição de 1 documento
    [InlineData(5, 10)]  // Adição de 5 documentos
    [InlineData(10, 11)] // Adição de 1 documento
    public async Task Challenger_EmpiricalProof_WhenNewDocumentInserted_DetailsFetched_And_ReadStatusResetToFalse(int initialDocs, int updatedDocs)
    {
        // Arrange
        const string procId = "PROC_NEW_DOC";
        const string proto = "00001.999999/2026-99";

        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = procId,
            ProtocoloFormatado = proto,
            TotalDocumentos = initialDocs,
            UltimaAtividadeData = "2026-10-29 08:00",
            Origem = "CAIXA_ENTRADA"
        });
        // Processo estava marcado como LIDO pelo usuário
        _db.SetLocalReadStatus(procId, isRead: true, docCount: initialDocs);
        Assert.True(_db.GetLocalReadStatus(procId));

        _client.SearchResult = new ProcessListResult(1, new List<SeiProcessItem>
        {
            new(procId, proto, "Desc", "Tipo", "GINFO", null, null)
        });

        // Retorna contagem de documentos incrementada
        _client.LightSummaries[procId] = new LightProcessSummary(
            procId,
            TotalDocumentos: updatedDocs,
            UltimaAtividadeData: "2026-10-29 08:00",
            UltimaAtividadeDesc: "Novo documento anexado",
            UltimoUsuarioNome: "colega.trabalho",
            ProtocoloFormatado: proto
        );

        _client.ProcessDetails[procId] = new ProcessDetail(
            procId, proto, "Desc", "Tipo", "GINFO", 0, new List<string> { "GINFO" },
            new List<SeiDocumentItem>
            {
                new("D_NEW", "Parecer Técnico", "Parecer", "G", "text/html", "Parecer", "1", true, false, false, "2026-10-29")
            },
            new List<SeiActivityItem>()
        );

        ProcessChangedEventArgs? capturedEvent = null;
        _poller.ProcessChanged += e => capturedEvent = e;

        // Act
        var result = await _poller.PollOnceAsync();

        // Assert
        Assert.Single(_client.ProcessDetailsCalls); // Comprovado: chamou GetProcessDetailsAsync!
        Assert.Equal(procId, _client.ProcessDetailsCalls[0].Id);
        Assert.Equal(1, result.ChangedCount);
        Assert.Equal(1, result.NewDocumentsCount);

        // Banco SQLite foi atualizado
        var procInDb = _db.GetMonitoredProcess(procId);
        Assert.NotNull(procInDb);
        Assert.Equal(updatedDocs, procInDb!.TotalDocumentos);

        // Status local de leitura foi RESETADO para false
        Assert.False(_db.GetLocalReadStatus(procId));
        Assert.False(procInDb.IsReadLocally);

        // Evento ProcessChanged disparado com parâmetros corretos
        Assert.NotNull(capturedEvent);
        Assert.Equal(UpdateType.NewDocument, capturedEvent!.Type);
        Assert.Equal(initialDocs, capturedEvent.PreviousDocCount);
        Assert.Equal(updatedDocs, capturedEvent.NewDocCount);
    }

    // =========================================================================
    // 3. COMPROVAÇÃO EMPÍRICA: AÇÃO DO PRÓPRIO USUÁRIO PRESERVA IS_READ_LOCALLY = TRUE
    // =========================================================================

    [Theory]
    [InlineData("victor.lima")]
    [InlineData("VICTOR.LIMA")]
    [InlineData("  victor.lima  ")]
    public async Task Challenger_EmpiricalProof_WhenUserOwnAction_ReadStatusRemainsTrue(string actingUser)
    {
        // Arrange
        const string procId = "PROC_USER_ACTION";
        const string proto = "00001.888888/2026-88";

        _db.UpsertMonitoredProcess(new MonitoredProcess
        {
            Id = procId,
            ProtocoloFormatado = proto,
            TotalDocumentos = 3,
            UltimaAtividadeData = "2026-10-29 09:00",
            Origem = "CAIXA_ENTRADA"
        });
        _db.SetLocalReadStatus(procId, isRead: true, docCount: 3);

        _client.SearchResult = new ProcessListResult(1, new List<SeiProcessItem>
        {
            new(procId, proto, "Desc", "Tipo", "GINFO", null, null)
        });

        // Ação executada pelo próprio usuário logado configurado ("victor.lima")
        _client.LightSummaries[procId] = new LightProcessSummary(
            procId,
            TotalDocumentos: 4, // Novo documento gerado pelo próprio usuário
            UltimaAtividadeData: "2026-10-29 11:00",
            UltimaAtividadeDesc: "Despacho assinado pelo usuário",
            UltimoUsuarioNome: actingUser,
            ProtocoloFormatado: proto
        );

        _client.ProcessDetails[procId] = new ProcessDetail(
            procId, proto, "Desc", "Tipo", "GINFO", 0, new List<string> { "GINFO" },
            new List<SeiDocumentItem>
            {
                new("D_MY_DOC", "Despacho", "Despacho", "G", "text/html", "Despacho", "1", true, false, false, "2026-10-29")
            },
            new List<SeiActivityItem>
            {
                new("ACT_USER", "29/10/2026", "11:00", "Despacho assinado", "GINFO", actingUser)
            }
        );

        // Act
        var result = await _poller.PollOnceAsync();

        // Assert
        Assert.Single(_client.ProcessDetailsCalls);
        Assert.Equal(1, result.ChangedCount);

        var procInDb = _db.GetMonitoredProcess(procId);
        Assert.NotNull(procInDb);
        Assert.Equal(4, procInDb!.TotalDocumentos);

        // Comprovação empírica: IsReadLocally permanece TRUE porque a ação foi do próprio usuário!
        Assert.True(_db.GetLocalReadStatus(procId));
        Assert.True(procInDb.IsReadLocally);
        Assert.False(procInDb.AbertoPorTerceiro);
    }

    // =========================================================================
    // 4. TESTE DE ESTRESSE: ALTA CONCORRÊNCIA COM 30 DISPAROS PARALELOS
    // =========================================================================

    [Fact]
    public async Task Challenger_Stress_30ConcurrentPollCalls_SerializedCleanlyWithoutDeadlock()
    {
        // Arrange
        const int concurrentTasks = 30;
        int activeInWorker = 0;
        int maxSimultaneousInWorker = 0;
        var exceptions = new ConcurrentBag<Exception>();

        _client.SearchResult = new ProcessListResult(1, new List<SeiProcessItem>
        {
            new("P_CONC", "00001.000000/2026-00", "Desc", "Tipo", "GINFO", null, null)
        });

        _client.OnSearchAsync = async _ =>
        {
            int current = Interlocked.Increment(ref activeInWorker);
            int prevMax;
            do
            {
                prevMax = maxSimultaneousInWorker;
                if (current <= prevMax) break;
            } while (Interlocked.CompareExchange(ref maxSimultaneousInWorker, current, prevMax) != prevMax);

            await Task.Delay(20);
            Interlocked.Decrement(ref activeInWorker);
        };

        // Act: Disparar 30 chamadas simultâneas via Task.WhenAll
        var tasks = Enumerable.Range(0, concurrentTasks)
            .Select(async _ =>
            {
                try
                {
                    return await _poller.PollOnceAsync();
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                    throw;
                }
            })
            .ToList();

        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.Empty(exceptions);
        Assert.Equal(concurrentTasks, results.Length);
        Assert.All(results, r => Assert.True(r.Success));
        // O SemaphoreSlim(1,1) deve ter garantido que no máximo 1 estava ativo simultaneamente
        Assert.Equal(1, maxSimultaneousInWorker);
    }

    // =========================================================================
    // 5. TESTE DE ESTRESSE: RESILIÊNCIA A FALHAS CAÓTICAS INDIVIDUAIS
    // =========================================================================

    [Fact]
    public async Task Challenger_Stress_ChaoticNetworkFailures_ProcessesRemainingCandidates()
    {
        // Arrange: 20 processos. Processos ímpares falham com erro HTTP ou SEI, pares têm sucesso.
        const int totalCount = 20;
        var searchItems = new List<SeiProcessItem>();

        for (int i = 1; i <= totalCount; i++)
        {
            string id = $"CHAOS_{i}";
            string proto = $"00001.0000{i:D2}/2026-00";
            searchItems.Add(new SeiProcessItem(id, proto, "Desc", "Tipo", "GINFO", null, null));

            _client.LightSummaries[id] = new LightProcessSummary(id, 2, "2026-10-29 10:00", "Ok", "user", proto);
            _client.ProcessDetails[id] = new ProcessDetail(id, proto, "Desc", "Tipo", "GINFO", 0, new List<string>(), new List<SeiDocumentItem>(), new List<SeiActivityItem>());
        }

        _client.SearchResult = new ProcessListResult(totalCount, searchItems);

        _client.OnLightSummaryAsync = (id, _) =>
        {
            int num = int.Parse(id.Replace("CHAOS_", ""));
            if (num % 2 != 0)
            {
                if (num % 4 == 1)
                    throw new SeiException(SeiErrorCode.Indisponivel);
                else
                    throw new HttpRequestException("Falha de conexão TCP simulada");
            }
            return Task.CompletedTask;
        };

        // Act
        var result = await _poller.PollOnceAsync();

        // Assert: 10 erros, 10 sucessos processados
        Assert.Equal(totalCount, result.TotalChecked);
        Assert.Equal(10, result.ErrorsCount);
        Assert.Equal(10, result.ChangedCount); // Novos processos cadastrados com sucesso

        // Confirma que os 10 pares foram gravados no banco
        for (int i = 1; i <= totalCount; i++)
        {
            string id = $"CHAOS_{i}";
            var inDb = _db.GetMonitoredProcess(id);
            if (i % 2 == 0)
            {
                Assert.NotNull(inDb);
            }
            else
            {
                Assert.Null(inDb);
            }
        }
    }

    // =========================================================================
    // 6. TESTE DE ESTRESSE: LOTE MASSIVO DE 500 PROCESSOS
    // =========================================================================

    [Fact]
    public async Task Challenger_Stress_MassiveBatch_500Processes_ExactPartitioning()
    {
        // 250 processos inalterados
        // 100 novos documentos
        // 100 novas atividades
        // 50 processos inteiramente novos
        const int totalProcesses = 500;
        var searchItems = new List<SeiProcessItem>();

        for (int i = 1; i <= totalProcesses; i++)
        {
            string id = $"BATCH_{i:D4}";
            string proto = $"00001.{i:D6}/2026-00";
            searchItems.Add(new SeiProcessItem(id, proto, "Desc", "Tipo", "GINFO", null, null));

            if (i <= 250)
            {
                // Inalterados: já no banco com mesma contagem e data
                _db.UpsertMonitoredProcess(new MonitoredProcess
                {
                    Id = id,
                    ProtocoloFormatado = proto,
                    TotalDocumentos = 10,
                    UltimaAtividadeData = "2026-10-29 10:00",
                    Origem = "CAIXA_ENTRADA"
                });
                _client.LightSummaries[id] = new LightProcessSummary(id, 10, "2026-10-29 10:00", "Desc", "user", proto);
            }
            else if (i <= 350)
            {
                // 100 com novo documento
                _db.UpsertMonitoredProcess(new MonitoredProcess
                {
                    Id = id,
                    ProtocoloFormatado = proto,
                    TotalDocumentos = 5,
                    UltimaAtividadeData = "2026-10-29 10:00",
                    Origem = "CAIXA_ENTRADA"
                });
                _client.LightSummaries[id] = new LightProcessSummary(id, 6, "2026-10-29 10:00", "Desc", "user", proto);
                _client.ProcessDetails[id] = new ProcessDetail(id, proto, "Desc", "Tipo", "GINFO", 0, new List<string>(), new List<SeiDocumentItem>(), new List<SeiActivityItem>());
            }
            else if (i <= 450)
            {
                // 100 com nova atividade
                _db.UpsertMonitoredProcess(new MonitoredProcess
                {
                    Id = id,
                    ProtocoloFormatado = proto,
                    TotalDocumentos = 5,
                    UltimaAtividadeData = "2026-10-28 10:00",
                    Origem = "CAIXA_ENTRADA"
                });
                _client.LightSummaries[id] = new LightProcessSummary(id, 5, "2026-10-29 12:00", "Nova atividade", "user", proto);
                _client.ProcessDetails[id] = new ProcessDetail(id, proto, "Desc", "Tipo", "GINFO", 0, new List<string>(), new List<SeiDocumentItem>(), new List<SeiActivityItem>());
            }
            else
            {
                // 50 processos novos (não existem no banco)
                _client.LightSummaries[id] = new LightProcessSummary(id, 1, "2026-10-29 15:00", "Novo", "user", proto);
                _client.ProcessDetails[id] = new ProcessDetail(id, proto, "Novo", "Tipo", "GINFO", 0, new List<string>(), new List<SeiDocumentItem>(), new List<SeiActivityItem>());
            }
        }

        _client.SearchResult = new ProcessListResult(totalProcesses, searchItems);

        // Act
        var result = await _poller.PollOnceAsync();

        // Assert: Exata conferência do particionamento e comportamento
        Assert.Equal(500, result.TotalChecked);
        Assert.Equal(250, result.UnchangedCount);
        Assert.Equal(250, result.ChangedCount);
        Assert.Equal(100, result.NewDocumentsCount);
        Assert.Equal(100, result.NewActivitiesCount);
        Assert.Equal(50, result.NewProcessesCount);
        Assert.Equal(0, result.ErrorsCount);

        // GetProcessDetailsAsync foi chamado EXATAMENTE 250 vezes (somente para os que tiveram delta)
        Assert.Equal(250, _client.ProcessDetailsCalls.Count);
        Assert.Equal(500, _client.LightSummaryCalls.Count);
    }

    // =========================================================================
    // 7. TESTE ADVERSARIAL: CONSTITUCIONAL CONTRA OVERFLOW E CORPO BRUTO
    // =========================================================================

    [Fact]
    public async Task Challenger_Constitutional_ExorbitantPayloadsAreStrictlyTruncated()
    {
        const string procId = "PROC_OVERFLOW";
        string hugePayload = new string('A', 50000); // 50KB string

        _client.SearchResult = new ProcessListResult(1, new List<SeiProcessItem>
        {
            new(procId, "00001.999999/2026-99", hugePayload, hugePayload, "GINFO", null, null)
        });

        _client.LightSummaries[procId] = new LightProcessSummary(
            procId, 1, "2026-10-29 10:00", hugePayload, hugePayload, "00001.999999/2026-99"
        );

        _client.ProcessDetails[procId] = new ProcessDetail(
            procId, "00001.999999/2026-99", hugePayload, hugePayload, "GINFO", 0,
            new List<string>(), new List<SeiDocumentItem>(), new List<SeiActivityItem>()
        );

        // Act
        await _poller.PollOnceAsync();

        // Assert
        var proc = _db.GetMonitoredProcess(procId);
        Assert.NotNull(proc);
        Assert.True(proc!.Descricao?.Length <= 500);
        Assert.True(proc.UltimaAtividadeDesc?.Length <= 500);
    }
}
