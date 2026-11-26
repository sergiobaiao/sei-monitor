using System;
using System.Collections.Generic;

namespace SeiMonitor.E2ETests.Harness;

public record ProcessoModel(
    string Id,
    string ProtocoloFormatado,
    string? Descricao,
    string? TipoProcesso,
    string Origem,
    string? IdGrupoEspecial,
    int TotalDocumentos,
    string? UltimaAtividadeData,
    string? UltimaAtividadeDesc,
    string? UltimoUsuarioNome,
    bool AbertoPorTerceiro,
    bool DestinadoAoUsuario,
    string UpdatedAt
);

public record AtividadeModel(
    string Id,
    string Data,
    string Hora,
    string Descricao,
    string? Unidade,
    string? Usuario
);

public record GrupoAcompanhamentoModel(
    string Id,
    string Nome,
    int TotalProcessos,
    string UpdatedAt
);

public record LocalReadStatusModel(
    string ProcessoId,
    bool IsReadLocally,
    int LastReadDocCount,
    string? LastReadAt
);

public record LogEntryModel(
    long Id,
    string Level,
    string Message,
    string CreatedAt
);

public record UserConfigModel(
    string WsseiUrl = "https://sei.pi.gov.br/api/v2",
    string SeadUrl = "https://api.sei.pi.gov.br",
    string UnidadePadrao = "110000001",
    string Orgao = "PGE-PI",
    int PollingIntervalMinutes = 5,
    double WidgetOpacity = 0.95,
    double WidgetX = 100,
    double WidgetY = 100,
    double WidgetWidth = 380,
    double WidgetHeight = 520,
    string Keywords = "Victor, vlima, GINFO",
    string MeuUsuarioSEI = "victor.lima"
);
