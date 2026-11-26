# Implementation Tasks: SEI Desktop Monitor com Widget Fences e Rastreamento de Caixa Compartilhada

**Branch**: `001-sei-desktop-monitor` | **Date**: 2026-10-29 | **Status**: Completed (100% Implemented & Tested)

---

## Phase 1: Setup do Projeto e Infraestrutura Local

- [x] **T-101**: Inicializar a estrutura do projeto e criar `src/SeiMonitor/SeiMonitor.csproj` configurado para `net10.0-windows`, `UseWPF=true`, `UseWindowsForms=true`, `ImplicitUsings=enable`, `Nullable=enable`, adicionando o pacote `Microsoft.Data.Sqlite` e `app.manifest`.
- [x] **T-102**: Implementar `src/SeiMonitor/Core/Database.cs` com o esquema SQLite completo (`settings`, `monitored_processes`, `special_groups`, `local_read_status`, `app_logs`), métodos de migração automática e queries tipadas.
- [x] **T-103**: Implementar `src/SeiMonitor/Core/CredentialStore.cs` utilizando chamadas Win32 ao Windows Credential Manager (`Advapi32.dll`: `CredWriteW`, `CredReadW`, `CredDeleteW`) para salvar credenciais do SEI com segurança.
- [x] **T-104**: Implementar `src/SeiMonitor/Core/Config.cs` para gerenciamento reativo de configurações (URL do WSSEI, URL da SEAD, unidade de consulta padrão, órgão, intervalo de polling em minutos, opacidade do widget, dimensões salvas e palavras-chave do usuário).

---

## Phase 2: Integração SEI & Analisador de Caixa Coletiva

- [x] **T-201**: Criar os modelos em `src/SeiMonitor/Core/Models/`:
  - `ProcessoItem.cs` (ID, número formatado, tipo, descrição, contagem de docs, data/hora da última ação, autor, flags `IsReadLocally`, `AbertoPorTerceiro`, `DestinadoAoUsuario`).
  - `AndamentoItem.cs` (ID, data/hora, descrição, unidade, usuário).
  - `GrupoAcompanhamento.cs` (ID, nome, total de processos).
- [x] **T-202**: Implementar `src/SeiMonitor/Core/SeiClient.cs`:
  - Autenticação lazy no WSSEI (`POST autenticar`) e fallback na API da SEAD (`POST v1/orgaos/usuarios/login`).
  - Renovação de token transparente no status HTTP 401.
  - Injeção obrigatória do cabeçalho `unidade` e `token` nas requisições.
  - Sanitização preventiva do bug do Solr do mod-wssei: **descartar** `mensagem` e `exception` de respostas de erro para jamais expor dados do servidor.
- [x] **T-203**: Implementar consultas de Acompanhamento Especial e Caixa de Entrada no `SeiClient`:
  - Listagem dos grupos de Acompanhamento Especial da unidade.
  - Listagem de processos vinculados a um grupo.
  - Listagem dos processos abertos/recebidos na unidade ativa (Controle de Processos).
- [x] **T-204**: Implementar `src/SeiMonitor/Core/TextAnalyzer.cs`:
  - Detecção de visualização/abertura por colegas na GINFO (`AbertoPorTerceiro`).
  - Varredura de palavras-chave do usuário no título, especificação e texto do último despacho gerado (`DestinadoAoUsuario`).

---

## Phase 3: Motor de Polling Leve & Cache

- [x] **T-301**: Implementar `src/SeiMonitor/Core/SeiPoller.cs`:
  - Agendador com loop em background thread e `SemaphoreSlim` para prevenir concorrência.
  - Consulta de resumo leve: `documento/listar/{id}?start=0&limit=1` e `atividade/listar?procedimento={id}&start=0&limit=1`.
  - Comparação estrita com a contagem e data/hora salvas no SQLite: se iguais, pula a consulta de documentos sem consumir a API.
  - Se alterado, atualiza o cache, roda o `TextAnalyzer` e sinaliza `SyncCompleted`.
