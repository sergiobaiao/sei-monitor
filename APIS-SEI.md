# APIs do SEI! no Eli: WSSEI e API da SEAD

Como o Eli lê o SEI! do Piauí: as duas APIs REST, a autenticação por conta de serviço, a escolha de unidade (inclusive para abrir documento que está em outra unidade), paginação, erros, cache e o que cada leitura custa. Tudo é **somente leitura**.

Código: `backend/app/clients/sei_rest.py` (as duas APIs e a alternativa), `backend/app/services/sei_cache_service.py` (último estado do processo), `backend/app/clients/arquivos_sei.py` (cache dos arquivos) e `backend/app/services/sei_analise_service.py` (ciclo da IA). Requisitos: FR-021, FR-039, FR-044, FR-061 na [spec](specs/001-projects-processes-dashboard/spec.md).

> Este arquivo não traz host interno, usuário, senha, unidade real de processo nem número de processo. Os valores ficam só no `.env` de cada instalação.

## Visão geral

| | WSSEI (mod-wssei, API v2) | API da SEAD |
|---|---|---|
| Papel no Eli | Fonte principal | Alternativa, quando o WSSEI falha |
| Base | `SEI_WSSEI_BASE_URL` (termina em `/api/v2`) | `SEI_SEAD_BASE_URL` (`https://api.sei.pi.gov.br`) |
| Login | `POST autenticar` (form: `usuario`, `senha`, `orgao`) | `POST v1/orgaos/usuarios/login` (JSON: `Usuario`, `Senha`, `Orgao`) |
| Token | Cabeçalho `token` | Cabeçalho `token` |
| Unidade | Cabeçalho `unidade` em cada chamada | No caminho: `v1/unidades/{id}/...` |
| Órgão | Código numérico do órgão no SEI | Sigla (`PGE-PI`) |
| Nível de acesso | Sim (`status.nivelAcessoGlobal`) | Não informa: o Eli trata como sigiloso |
| Data do documento (FR-061) | Sim | Não |
| Resumo leve (total de documentos e último andamento) | Sim | Não |
| Custo por documento listado | ~0,4 s | ~2,7 s |
| Página | 100 itens | 10 itens (o gateway estoura perto de 60 s com mais) |

As duas autenticam **como um usuário do SEI**, então o Eli usa uma conta de serviço. Nenhuma das duas responde "este e-mail acessa este processo?": a regra de quem vê o quê fica no Eli (FR-010a e FR-021). Processo público aparece a todos do módulo; restrito e sigiloso só a quem tem `projetos_admin` (ou superadmin). Nível de acesso ausente vale como sigiloso (fail-closed).

A montagem fica em `criar_leitura_sei()`: WSSEI se configurado, com a SEAD como alternativa (`SeiComAlternativa`), e por cima o cache de arquivos (`FonteComCacheDeArquivos`) quando há armazém (S3 ou pasta). Se o WSSEI falhar por qualquer erro de integração, a mesma operação é tentada na SEAD; se as duas falharem, sobe o erro **do WSSEI**, que é o que diz a causa. `resumo` e `data_do_documento` só existem no WSSEI: sem ele, o ciclo lê o processo inteiro e a linha do tempo usa o histórico de fases.

O cliente SOAP antigo (`backend/app/clients/sei.py`, `SeiWS`) só entra quando nenhuma das APIs REST está configurada. É dele que vêm as exceções comuns (`SeiIndisponivel` etc.).

## Configuração

| Variável | Para quê |
|---|---|
| `SEI_WSSEI_BASE_URL`, `SEI_WSSEI_USUARIO`, `SEI_WSSEI_SENHA`, `SEI_WSSEI_ORGAO` | WSSEI. Sem as quatro, o WSSEI fica desligado |
| `SEI_UNIDADE` | Unidade de consulta (id numérico): de onde a pesquisa enxerga o processo e onde a SEAD lê por padrão |
| `SEI_SEAD_BASE_URL`, `SEI_SEAD_ORGAO` | SEAD. `SEI_SEAD_ORGAO` é a sigla, não o código do WSSEI |
| `SEI_SEAD_USUARIO`, `SEI_SEAD_SENHA` | Conta própria da SEAD (opcional; vazias, a SEAD usa a conta do WSSEI) |
| `SEI_TIMEOUT_S` | Tempo máximo por chamada (padrão 120 s) |
| `SEI_MAX_DOWNLOAD_BYTES` | Maior arquivo aceito (padrão 25 MB) |
| `SEI_CACHE_VALIDADE_HORAS` | Depois de quanto tempo o último estado guardado do processo fica "velho" e é relido (padrão 6 h) |
| `SEI_ARQUIVOS_DIR` / `ARMAZENAMENTO_S3_BUCKET` | Onde fica o cache dos arquivos (pasta local ou S3, FR-043) |

