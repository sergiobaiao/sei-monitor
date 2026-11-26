# Feature Specification: SEI Desktop Monitor com Widget Fences e Rastreamento de Caixa Compartilhada

**Feature Branch**: `001-sei-desktop-monitor`  
**Created**: 2026-10-29  
**Status**: Ready  
**Input**: Monitor de processos do SEI! com widget fixado na Área de Trabalho (estilo Fences), monitoramento de Acompanhamento Especial com seletor de grupos, detecção leve de novos andamentos por contagem de documentos/cache, e heurística de caixa compartilhada (GINFO) para evitar processos "passados batido".

---

## User Scenarios & Testing

### User Story 1 — Monitoramento de Acompanhamento Especial no Widget de Desktop (Priority: P1)

Como servidor da unidade (ex.: GINFO), quero visualizar na Área de Trabalho um widget translúcido fixado que mostre os processos que acompanho no SEI, permitindo alternar rapidamente entre grupos temáticos de Acompanhamento Especial, sem que a janela seja minimizada ao teclar `Win + D` ou fique na frente de outros programas.

**Why this priority**: É a interface visual central e diferencial do produto, adaptando a técnica de sucesso do projeto `pol`.

**Independent Test**: Pode ser testado abrindo o aplicativo, selecionando um grupo de Acompanhamento Especial no Dropdown e verificando se os processos aparecem no widget mantido na camada do desktop (atrás das outras janelas e visível no `Win + D`).

**Acceptance Scenarios**:
1. **Given** o widget aberto na Área de Trabalho, **When** o usuário tecla `Win + D`, **Then** o widget continua visível no desktop e não é minimizado.
2. **Given** o widget exibindo processos, **When** o usuário clica no Dropdown de grupos no cabeçalho e seleciona "Licitações", **Then** a pill `MONITORADOS` e a lista exibem apenas os processos vinculados ao grupo "Licitações".
3. **Given** o usuário arrasta o widget pelo cabeçalho ou redimensiona pela alça inferior direita, **When** o aplicativo é reiniciado, **Then** as coordenadas físicas e dimensões exatas são restauradas.

---

### User Story 2 — Detecção Leve de Novos Andamentos com Cache Local (Priority: P1)

Como usuário do monitor, quero que o sistema identifique quando novos documentos forem juntados ou quando houver nova tramitação nos processos monitorados e na caixa de entrada, sem sobrecarregar a API do SEI e sem gerar lentidão.

**Why this priority**: É o motor de sincronização principal. A leitura completa no SEI é custosa (~0,4s a ~2,7s por documento); o monitor precisa usar *light polling*.

**Independent Test**: Simular alteração na contagem de documentos de um processo e verificar se o ciclo periódico com `limit=1` detecta a divergência e atualiza a UI sem baixar o histórico completo dos processos não alterados.

**Acceptance Scenarios**:
1. **Given** um processo já em cache local com 15 documentos e última movimentação em 28/10/2026 14:00, **When** o poller executa `documento/listar?limit=1` e `atividade/listar?limit=1` e obtém os mesmos valores, **Then** o monitor não dispara nenhuma requisição adicional para esse processo.
2. **Given** que um novo documento foi inserido no processo no SEI (total passa para 16), **When** o poller executa a verificação leve, **Then** detecta a divergência, busca o novo documento/andamento e incrementa a pill `NOVOS ANDAMENTOS`.

---

### User Story 3 — Proteção da Caixa Coletiva GINFO contra Processos "Passados Batido" (Priority: P1)

Como integrante de uma equipe com caixa de entrada compartilhada no SEI (ex.: GINFO), quero saber quais processos chegaram ou foram movimentados no meu setor, saber se foram abertos por um colega sem despacho posterior, e receber destaque imediato quando um processo for destinado nominalmente a mim, para que nenhum processo passe batido.

**Why this priority**: Resolve uma dor diária crítica de setores públicos: colegas abrem processos na caixa web do SEI, o ícone perde o destaque de "não lido" no setor inteiro e o verdadeiro destinatário nunca é avisado.

**Independent Test**: Inserir na caixa de entrada um processo onde o último documento cita "Ao servidor Victor" e cuja última ação no SEI foi realizada por outro usuário. O monitor deve destacar o processo como "🎯 Destinado a Você" e "⚠️ Aberto por Terceiro".

**Acceptance Scenarios**:
1. **Given** um processo novo na caixa de entrada da GINFO que um colega abriu no SEI web, **When** o usuário abre o SEI Monitor na sua máquina, **Then** o processo continua destacado em vermelho/negrito com flag de leitura local `IsReadLocally = false`.
2. **Given** um processo na caixa cujo último andamento foi "Processo aberto na unidade por Servidor B", **When** o usuário Victor visualiza o processo no monitor, **Then** um badge indica `👁️ Aberto por: Servidor B (sem conclusão)`.
3. **Given** que as palavras-chave do usuário contêm "Victor" e o último despacho diz "Ao servidor Victor para manifestação", **When** o monitor processa o documento, **Then** o badge `🎯 Destinado a Você` é atribuído e a pill `PARA MIM` é incrementada.

---

### User Story 4 — Painel de Controle e Notificações na Bandeja (Priority: P2)