- [x] **T-302**: Integrar o controle de leitura local pessoal (`local_read_status`): novos processos ou processos com novos andamentos iniciam com `IsReadLocally = false`.

---

## Phase 4: Widgets de Apresentação (Desktop Fences & Taskbar)

- [x] **T-401**: Implementar `src/SeiMonitor/Ui/Theme.cs` com tema Dark Fluent, gradiente de urgência para andamentos e pincéis de UI.
- [x] **T-402**: Implementar `src/SeiMonitor/Ui/FenceWindow.xaml`:
  - Janela translúcida sem bordas com cantos arredondados (`AllowsTransparency=True`).
  - Cabeçalho arrastável contendo: título, Dropdown `ComboBox` de grupos de Acompanhamento Especial, botão de sincronização `🔄` e botão de recolhimento `▾ / ▸`.
  - 4 Pills de contagem: 📂 **MONITORADOS**, ⚡ **NOVOS ANDAMENTOS**, 🎯 **PARA MIM**, ⚠️ **ATENÇÃO GINFO**.
  - Lista de processos prioritários com badges coloridos e rolagem.
  - Alça `Thumb` de redimensionamento no rodapé.
- [x] **T-403**: Implementar `src/SeiMonitor/Ui/FenceWindow.xaml.cs`:
  - Lógica Win32 P/Invoke: `SetWindowLongPtr(GWLP_HWNDPARENT, progman)`, `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` e `SetWindowPos(HWND_BOTTOM)`.
  - `DispatcherTimer` de 1 segundo para manter o pin caso o Explorer reinicie.
  - Tratamento do arraste suave e salvamento de coordenadas físicas em SQLite.
  - Suporte a recolhimento em barra compacta de 50px.
- [x] **T-404**: Implementar `src/SeiMonitor/Ui/TaskbarWidgetWindow.xaml` e `.cs`:
  - Janela compacta acoplada na barra de tarefas (ao lado da bandeja de notificação), com rastreamento da posição da `Shell_TrayWnd`.
  - Letreiro digital em scrolling horizontal (marquee ticker) alimentado por push de eventos ("Novo processo recebido...", "Novo documento juntado...", "🎯 Processo destinado a você...").
  - Pausa na rolagem ao passar o mouse (`MouseEnter`) e acionamento da `TaskbarFlyoutWindow` ao clicar.
- [x] **T-405**: Implementar `src/SeiMonitor/Ui/TaskbarFlyoutWindow.xaml` e `.cs` (estilo Notícias e Clima do Windows):
  - Painel flutuante posicionado logo acima da barra de tarefas com saudação contextual, cards informativos de processos e fechamento automático em `Deactivated`.

---

## Phase 5: Painel Principal, Bandeja do Sistema e Detalhes

- [x] **T-501**: Implementar `src/SeiMonitor/App.xaml` e `src/SeiMonitor/App.xaml.cs`:
  - Instância única via Mutex nomeado `Local\SEI_Monitor_Desktop` e EventWaitHandle para reexibição da tela.
  - Ícone na bandeja do sistema (`NotifyIcon`) com menu de contexto: Abrir Painel, Sincronizar Agora, Alternar Widget, Sair.
- [x] **T-502**: Implementar `src/SeiMonitor/Ui/MainWindow.xaml` e `MainWindow.xaml.cs`:
  - Abas: **Visão Geral** (estatísticas e timeline), **Caixa de Entrada GINFO** (grid detalhado com filtros por "Destinado a Você" e "Aberto por Colega"), **Acompanhamento Especial** (visão agrupada), **Configurações** (credenciais, unidade, palavras-chave, opacidade) e **Logs**.
  - Botão "Marcar como Lido" para atualizar o status local do usuário.
- [x] **T-503**: Implementar `src/SeiMonitor/Ui/DetailWindow.xaml` e `DetailWindow.xaml.cs` para exibir a árvore de documentos e a linha do tempo completa de andamentos do processo.
- [x] **T-504**: Validar o fluxo de ponta a ponta com compilação sem warnings e testes de carga do cache (681 testes automatizados aprovados).