Credenciais nunca vão a log nem a mensagem de erro (INV-8). Os spans HTTP (logfire) não guardam cabeçalhos nem corpos.

## Autenticação

- O login é preguiçoso: só acontece na primeira chamada.
- Resposta **401** em qualquer chamada faz um novo login e uma nova tentativa. Um segundo 401 vira `SeiAcessoNegado`.
- **WSSEI:** o token vem em `data.token`. Sem token, acesso negado.
- **SEAD:** o corpo do login traz `Token`, `Login.IdUnidadeAtual` e a lista `Unidades` (formato conferido em 2026-10-20). Sem `SEI_UNIDADE`, a SEAD usa `IdUnidadeAtual` e, na falta dele, a primeira de `Unidades`.

O token do WSSEI vale para **todas as unidades** da conta. Trocar de unidade não exige novo login: basta mandar outro valor no cabeçalho `unidade` (veja a próxima seção).

## Unidade: de onde se lê e como abrir documento de outra unidade

No SEI, o que um usuário enxerga depende da **unidade** em que ele está. A conta de serviço tem acesso a várias unidades, mas cada leitura é feita "a partir de" uma delas. Ler da unidade errada dá "acesso negado" ou um documento marcado como não visualizável. Na tela do SEI, isso equivale a o usuário trocar de unidade no seletor do topo antes de abrir o processo ou o documento.

O Eli nunca troca a unidade "da sessão". Ele escolhe a unidade **por chamada**:
- **WSSEI:** cabeçalho `unidade: <id>` em cada requisição.
- **SEAD:** o id vai no caminho, `v1/unidades/<id>/procedimentos/...`.

### Passo a passo (WSSEI)

1. **Achar o processo pela unidade de consulta.** `GET processo/pesquisar?palavrasChave=<número>&start=0&limit=50` com `unidade: SEI_UNIDADE`.
   - A pesquisa é feita no Solr e só enxerga o processo a partir de uma unidade que tenha acesso a ele.
   - Ela devolve itens planos, inclusive documentos que citam o número.
   - O Eli fica com o item cujo `protocoloFormatadoProcedimento` (ou `atributos.numero`) é exatamente o número procurado. Se nenhum bater, `ProcessoNaoEncontrado`.
   - Pega o id em `idProcedimento`.
2. **Ver onde o processo está aberto.** `GET processo/{id}` (unidade de consulta). Em `atributos.dadosAbertura.unidades` vêm as unidades onde o processo está aberto, na ordem do SEI.
3. **Montar a lista de unidades a tentar:** as unidades abertas, na ordem, e por último a unidade de consulta, sem repetir.
4. **Listar os documentos tentando cada unidade.** `GET documento/listar/{id}` em cada unidade da lista, até uma permitir. A conta pode ter acesso negado em algumas das unidades abertas; nesse caso passa para a próxima. A unidade que listou é a "unidade de leitura" do processo, e os andamentos (`atividade/listar`) são lidos dela também.
5. **Abrir na unidade que gerou os documentos que vieram bloqueados.**
   - Documento restrito ou ainda sem assinatura só abre na unidade que o gerou: o campo `idUnidade` do documento (conferido em 2026-10-21).
   - Para cada documento que veio com `status.podeVisualizarDocumento != "S"` e não está cancelado, o Eli junta os `idUnidade` diferentes da unidade de leitura.
   - Lista o processo de novo a partir de cada uma delas, uma chamada por unidade.
   - O documento que lá aparece como visualizável passa a valer, e a unidade dele é guardada.
   - Falha nessa releitura mantém o documento como "sem acesso", sem erro.
6. **Ler o documento sempre pela unidade guardada.** Conteúdo, download e data (FR-061) usam o cabeçalho `unidade` daquele documento (`_unidade_do_documento`). Isso vale para documentos de unidades diferentes dentro do mesmo processo.

Resumo da ordem: **unidades onde o processo está aberto → unidade de consulta → para cada documento bloqueado, a unidade que o gerou.**

O que **não** se resolve trocando de unidade:
- Documento que a conta de serviço não pode ver em nenhuma unidade continua bloqueado. A permissão é do SEI, e quem concede é o NIT.
- Processo sigiloso exige credencial de acesso no SEI para a própria conta.

### SEAD

1. **Consulta.** `GET v1/unidades/{unidade}/procedimentos/consulta?protocolo_procedimento=<número>&sinal_unidades_procedimento_aberto=S`, feita na unidade da instalação. Em `UnidadesProcedimentoAberto[].Unidade.IdUnidade` vêm as unidades onde o processo está aberto.
2. **Ordem de tentativa:** a unidade da instalação primeiro, se o processo estiver aberto nela, e depois as outras unidades abertas. A primeira da lista da SEAD costuma ser de outro órgão, sem acesso para a conta. Se não vier nenhuma unidade aberta, vale só a da instalação.
3. **Documentos.** `GET v1/unidades/{unidade}/procedimentos/documentos` em cada unidade. Resposta **422** com "Acesso negado a este recurso" significa que a conta não enxerga o processo ali; o Eli tenta a próxima. Se nenhuma unidade permitir, `SeiAcessoNegado`.
4. **Andamentos e downloads** usam a unidade que listou os documentos (`v1/unidades/{unidade}/documentos/baixar?protocolo_documento=<número do documento>`).

