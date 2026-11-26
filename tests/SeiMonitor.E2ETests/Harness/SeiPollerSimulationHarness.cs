using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SeiMonitor.E2ETests.Harness;

public record RemoteLightSummary(
    string IdProcedimento,
    int TotalDocumentos,
    string? UltimaAtividadeData,
    string? UltimaAtividadeDesc,
    string? UltimoUsuarioNome,
    string? UltimoDespachoTexto = null,
    string? ProtocoloFormatado = null,
    string? TipoProcesso = null,
    string? Descricao = null
);

public class SeiPollerSimulationHarness
{
    private readonly SqliteTestDatabase _db;
    public int LightPollQueriesCount { get; private set; }
    public int FullDetailFetchesCount { get; private set; }
    public int SkippedQueriesCount { get; private set; }

    public SeiPollerSimulationHarness(SqliteTestDatabase db)
    {
        _db = db;
    }

    public async Task<int> RunLightPollCycleAsync(
        IEnumerable<RemoteLightSummary> remoteProcesses,
        UserConfigModel config,
        Func<string, Task<RemoteLightSummary>>? lightSummaryFetcher = null)
    {
        int changedCount = 0;

        foreach (var remote in remoteProcesses)
        {
            LightPollQueriesCount++;

            // Simulate limit=1 lightweight query
            var summary = lightSummaryFetcher != null
                ? await lightSummaryFetcher(remote.IdProcedimento)
                : remote;

            var cached = _db.GetMonitoredProcess(summary.IdProcedimento);

            // Light comparison: total docs AND last activity timestamp
            if (cached != null &&
                cached.TotalDocumentos == summary.TotalDocumentos &&
                cached.UltimaAtividadeData == summary.UltimaAtividadeData)
            {
                // No change! Skip expensive detail retrieval
                SkippedQueriesCount++;
                continue;
            }

            // Detected change or new process
            changedCount++;
            FullDetailFetchesCount++;

            // Run TextAnalyzer heuristics
            var activities = new List<AtividadeModel>();
            if (!string.IsNullOrEmpty(summary.UltimaAtividadeDesc))
            {
                activities.Add(new AtividadeModel(
                    Id: "1",
                    Data: summary.UltimaAtividadeData ?? DateTime.UtcNow.ToString("dd/MM/yyyy"),
                    Hora: "12:00:00",
                    Descricao: summary.UltimaAtividadeDesc,
                    Unidade: config.UnidadePadrao,
                    Usuario: summary.UltimoUsuarioNome
                ));
            }

            bool abertoPorTerceiro = TextAnalyzerHarness.IsOpenedByThirdParty(activities, config.MeuUsuarioSEI);
            bool destinadoAoUsuario = TextAnalyzerHarness.IsDestinedToUser(
                summary.ProtocoloFormatado ?? summary.Descricao,
                summary.Descricao,
                summary.UltimoDespachoTexto,
                config.Keywords.Split(',').Select(k => k.Trim())
            );

            // Update SQLite cache
            var updatedModel = new ProcessoModel(
                Id: summary.IdProcedimento,
                ProtocoloFormatado: summary.ProtocoloFormatado ?? cached?.ProtocoloFormatado ?? "00001.000000/2026-00",
                Descricao: summary.Descricao ?? cached?.Descricao,
                TipoProcesso: summary.TipoProcesso ?? cached?.TipoProcesso ?? "Processo Administrativo",
                Origem: cached?.Origem ?? "CAIXA_ENTRADA",
                IdGrupoEspecial: cached?.IdGrupoEspecial,
                TotalDocumentos: summary.TotalDocumentos,
                UltimaAtividadeData: summary.UltimaAtividadeData,
                UltimaAtividadeDesc: summary.UltimaAtividadeDesc,
                UltimoUsuarioNome: summary.UltimoUsuarioNome,
                AbertoPorTerceiro: abertoPorTerceiro,
                DestinadoAoUsuario: destinadoAoUsuario,
                UpdatedAt: DateTime.UtcNow.ToString("o")
            );

            _db.UpsertMonitoredProcess(updatedModel);

            // Mark as unread locally on new/updated content
            _db.SetLocalReadStatus(summary.IdProcedimento, isRead: false, lastReadDocCount: summary.TotalDocumentos, lastReadAt: null);
        }

        return changedCount;
    }
}