Como usuário, quero poder abrir uma janela completa detalhada a partir do ícone da bandeja do sistema para visualizar a linha do tempo completa dos processos, marcar itens como lidos e configurar credenciais e parâmetros.

**Independent Test**: Clicar duas vezes no ícone da bandeja, abrir o painel principal, filtrar processos da GINFO e clicar em "Marcar como Lido".

**Acceptance Scenarios**:
1. **Given** o aplicativo em execução em segundo plano, **When** o usuário clica com o botão esquerdo no ícone da bandeja, **Then** a janela principal é exibida e focada.
2. **Given** um processo com novos andamentos na caixa de entrada, **When** o usuário clica no botão "Marcar como Lido" no painel, **Then** o contador de pendências locais do processo é zerado.

---

### User Story 5 — Widget da Taskbar com Letreiro em Scrolling Horizontal e Flyout Estilo Notícias e Clima (Priority: P1)

Como servidor público que passa o dia com várias janelas abertas sobrepostas à Área de Trabalho, quero ver o status do SEI diretamente na **Barra de Tarefas do Windows** como uma pílula informativa integrada com **texto em rolagem horizontal suave (ticker / letreiro digital)** exibindo as últimas atualizações em tempo real (ex.: *"⚡ Novo processo recebido — 00001.000000/2026-00"*, *"📄 Novo documento juntado no processo..."*, *"🎯 Processo destinado a você: ..."*), e, ao clicar nele, abrir o **painel flyout suspenso** ancorado logo acima da barra de tarefas com os cards detalhados.

**Why this priority**: O usuário muitas vezes tem janelas maximizadas cobrindo o desktop; o widget da taskbar com letreiro deslizante garante visibilidade periférica dinâmica das novidades sem exigir minimizar os programas em uso.

**Independent Test**: Simular o recebimento de um push/evento de novo documento e novo processo, validar que o widget na barra de tarefas inicia o scrolling horizontal suave do texto da mensagem e, ao passar o mouse ou clicar, pausa e expande o flyout suspenso ancorado.

**Acceptance Scenarios**:
1. **Given** o widget acoplado na barra de tarefas, **When** o motor do poller detecta um novo processo ou juntada de documento, **Then** o texto da atualização entra no ticker em rolagem horizontal contínua e suave (ex.: *"⚡ Novo processo recebido — 00001.000000/2026-00 — GINFO"*).
2. **Given** o texto em rolagem na barra de tarefas, **When** o usuário posiciona o cursor sobre o widget, **Then** a rolagem pode pausar temporariamente para facilitar a leitura.
3. **Given** o widget na barra de tarefas, **When** o usuário clica sobre ele, **Then** abre o painel flyout suspenso ancorado imediatamente acima da taskbar com saudação personalizada e cards detalhados de cada processo atualizado.

---

## Requisitos do Sistema

### Requisitos Funcionais (FR)
- **FR-01:** Autenticação no WSSEI (mod-wssei v2) como primário com cabeçalhos `token` e `unidade` por requisição.
- **FR-02:** Fallback automático para a API da SEAD (`https://api.sei.pi.gov.br`) caso o WSSEI retorne erro de integração/indisponibilidade.
- **FR-03:** Renovação automática de token em caso de HTTP 401.
- **FR-04:** Sanitização obrigatória de erros do Solr no WSSEI (proibido registrar `mensagem` e `exception` em log).
- **FR-05:** Consulta e listagem dos grupos de Acompanhamento Especial da unidade ativa.
- **FR-06:** Seletor Dropdown de Grupos no widget desktop e no painel para filtrar os "MONITORADOS".
- **FR-07:** Verificação periódica da Caixa de Entrada da unidade e dos processos em Acompanhamento Especial.
- **FR-08:** Mecanismo de verificação leve (*light polling*) com `limit=1` comparando com o cache local do SQLite.
- **FR-09:** Manutenção de estado de leitura local (`IsReadLocallyByMe`) por processo no banco SQLite.
- **FR-10:** Detecção de processos abertos por terceiros na unidade através da autoria da última atividade.
- **FR-11:** Heurística de destinatário por palavras-chave cadastradas pelo usuário aplicadas ao título e texto do despacho.
- **FR-12:** Widget desktop WPF sem bordas, translúcido, com Owner = `Progman`, `WS_EX_TOOLWINDOW`, `WS_EX_NOACTIVATE` e `HWND_BOTTOM`.
- **FR-13:** Persistência local de coordenadas (pixels físicos), tamanho, opacidade e grupo selecionado.
- **FR-14:** Ícone na bandeja do sistema (`NotifyIcon`) com menu de contexto e instância única via Mutex.
- **FR-15:** Widget na Barra de Tarefas (`TaskbarWidgetWindow`) com visual integrado nativo e janela suspensa flyout (`TaskbarFlyoutWindow`) estilo Windows Widgets ancorada na barra.

### Requisitos Não-Funcionais (NFR)
- **NFR-01:** Consumo de CPU < 1% e memória RAM < 80 MB em repouso.
- **NFR-02:** Tolerância a falhas de rede com retentativas suaves e sem travar a interface.
- **NFR-03:** Segurança: senhas armazenadas exclusivamente no Windows Credential Manager.