A SEAD não tem o passo 5 do WSSEI: ela não informa a unidade que gerou o documento.

## Operações e endpoints

### WSSEI

| Operação no Eli | Chamada | Observação |
|---|---|---|
| Achar o processo | `GET processo/pesquisar?palavrasChave=&start=0&limit=50` | Solr; unidade de consulta |
| Detalhe e nível de acesso | `GET processo/{id}` | `atributos`: `tipoProcesso`, `descricao`, `unidade.sigla`, `status.nivelAcessoGlobal` (0 público, 1 restrito, 2 sigiloso; outro valor vale como sigiloso), `dadosAbertura.unidades` |
| Listar documentos | `GET documento/listar/{id}?start=&limit=` | `start` é o **número da página** (0, 1, 2…); o `total` vem na primeira página |
| Andamentos | `GET atividade/listar?procedimento={id}&start=&limit=` | Do mais recente ao mais antigo (conferido em 2026-10-27); `data` em `dd/mm/aaaa`, `hora` em `hh:mm:ss` |
| Conteúdo de documento interno (HTML) | `GET documento/{id}/interno/visualizar` | `data` ou `data.html` com o HTML |
| Arquivo de documento externo | `GET documento/baixar/anexo/{id}` | Binário; resposta JSON aqui é erro |
| Data do documento (FR-061) | `GET documento/externo/consultar/{id}` ou `documento/interno/consultar/{id}` | Externo (`tipoDocumento = X`): `anexo.dataInclusao` (`dd/mm/aaaa hh:mm:ss`); gerado no SEI (`I`, `A`): `dataElaboracao` (`dd/mm/aaaa`) |
| Catálogo de tipos de documento (FR-044) | `GET documento/tipo/pesquisar?filter=&start=&limit=` | Aqui `start` é **deslocamento** (0, 100, 200…), diferente das outras listagens |

Campos do documento usados (`atributos` de cada item de `documento/listar`):

| Campo | Uso |
|---|---|
| `id` / `idProtocolo` | Id do documento |
| `protocoloFormatado` | Número do documento |
| `nomeComposto`, `tipo` | Nome e tipo exibidos |
| `mimeType` | Formato; HTML sem arquivo = documento interno |
| `nome` | Nome do arquivo do documento externo |
| `tipoDocumento` | Origem: X externo, I e A gerados no SEI |
| `idUnidade` | Unidade que gerou o documento (passo 5 acima) |
| `status.podeVisualizarDocumento` | `S` = a conta pode abrir desta unidade |
| `status.documentoCancelado` | `S` = cancelado, fica fora da análise |
| `status.documentoSigiloso` | `S` = indicador visual na aba Documentos |

### API da SEAD

| Operação | Chamada (sempre sob `v1/unidades/{unidade}/`) | Campos |
|---|---|---|
| Consulta | `procedimentos/consulta?protocolo_procedimento=&sinal_unidades_procedimento_aberto=S` | `IdProcedimento`, `ProcedimentoFormatado`, `TipoProcedimento.Nome`, `Especificacao`, `UnidadesProcedimentoAberto` |
| Documentos | `procedimentos/documentos?protocolo_procedimento=&pagina=&quantidade=10` | `Documentos[]`: `IdDocumento`, `DocumentoFormatado`, `Serie.Nome`, `Numero` |
| Andamentos | `procedimentos/andamentos?protocolo_procedimento=&pagina=&quantidade=10` | `Andamentos[]`: `DataHora`, `Descricao`, `Unidade.Sigla` |
| Download | `documentos/baixar?protocolo_documento=<número do documento>` | Binário |
| Tipos de documento | `documentos/tipos` | `Tipos[]`: `IdSerie`, `Nome`; o filtro por nome é feito no Eli |

## Paginação

- **WSSEI:**
  - Páginas de 100 itens, até 100 páginas.
  - Para quando a página vem vazia, quando já leu `total` itens ou quando a página veio com menos de 100.
  - Sem resultado, o WSSEI responde `sucesso: true` e `total`, mas **omite `data`**; o Eli trata como lista vazia.
- **SEAD:**
  - Páginas de 10, com `pagina` começando em 1.
  - A SEAD devolve **um item a menos** que o pedido por página. Por isso o fim vem do total (`Info.TotalItens`), e não do tamanho da página.
  - Páginas maiores fazem o gateway dela estourar perto de 60 s (cerca de 2,7 s por documento).

## Erros

| Situação | Como chega | Erro no Eli (`codigo`) |
|---|---|---|
| Rede, timeout, DNS | Exceção de transporte | `SEI_INDISPONIVEL` |
| 401 ou 403 (após uma renovação de token) | HTTP | `SEI_ACESSO_NEGADO` |
| WSSEI: conta sem acesso ao recurso naquela unidade | **HTTP 200** com `sucesso: false` e "Acesso negado a este recurso (...)" (conferido em 2026-10-22) | `SEI_ACESSO_NEGADO` |
| SEAD: conta sem acesso ao processo naquela unidade | HTTP 422 com "Acesso negado a este recurso" | `SEI_ACESSO_NEGADO` |
| 404 ou processo fora da pesquisa | HTTP / pesquisa vazia | `PROCESSO_NAO_ENCONTRADO` |
| Outro status não 200 | HTTP | `SEI_INDISPONIVEL` |
| JSON inválido, `sucesso` diferente de `true`, formato inesperado, documento vazio ou maior que o limite | Corpo | `SEI_RESPOSTA_INVALIDA` |
| Nenhuma API configurada | Configuração | `SEI_NAO_CONFIGURADO` (503) |

Os "acesso negado" por unidade são tratados como **tente a próxima unidade** nos passos de listagem. Só viram erro quando nenhuma unidade permite.

Falha de leitura nunca apaga o último estado conhecido: a aba SEI mostra o que está guardado e a causa (FR-013).

### Problema conhecido do mod-wssei (relatado à mantenedora)

Quando a consulta ao Solr falha, `GET processo/pesquisar` responde HTTP 200 com `sucesso: false`. Em `mensagem` e `exception`, vêm a URL do Solr **com usuário e senha** e o stack trace do servidor. O mesmo formatador de erro (`formataRetornoErroREST`) é usado no módulo inteiro, então qualquer exceção pode expor detalhes internos.

Consequências para o Eli:
- O Eli nunca registra a `mensagem` nem a `exception` do WSSEI em log, só o código do erro.
- Ao depurar à mão, não cole respostas de erro do WSSEI em chamados, chats ou issues.

O texto do relato fica nas notas da sessão (`bug-report-mod-wssei.md`). Antes de publicar, a ETIPI precisa confirmar a troca da senha do Solr.

## Leitura, cache e custo

- **Ler um processo inteiro é caro.** São centenas de documentos a ~0,4 s cada pelo WSSEI, ou ~2,7 s pela SEAD. Por isso a aba SEI nunca lê na hora:
  - Mostra o último estado guardado (`sei_processo_cache`: só nomes, tipos, datas e andamentos, nunca o conteúdo).
  - Dispara a releitura em segundo plano quando o estado está "velho".
  - Uma leitura presa há mais de 15 minutos é considerada travada.
  - O administrador pode refazê-la depois de 2 minutos.
- **Inclusão de item:** só `existe_processo` (a pesquisa), sem ler documentos.
- **Ciclo da IA (FR-061):** primeiro o **resumo leve**, com `documento/listar` e `atividade/listar` com `limit=1`, que trazem o total e o último andamento. O processo inteiro só é lido quando a quantidade de documentos muda, há leitura pendente ou falta a data de algum documento de etapa (`item.sei_datas_pendentes`).
- **Data de cada documento:** pedida ao SEI uma vez e guardada no item.
- **Arquivos (FR-039, FR-043):**
  - O arquivo baixado fica em cache no S3 (`sei/arquivos/<id do documento>`) ou na pasta local, e serve ao download e à análise.
  - Documento assinado não muda, então o cache não expira (retenção: T123, decisão do NIT).
  - A chave é só o id do documento, validado por expressão regular.
- **Análise de IA:** lê HTML, PDF e texto. Documento cancelado ou em outro formato (imagem, planilha, Word) fica de fora. Processo não público marca todos os documentos como sigilosos.
- **Conteúdo de documento:** nunca vai ao banco nem a log (INV-3).

## Testes

Os testes nunca chamam o SEI real:
- `backend/tests/fakes/` e os transportes falsos do `httpx` simulam as respostas, inclusive os formatos conferidos em produção que estão citados acima.
- Para mudar o cliente, escreva primeiro o teste com a resposta gravada (TDD, regra do projeto).

## Conferindo em produção (só leitura)

Para olhar uma resposta real, faça pelo container `api` da instalação, com as credenciais do próprio `.env`, sem imprimi-las:
- Imprima só metadados: status, `sucesso`, `total` e nomes de campos.
- Nunca imprima conteúdo de documento, `mensagem` ou `exception` de erro, nem o token.
- Processo sigiloso: só metadados, sempre.
