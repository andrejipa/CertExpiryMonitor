# Changelog

Todas as mudanças notáveis neste projeto. Formato baseado em
[Keep a Changelog](https://keepachangelog.com/pt-BR/1.0.0/).

---

## [1.0.9] — 2026-07-10

### Corrigido

- **Falhas de leitura nao viram defaults salvaveis**: settings/state agora usam `TryLoad`; timeout, IO, acesso, JSON ou DPAPI invalidos abortam a operacao sem sobrescrever dados existentes.
- **Leitura X.509 explicita**: falha total ou parcial no `CurrentUser\\My` nao consolida `LastCheckDate` nem o hash do snapshot e agenda retry seguro.
- **Ciclo de notificacao testavel**: `NotificationCheckCoordinator` centraliza check, tentativa de aviso, `MarkNotified` e persistencia final fora do WinForms.
- **Retencao SQLite fora do hot path**: inserts de diagnostico nao executam mais retencao/checkpoint/VACUUM; manutencao roda em worker no startup e depois do check diario.

### Testes

- Regressões deterministicas para timeout/IO dos stores, falha total/parcial X.509, matriz do coordenador e manutencao SQLite explicita.
- **Mutation testing ampliado**: o filtro inclui `NotificationCheckCoordinator`, `CertificateReader` e os resultados explícitos do check, com sobreviventes funcionais críticos eliminados.

### Validação

- Build Release sem warnings, 405 testes passando e cobertura `44,91%` de linhas / `52,47%` de branches; coordenador com `100%` de linhas / `96,42%` de branches.
- Stryker final `70,08%`; na contagem normalizada do relatório HTML, `68,91%` contra baseline preservado `67,90%`.
- Publish single-file `win-x64` com `75,52 MiB`; instalador Inno Setup com `70,31 MiB`. O SHA-256 definitivo acompanha a release em `SHA256SUMS.txt`, pois o artefato incorpora o `SourceRevisionId` do commit publicado.
- BugHunt E2E `-Maximum -KeepArtifacts` concluído em `artifacts\bughunt\20260710-132152`, incluindo SQLite, logs, PNGs e UIA; neste PC o Task Scheduler negou acesso, o fallback `HKCU\Run` foi validado e o popup próprio foi exercitado via UI Automation sem aviso duplicado.

---

## [1.0.8] — 2026-05-13

### Adicionado

- **Diagnóstico estruturado em SQLite local**: novo `diagnostics.db` registra eventos técnicos mínimos e observações redigidas de certificados para análise posterior em PCs de campo, sem substituir `settings.json`, `certificate-state.json`, `telemetry.json` ou `monitor.log`.
- **Exportação inclui snapshot consultável do SQLite**: o `.zip` de diagnóstico agora inclui `diagnostics.db`; se o banco estiver bloqueado, a exportação continua e registra o erro em `copy-errors.txt`.

### Corrigido

- **Settings envelope tolera `version` ausente ou malformado**: `JsonSettingsStore` agora detecta a propriedade `settings` por nome case-insensitive e desserializa somente esse bloco. Arquivos como `{ "settings": {...} }` ou `{ "version": "1", "settings": {...} }` não perdem mais as configurações como se fossem formato legado.
- **State envelope tolera `version` ausente ou malformado**: `JsonStateStore` agora aproveita `records` válido mesmo quando a versão foi editada manualmente como string, sem renomear o estado para `.corrupt-*`.
- **Telemetria tolera `version` malformado**: `telemetry.json` com `"version": "1"` mantém contadores acumulados e ainda aceita incrementos futuros, em vez de abortar a atualização como se o arquivo estivesse corrompido.
- **CaptureUi captura quando `MainWindowHandle` fica zero**: o script agora usa `NativeWindowHandle` obtido via UIA como fallback para `PrintWindow`. O BugHunt encontrou janela acessível por UIA, mas sem handle principal exposto pelo processo.
- **Hash de snapshot ignora duplicatas de thumbprint**: `CertificateCheckService.ComputeSnapshotHash` agora consolida certificados pelo thumbprint normalizado, alinhando o skip diário à mesma regra usada pelo evaluator. Duplicatas no store não fazem o app reprocessar como se o conjunto tivesse mudado.
- **Documento com texto embutido não é mascarado como CPF/CNPJ**: `CertificateDocumentHelpers.FormatDocument` só formata entradas compostas por dígitos, pontuação comum e espaços. Textos como `CPF 12345678909` permanecem visíveis como entrada malformada.
- **Titular malformado com dois-pontos no início não vira nome vazio**: `CertificateDocumentHelpers.ParseHolder` agora mantém entradas como `:12345678909` como nome malformado, sem inventar documento para um titular vazio.
- **CN com espaços antes do atributo é reconhecido**: `GetCommonNameFallback` agora aceita DNs com whitespace inicial antes de `CN=`, mantendo a regra de não capturar `CN=` embutido dentro de outro atributo.
- **BugHunt tolera task/atalho residual da própria rodada**: `scripts/Run-BugHunt.ps1` remove resíduos que apontam para `CertExpiryMonitor-BugHunt` antes do snapshot, aguarda processos encerrarem antes de remover a pasta e trata `schtasks` ausente por exit code, sem quebrar cleanup por stderr.
- **Diagnóstico mostra startup apontando para caminho antigo**: o pacote exportado agora grava `TaskSchedulerMatchesCurrentExecutable` e `RegistryMatchesCurrentExecutable`, facilitando identificar PCs onde Task Scheduler/HKCU ainda apontam para outro executável.
- **Toast em máquina com atalho antigo/quebrado**: o app agora recria sempre o atalho do Menu Iniciar usado pelo Windows Toast, garantindo `AppUserModelID` e caminho do executável atualizados. Antes, um `.lnk` existente mais novo que o `.exe` podia ser aceito como válido mesmo sem o AppUserModelID correto.
- **BugHunt mais seguro**: o certificado sintético criado em `scripts/Run-BugHunt.ps1` agora usa chave privada não exportável. O teste continua removendo por thumbprint no cleanup, mas reduz o impacto caso a rodada seja interrompida fora do fluxo normal.
- **Higiene de fonte**: removido byte NUL literal de `StressTests.cs` (`[InlineData("\0")]` agora usa escape textual). Isso evita que o Git trate o arquivo de teste como binário e melhora revisão/diff.
- **Desinstalação manual mais segura**: `scripts/Uninstall-CurrentUser.ps1` agora recusa `-InstallDirectory` fora de `%LOCALAPPDATA%\Programs` antes de executar `Remove-Item -Recurse -Force`.
- **Retry de notificação obrigatório**: o app não grava mais `LastCheckDate`/hash antes de saber se a notificação foi exibida. Se o Windows Toast e o fallback falharem, o fingerprint do check é limpo para a próxima rodada tentar de novo no mesmo dia.
- **BugHunt mais seguro**: o cleanup do `scripts/Run-BugHunt.ps1` agora valida fronteira de diretório com separador antes de qualquer remoção recursiva.
- **Log JSONL consistente**: quando `LogFormat=Json`, o primeiro log de startup agora respeita JSON antes de escrever `monitor.log`. O BugHunt encontrou mistura de primeira linha texto + linhas JSON.
- **Instalação remota sem hang**: `scripts/Install-FromUrl.ps1` não usa mais `Start-Process -Wait` para o setup. Agora espera apenas o processo direto do instalador, com timeout, evitando travar quando o setup inicia o app residente na bandeja.
- **Toast do Windows mais forte e sem som indevido**: o XML agora usa cenário `urgent` sem reintroduzir botões visíveis, e respeita "Tocar som" gerando `<audio silent="true"/>` quando o usuário desativa som.
- **Clipping no topo da tela de certificados**: o painel de resumo agora tem altura calculada a partir dos cards, filtro e padding, evitando cortar textos em resoluções/DPI menores.
- **Telemetry anti-DoS**: `telemetry.json` agora tem size guard de 1 MB, preserva arquivo gigante como `.corrupt-*` e evita ler payload local inflado no startup.
- **Configurações não rearmam aviso sem mudança real**: salvar a tela sem alterar o horário diário não limpa mais `LastCheckDate` nem força nova notificação no mesmo dia.
- **Remoção de certificados com alerta correto**: certificados a vencer (`Critical`/`Warning`) agora entram no aviso reforçado de remoção porque ainda podem ser usados para assinatura.
- **Diagnóstico mais resiliente**: exportação continua se `monitor.log` estiver bloqueado, registra `logs/copy-errors.txt` no zip e mostra erro ao usuário se o pacote não puder ser gerado.
- **Diagnóstico com telemetria bloqueada**: `telemetry.json` agora também é opcional/best-effort no pacote, com `copy-errors.txt` quando o arquivo estiver travado.
- **Startup/uninstall sem resíduos**: o app limpa `HKCU\Run` quando Task Scheduler já está correto, e o uninstall Inno remove também o fallback de registro.
- **Scripts de instalação mais defensivos**: uninstall manual só remove a pasta dedicada do app; install manual não escolhe o primeiro exe recursivo; install remoto falha se outra instância fora da instalação atual estiver segurando o mutex.
- **Instalador sem diretório customizado**: `DisableDirPage=yes` impede escolher uma pasta reaproveitada e reduz risco de uninstall apagar arquivos alheios.
- **Thumbprint resiliente a caracteres invisíveis**: ao carregar estado ou processar ações de toast, o app remove whitespace e marcas invisíveis comuns (`LRM`/`RLM`/`BOM`) antes de comparar thumbprints.
- **Instalação manual sem resíduo em falha cedo**: `scripts/Install-CurrentUser.ps1` valida origem e diretório dedicado antes de criar `%LOCALAPPDATA%\Programs\CertExpiryMonitor`, recusando destino fora do caminho esperado.
- **Preservação de JSON corrompido sem sobrescrita**: `settings.json` e `certificate-state.json` agora usam sufixo único ao renomear para `.corrupt-*`, evitando perder evidência quando duas falhas acontecem no mesmo segundo.
- **Plano de notificação sem estado obsoleto**: `CertificateCheckService.LastPlan` é limpo em skips, falhas e verificações sem itens, evitando ações tardias sobre um plano antigo.
- **Thresholds sempre normalizados no evaluator**: `ExpiryEvaluator` normaliza thresholds recebidos diretamente, sem depender de todos os chamadores fazerem isso antes.
- **Diagnóstico de startup mais tolerante**: parser do CSV do `schtasks` agora ignora linhas de preâmbulo antes do cabeçalho e extrai corretamente `Task To Run`.
- **CN de certificado com vírgula escapada**: fallback de `CertificateDocumentHelpers.GetCommonNameFallback` agora preserva valores como `CN=EMPRESA\, FILIAL`, evitando truncar titular quando `SimpleName` não estiver disponível.
- **Telemetria corrompida sem sobrescrita**: `telemetry.json.corrupt-*` também usa sufixo único, alinhado a settings/state e preservando evidências de falhas repetidas.
- **Scripts manuais param só a instalação alvo**: install/uninstall manuais agora encerram apenas o processo cujo caminho é exatamente `%LOCALAPPDATA%\Programs\CertExpiryMonitor\CertExpiryMonitor.exe`, evitando matar builds de desenvolvimento ou outra cópia em teste.
- **Diagnóstico fora da pasta ativa do app**: exportação de `.zip` agora recusa destino dentro de `%LOCALAPPDATA%\CertExpiryMonitor`, evitando misturar pacotes gerados com dados/logs ativos.
- **Persistência resiliente a pasta removida**: logger, settings/state stores e telemetria recriam `%LOCALAPPDATA%\CertExpiryMonitor` antes de gravar, evitando falha se a pasta de dados for apagada durante o uso.
- **Falha de persistência não expõe plano inválido**: `CertificateCheckService` agora só publica `LastPlan` e atualiza data/hash depois que `certificate-state.json` foi salvo com sucesso.
- **Settings corrompido com `thresholds:null` não derruba monitoramento**: runtime normaliza thresholds nulos para o padrão seguro antes de avaliar certificados.
- **Mudança de faixas reavalia no mesmo dia**: salvar novos thresholds limpa `LastCheckDate`/hash e rearma a próxima verificação, evitando que o hash antigo esconda certificados que passaram a entrar na faixa.
- **Salvar configurações sem estado parcial**: a tela de configurações agora persiste horário, som, faixas, log, EventLog e telemetria em uma única gravação; se falhar, a UI não mostra sucesso nem aplica mudança parcial como se estivesse salva.
- **Mudança de faixas reavalia imediatamente**: alterar thresholds agenda uma verificação em 1 segundo, mesmo se o horário diário já passou; `_settings` em memória só troca depois que `settings.json` foi salvo.
- **Retry de rechecagem imediata**: se a verificação forçada por mudança de faixas não conseguir rodar por concorrência, o timer rearma a tentativa em 1 segundo sem perder o reminder forçado.
- **Retry imediato não é sobrescrito pelo agendamento diário**: `OnTimerTick` agora evita chamar `ScheduleNextDailyCheck()` no `finally` quando já armou retry de 1 segundo.
- **Toast desabilitado pelo Windows aciona fallback**: antes `ToastNotificationManager.Show()` era tratado como sucesso mesmo se `NotificationSetting` estivesse desabilitado por usuário/política. Agora o app detecta isso e retorna ao popup próprio.
- **Notificação exibida sem estado salvo não consolida o dia**: `MarkNotified` agora retorna sucesso/falha; se `certificate-state.json` não salvar, o app não persiste `LastCheckDate`/hash como se estivesse tudo certo.
- **Startup só é persistido depois de aplicar Task Scheduler/HKCU**: alternar "Iniciar com Windows" só salva `settings.json` após registro/remoção bem-sucedido, com rollback best-effort se o save posterior falhar.
- **Scripts não derrubam instâncias alheias**: `CaptureUi.ps1`, `StressUiCapture.ps1` e install manual agora recusam/limitam processos fora do executável alvo; o Inno também para apenas a instância instalada pelo caminho esperado.
- **BugHunt restaura argumentos originais**: cleanup de `scripts/Run-BugHunt.ps1` reinicia processos pré-existentes com a linha de comando original, em vez de forçar tudo como `--background`.
- **BugHunt respeita fronteira de diretório ao restaurar processos**: processos em pastas com prefixo parecido, como `CertExpiryMonitor-BugHunt-Old`, não são confundidos com a instalação temporária da rodada.
- **Instalação manual espera o processo sair**: `Install-CurrentUser.ps1` agora aguarda a instância instalada encerrar antes de copiar arquivos, evitando falha intermitente por `.exe` ainda bloqueado.
- **Desinstalação manual espera o processo sair**: `Uninstall-CurrentUser.ps1` agora aguarda a instância instalada encerrar antes de remover a pasta, evitando falha por arquivo ainda bloqueado.
- **Uninstall Inno com escape PowerShell corrigido**: o bloco `Where-Object` usado para parar apenas a instância instalada não gera mais chave extra no comando.
- **Diagnóstico de startup sem congelar UI**: `StartupDiagnosticsWindow` consulta `schtasks` fora da thread de UI.
- **Diagnóstico de startup diferencia falha real de registro**: o botão "Tentar registrar de novo" agora observa o `bool` de `EnsureRegistered()` e mostra erro quando Task Scheduler/HKCU falham.
- **Fallback fechado não marca certificado como notificado**: fechar o popup próprio com "Fechar aviso"/Esc não consolida `LastCheckDate`; só "Ver detalhes" conta como aviso efetivo.
- **Rodapé da janela de detalhes com ellipsis**: mensagens longas de status agora indicam truncamento em largura mínima.
- **Startup não aceita caminho parecido como válido**: a checagem de task registrada agora compara o executável exato, evitando aceitar `CertExpiryMonitor.exe.old` como se fosse o app atual.
- **Startup em Windows localizado reconhece task válida**: o fallback do parser CSV do `schtasks` agora extrai o campo de comando mais provável em vez de devolver a linha CSV inteira. Isso evita recriar uma task correta quando o cabeçalho `Task To Run` vier traduzido.
- **Startup com CSV malformado não finge registro válido**: se o parser do `schtasks` não conseguir identificar um comando com `.exe`, agora retorna `null` em vez de uma linha arbitrária, forçando recriação/diagnóstico correto.
- **Startup rejeita caminho vazio**: builders de Task Scheduler/HKCU agora recusam `null`, vazio ou espaços antes de montar comandos inválidos.
- **Instalação remota aguarda processo sair**: `scripts/Install-FromUrl.ps1` agora aborta antes de instalar se houver instância fora do destino e espera a instância instalada encerrar antes de chamar o instalador, reduzindo falha intermitente por arquivo ainda bloqueado.
- **State JSON tolera `records:null`**: envelope válido com lista nula agora carrega como estado vazio sem renomear o arquivo como corrompido.
- **Toast action tolera espaços em chaves**: parser de argumentos agora normaliza a chave (`action`, `thumbprint`) sem remover espaços codificados do valor.
- **CN não confunde texto dentro de outro atributo**: fallback de `GetCommonNameFallback` agora só aceita `CN=` no início do DN ou depois de vírgula delimitadora, e respeita vírgulas escapadas.
- **Snapshot hash ordena thumbprint normalizado**: caracteres invisíveis/espaços em thumbprints não mudam mais a ordem interna do hash do conjunto de certificados.
- **Evaluator ignora thumbprint vazio**: avaliação, dismiss, restore e mark-notified não criam mais registro de estado com chave vazia.
- **State store filtra após normalizar**: `certificate-state.json` com thumbprint composto só por caracteres invisíveis não cria mais chave vazia; o save também normaliza/omite registros inválidos.
- **Diagnóstico de startup não aceita caminho antigo**: a janela agora só considera inicialização registrada quando Task Scheduler/HKCU apontam para o executável atual; entradas antigas aparecem como `REGISTRADO (CAMINHO DIFERENTE)`.
- **Startup rejeita caminho ambíguo**: builders de Task Scheduler/HKCU recusam aspas e caracteres de controle no caminho do executável antes de montar comandos.
- **Startup exige separador após exe citado**: comandos como `"CertExpiryMonitor.exe"--background` não são mais aceitos como válidos no diagnóstico.
- **Toast action decodifica chaves percent-encoded**: parser agora aceita `%61ction=view-details` e mantém fallback seguro quando a chave vem malformada.
- **Diagnóstico de startup tolera janela fechada durante consulta**: callbacks assíncronos não tentam atualizar controles descartados.
- **Diagnóstico JSON completo**: logs JSON agora preservam `exception.ToString()` além de tipo/mensagem/stack, mantendo inner exceptions.
- **UI sem caractere fora do BMP**: `TelemetryWindow` trocou emoji por símbolo BMP para evitar tofu/quadrado em Segoe UI padrão.
- **CaptureUi mais robusto**: o script espera processos antigos encerrarem e localiza a janela real via UIA/handle antes de capturar.

### Validação

- Build Release sem warnings, 300 testes passando, Stryker `60,84%` com SQLite incluído no mutate filter.
- BugHunt E2E `-Maximum -KeepArtifacts` validado em `artifacts\bughunt\20260523-041940` com publish isolado, startup, toast/logs, `diagnostics.db`, capturas UIA/PNG e cleanup do certificado/processo.

---

## [1.0.0] — 2026-05-10

### UI / UX (validado via screenshots reais)

- **Acentuação correta em PT-BR** em toda a interface (aba "Configurações", "Horário do popup", "Faixas de notificação", "Faixa média", "Salvar horário", "Validos" → "Válidos", "Até X dias", "Situação", "usuário atual", "Atualizado às HH:mm", balloon tip "Nenhum certificado próximo do vencimento", toast title, tray menu "Executar verificação agora", tooltip "Última verificação"). Antes: strings ASCII intencionais por preocupação histórica de encoding — irrelevante em .NET 8 com source files UTF-8.
- **Status text com acentos**: `CertificateStatusHelpers.GetStatusText` agora retorna "Próximo do vencimento" / "Válido" (testes atualizados em conjunto).
- **Coluna "Titular" não trunca mais nomes corporativos**: `FillWeight = 150` (era default 100). Validado com 3 certs reais: "PORTAL COMERCIO E DISTRIBUIDORA LTDA" e "PIONEIRA COMERCIO DE ALIMENTOS LTDA" agora aparecem por extenso.
- **Tooltips explicativos** nos campos de "Faixas de notificação" — clarifica o que cada faixa significa (Faixa longa = primeiro aviso, Urgente = último aviso crítico).
- **Mensagens de erro com acentos**: todos os `MessageBox.Show("Nao foi possivel...")` corrigidos para "Não foi possível...".
- **Toast actions "Não lembrar este" / "Não lembrar nenhum"** com acentos.
- **Nova flag `--details`** no `Program.cs`/`TrayApplicationContext`: abre a janela direto na aba Certificados (`--configure` já existia para abrir em Configurações). Útil para shortcuts no menu Iniciar e para automação/testing.
- **Script `scripts/CaptureUi.ps1`**: automação que inicia o app, captura PNG das duas abas via `PrintWindow` (Win32 GDI — funciona mesmo em sessão sem desktop interativo) e dumpa árvore UIA para análise de acessibilidade.

### Round 5 — Inicialização automática robusta (campo: app não abria após reboot)

**Bug de campo reportado:** computadores onde o app foi instalado **não abriam o app sozinhos depois de reboot**. Análise revelou cadeia frágil:

- O `.iss` (Inno Setup) registrava startup **apenas** via primeira execução do app
- Se a primeira execução não acontecesse (silent install, usuário desmarcou "Iniciar agora", crash silencioso na inicialização, timeout do `schtasks` em ambiente lento), **nada ficava registrado**
- Reboot → app não iniciava → usuário precisava abrir manualmente sem saber disso

**Fix em 3 camadas defensivas:**

1. **Instalador registra startup direto** — novo `[Run]` em `installer/CertExpiryMonitor.iss` que invoca `schtasks /create /sc ONLOGON /rl LIMITED /f` **antes** mesmo da primeira execução do app. Independe de o app rodar, funciona em install silent, sobrescreve task antiga via `/f`.

2. **`EnsureRegistered` idempotente** — `StartupRegistration.EnsureRegistered()` agora faz pre-check: se a task já existe E aponta para o exe atual, pula recriação. Importante após atualização de versão (caminho do exe pode mudar). Reduz overhead.

3. **Diagnóstico no menu da bandeja** — novo item **"Diagnóstico de inicialização..."** abre `StartupDiagnosticsWindow` que mostra:
   - Status visual: ✓ registrado / ⚠ não registrado
   - Caminho do executável resolvido
   - Comando exato no Task Scheduler (via `schtasks /query`)
   - Comando exato no `HKCU\Run` (via Registry API)
   - Botão **"Tentar registrar de novo"** para forçar `EnsureRegistered()` manualmente

**API nova:** `StartupRegistration.QueryStatus()` retorna `StartupStatus` record (`TaskSchedulerRegistered`, `RegistryRegistered`, `ResolvedExecutablePath`, comandos completos) para inspeção/UI sem mutação.

**Para corrigir instalações antigas em campo** (computadores já instalados antes deste fix):
- Opção A: reinstalar com o novo `.exe` do instalador (o `[Run]` cria a task na hora)
- Opção B: abrir o app manualmente 1 vez (o `EnsureRegistered` programático ainda funciona como backstop)
- Opção C: abrir o menu da bandeja → "Diagnóstico de inicialização..." → "Tentar registrar de novo"

### Round 4 — Telemetria + Logs estruturados + EventLog + Enum semantic

**Telemetria local opt-in** (`Services/TelemetryService.cs`)

- Coleta **anônima e local** (sem rede): 11 contadores agregados em `telemetry.json` (envelope v1).
- Métricas: `TotalChecks`, `ChecksWithPlan`, `ChecksSkipped`, `ManualChecks`, `NotificationsShown`, `NotificationFailures`, `DismissOne`, `DismissAll`, `Restore`, `ThresholdsChanged`, `ScheduleChanged`.
- **Privacidade by design**: nunca grava thumbprints, nomes, documentos ou caminhos. Apenas contadores numéricos.
- **Opt-in via `AppSettings.TelemetryEnabled`** (default `false`). Quando desabilitado, `Increment()` é no-op (não cria arquivo nem persiste).
- Visualização via menu de bandeja **"Ver estatísticas de uso..."** → `TelemetryWindow` (mini-form com tabela dos contadores + botão "Limpar estatísticas" + data de início da coleta).
- **6 testes** em `TelemetryServiceTests`: no-op quando disabled, persistence, load default, reset, UpdatedAt avança, CreatedAt preservado entre updates.

**Logs estruturados JSON** (`FileLogger`)

- Nova propriedade `LogFormat` em `AppSettings` (`Text` default | `Json`). `FileLogger.ApplySettings(settings)` aplica em runtime sem precisar reiniciar.
- Formato JSON Lines (JSONL — uma linha por evento): `{"ts":"...","level":"...","message":"...","exceptionType":"...","exceptionMessage":"...","stackTrace":"..."}`. Pronto para ingestão em Splunk/ELK/Sentinel/Azure Monitor.
- Formato Text legado mantido como default (compatibilidade com qualquer parser que já leia logs antigos).

**Windows EventLog opcional** (`FileLogger`)

- Nova propriedade `EventLogEnabled` em `AppSettings` (default `false`). Quando ativa, eventos ERROR são espelhados para Windows Event Log (canal Application, EventID 1000).
- Tenta criar source `CertExpiryMonitor` (precisa admin uma vez); fallback para source genérica `Application` (sempre disponível, sem elevação).
- Falha do EventLog é silenciosa (best-effort) — nunca quebra o app.

**Item 12 — `CertificateNotificationState` enum semantic**

- `Notified30 → NotifiedLong`, `Notified15 → NotifiedMedium`, `Notified7 → NotifiedShort`, `Notified1 → NotifiedUrgent`. Valores numéricos do enum (30, 15, 7, 1, 999) **mantidos** para preservar compatibilidade com `certificate-state.json` existentes em produção.
- Testes acompanhados (rename mecânico em 5 arquivos de teste).

**Item 13 — Race Program.cs mutex/events** — falso positivo

- Análise re-feita: `EventWaitHandle.AutoReset` SEM waiter mantém o estado signaled até alguém esperar (Win32 spec). O `HandleActivationRequests` faz `WaitOne(0)` a cada 500ms no message loop, drenando qualquer sinal pendente. **Sinal não é perdido.** Documentado em AGENTS.md como verificado.

**UI da aba Configurações — GroupBox "Avançado"**

- 3 CheckBoxes (LogFormat=Json, EventLogEnabled, TelemetryEnabled) com tooltips explicativos.
- Form cresceu `ClientSize 940×500 → 940×600` para acomodar a nova GroupBox.
- "Salvar configurações" agora salva 3 grupos atomicamente: Notificação + Faixas + Avançado.
- `FileLogger.ApplySettings` é chamado em runtime após Save — formato JSON e EventLog refletem instantaneamente sem restart.

### UI/UX — Round 3 (refactor estrutural + busca + identidade visual)

- **Cards do summary agora são clicáveis** para filtrar o grid: clicar em "⛔ Vencidos" aplica `Mostrar = Vencidos`, "⚠ Até 7 dias"/"⌛ Até 30 dias" aplica `Mostrar = A vencer`, etc. Cursor muda para hand-pointer ao hover, tooltip "Clique para filtrar a lista por esta categoria".
- **Busca textual no grid** (`TextBox` com `PlaceholderText`): filtra por `Holder` ou `Document` via `LIKE` no `DataTable.RowFilter`, case-insensitive, escapando apóstrofos para evitar SQL injection. Combina via `AND` com o filtro de categoria.
- **Coluna "Situação" some dinamicamente** quando um filtro de categoria específico está aplicado (era redundante — todas as linhas teriam o mesmo valor).
- **Refactor estrutural da aba Configurações**:
  - `GroupBox "Notificação"` agrupando descrição + horário + botão de teste + checkbox de som (CheckBox "Tocar som" era órfão entre seções).
  - `GroupBox "Faixas de notificação (dias antes do vencimento)"` com layout 1-coluna sequencial decrescente: longa → média → curta → urgente (era 2×2 forçando ziguezague).
  - Cada NumericUpDown agora tem sufixo "dias" em cinza reforçando a unidade.
  - Sublabel explicativo "Da faixa mais distante (primeiro aviso) à mais crítica (último aviso):".
  - **Botão único "Salvar configurações"** no rodapé direito, substituindo os 2 botões duplicados (`Salvar horário` + `Salvar faixas`). Salva horário + som + faixas atomicamente.
- **Footer reordenado conforme convenção Windows**: `[Atualizar lista] [Ignorar certificado] [Fechar]` — Fechar mais à direita, isolada das ações de manipulação de dados.
- **Ícone próprio do app** (`assets/CertExpiryMonitor.ico`, 7 tamanhos 16/24/32/48/64/128/256 px) embarcado como `EmbeddedResource` e referenciado via `<ApplicationIcon>` no `.csproj`. Substituiu o `SystemIcons.Information` genérico em `NotifyIcon` (bandeja) e `DetailsForm.Icon` (title bar / Alt-Tab). Gerador: `scripts/GenerateAppIcon.ps1` desenha programaticamente um certificado estilizado com selo "A1".
- **`AppIcon` helper** (`Services/AppIcon.cs`): carrega o ícone embutido uma única vez (cache estático) com fallback para `SystemIcons.Information` se o resource não for encontrado.
- **DetailsForm cresceu** `ClientSize = 940×500` (era 940×460) para acomodar a linha de busca; `MinimumSize` cresceu proporcionalmente.

### Operacional — repositório Git

- **`git init -b main`** + primeiro commit "Initial commit: CertExpiryMonitor v1.0.0" criado.
- **`.gitignore` atualizado** para incluir `assets/preview-*.png` (gerados pelo script de preview do ícone).
- **Remote `origin`** configurado para `https://github.com/andrejipa/CertExpiryMonitor.git`. Credenciais HTTPS já estão no Windows Credential Manager (auth chegou ao GitHub e respondeu "Repository not found").
- **README com 4 badges** (CI build, tests passing, .NET 8, Windows 10/11) — URLs apontando para `andrejipa/CertExpiryMonitor`.
- **Instruções no README** sobre como completar o push (criar repo em `github.com/new` + `git push -u origin main`).

### UI/UX — Round 2 (validado via screenshots + auditoria adversarial)

- **`HighDpiMode = PerMonitorV2`** no `.csproj` (`<ApplicationHighDpiMode>`): textos nítidos em monitores 4K e setups multi-monitor com DPIs diferentes. Sem isso, o app fica borrado em DPI > 100%.
- **`ApplicationUseCompatibleTextRendering = false`** + **`ApplicationVisualStyles = true`**: garante renderização GDI+ moderna e visual styles do Windows 10/11.
- **Ícones Unicode BMP nos cards de contagem** (⛔ ⚠ ⌛ ✓ ⊘): resolve acessibilidade de cor (WCAG — daltônicos não dependem mais só de matiz). Usados caracteres do Basic Multilingual Plane porque emojis do Supplementary Plane (🔕, ⏰) não renderizam consistentemente em fontes WinForms.
- **Coluna "Vencimento" simplificada para `dd/MM/yyyy`** (sem hora): minutos não agregam valor — usuário não age sobre eles. Reduz ruído cognitivo.
- **"Dias restantes" alinhado à direita** com padding-right de 10px: comparação numérica visual (73 vs 275 vs 364) agora flui naturalmente.
- **Zebra striping visível**: cor passou de `RGB(250,250,250)` (quase invisível) para `RGB(244,247,250)` (sutil mas perceptível). Aplicado em `AlternatingRowsDefaultCellStyle` E no `ApplyRowStyles`.
- **Tooltips em todos os botões** ("Ignorar certificado", "Atualizar lista", "Fechar"): explica o que cada um faz, com ênfase no estado desabilitado do "Ignorar".
- **Estado vazio com CTA**: `FormatCountLabel` retorna mensagem singular/plural correta e, quando 0 certificados, exibe "Nenhum certificado A1 encontrado. Instale um certificado .pfx no Windows para começar a monitorar."
- **AccessibleName e AccessibleDescription** em controles principais (DataGridView, ComboBox de filtro, 3 botões do footer, 4 NumericUpDown de faixas). Validado via dump UIA: leitores de tela agora lêem "Filtro de exibição de certificados" em vez de herdar nome do controle adjacente.

### Infraestrutura

- **CI no GitHub Actions** (`.github/workflows/build.yml`): job `build-test` (windows-latest, .NET 8 SDK, restore→build→test com cobertura, upload de TRX) e job `publish-smoke` (publish single-file win-x64, upload do `.exe` como artefato). Garante que regressões de build/test/publish quebrem o PR.
- **`.gitignore`**: cobre `bin/`, `obj/`, `publish/`, `TestResults/`, `installer-output/`, `.dotnet-local/`, IDEs e arquivos sensíveis.
- **Envelope versionado em `settings.json`**: `{ "version": 1, "settings": { ... } }`, com leitura retrocompatível ao formato legado e migração automática no próximo Save. Alinha com `JsonStateStore`. `PropertyNameCaseInsensitive=true` no JsonOptions evita bugs de case (PascalCase vs camelCase).

### Testes

- **104 testes verdes** (era 86 casos). Novos arquivos:
  - `JsonSettingsStoreTests` (8 casos): envelope, legado, migração, JSON corrompido, future version forward-compat.
  - `JsonStateStoreConcurrencyTests` (3 casos): `Parallel.For` Save+Load não corrompe, exceptions zero sob race.
  - Em `CertificateCheckServiceTests` (+6 casos): `FakeCertificateReader` (subclasse via `virtual`) elimina dependência do store X.509 real; teste de race do `Interlocked` no `_isChecking`; null guards.
- **`CertificateReader.ReadCurrentUserPersonalCertificates` virtualizado** (sem criar interface — respeita a convenção do projeto). Permite testes determinísticos sem violar a convenção "classes concretas diretas".

### Adicionado

- **Faixas de notificação configuráveis** (`ExpiryThresholds`): usuário pode ajustar os limites de 30/15/7/1 dias diretamente na aba Configurações do DetailsForm; `Normalized()` garante ordem crescente e valores ≥ 1.
- **Task Scheduler para startup**: `StartupRegistration` tenta `schtasks /create /sc ONLOGON /rl LIMITED` antes de usar `HKCU\Run`; stderr logado em caso de falha; fallback automático para `HKCU\Run`.
- **Hash de snapshot de certificados**: SHA-256 de thumbprints+validades para detectar mudanças no store sem re-executar verificação completa no mesmo dia.
- **`CertificateCheckService`**: toda a lógica de verificação extraída de `TrayApplicationContext`; guard `_isChecking`, propriedade `LastPlan`, método público `MarkNotified`.
- **`CertificateDocumentHelpers`**: classe `internal static` com `FormatDocument`, `ParseHolder`, `GetCommonNameFallback` — agora testáveis isoladamente.
- **`DetailsFormOptions`**: substituiu construtor com 13+ parâmetros em `DetailsForm`.
- **`GetThresholds` callback** em `DetailsFormOptions`: `WireAnalyzeButton` sempre usa thresholds atuais (corrigi captura stale do form).
- **Versão no binário**: `<Version>`, `<AssemblyVersion>`, `<FileVersion>` no `.csproj`; log `CertExpiryMonitor v{versao} starting` na inicialização.
- **Stack traces nos logs de erro**: `FileLogger.Error` usa `exception.ToString()` — inclui tipo, mensagem e stack trace completo.
- **Stderr do schtasks logado** quando ExitCode ≠ 0.
- **Feedback visual** quando verificação manual não encontra certificados pendentes (balloon tip "Nenhum certificado próximo do vencimento").
- **Tooltip dinâmico** do ícone de bandeja: versão e data da última verificação (respeitando limite de 63 chars do Win32).
- **Rotação de log em cascata**: até 3 backups (`monitor.log.1/.2/.3`); antes mantinha apenas 1.
- **`AppPaths` com `rootOverride`**: parâmetro opcional para testes isolados em diretórios temporários.
- **`InternalsVisibleTo("CertExpiryMonitor.Tests")`**: clases `internal` acessíveis nos testes.
- **coverlet** adicionado ao projeto de testes.
- **Novos testes** (~86 casos no total): `ExpiryThresholdsTests`, `CertificateDocumentHelpersTests`, `CertificateStatusHelpersTests` (faixas customizadas no grid), `JsonStateStoreTests`, `CertificateCheckServiceTests`, `ExpiryEvaluatorThresholdsTests`.
- **`CertificateStatusHelpers`**: `internal static` com `GetStatusText` e `GetStatusCategory` extraidos de `DetailsForm` — testaveis isoladamente, garantem que classificacao visual respeita thresholds customizados.
- **`AGENTS.md`**: guia completo para agentes de IA com mapa de arquivos, regras de negócio e backlog.
- **`scripts/Publish-Release.ps1`**: script de release que sincroniza versão no `.csproj` e no `.iss`, executa `dotnet publish` e opcionalmente chama o Inno Setup.
- Menu "Abrir pasta de logs" na bandeja do sistema.
- `SimpleName` via `GetNameInfo(X509NameType.SimpleName)` em `CertificateReader` — parsing correto de CNs com vírgulas escapadas.
- Atalho COM recriado somente quando executável é mais novo que o atalho existente (`EnsureShortcut`).
- Envelope versionado em `JsonStateStore` (`{ "version": 1, "records": [...] }`) com leitura retrocompatível de formato legado (array puro).

### Corrigido

- **Duplo startup**: instalador e scripts não gravam mais `HKCU\Run` — toda gestão de startup centralizada em `StartupRegistration`.
- **Task Scheduler órfão no uninstall**: `[UninstallRun]` do Inno Setup e `Uninstall-CurrentUser.ps1` removem a tarefa agendada.
- **Thresholds stale no DetailsForm**: "Atualizar lista" usava thresholds capturados na abertura do form; corrigido via `GetThresholds` callback.
- **Overflow em `FormatDocument`**: `Convert.ToUInt64` substituído por `ulong.TryParse` — sem `OverflowException` para números de série com prefixo hexadecimal.
- **`Application.DoEvents()` removido**: botão "Atualizar lista" usa `async/await` + `Task.Run`.
- **Cast overflow em timer**: `Math.Clamp(delay.TotalMilliseconds, 1000, int.MaxValue)` impede overflow ao agendar delays > 24 dias.
- **Botões de dismiss ausentes no toast**: adicionados "Nao lembrar este" e "Nao lembrar nenhum" (thumbprints URI-encoded nos argumentos).
- **Thresholds hardcoded no DetailsForm**: `GetStatusCategory` e `ApplyRowStyle` usavam dias fixos (7/30) em vez dos thresholds configurados pelo usuário; corrigido para usar `ExpiryThresholds.Level7`/`Level30` normalizados; `ApplyRowStyle` reescrito para usar `StatusCategory` pré-calculado, eliminando dupla avaliação.
- **Summary panel desatualizado ao mudar thresholds**: rótulos "Ate X dias" e cores do grid não refletiam thresholds salvos até reabrir o form. Adicionado callback `onThresholdsSaved` que reclassifica linhas em memória, atualiza Tag dos labels Critical/Warning e refaz cores via `ApplyRowStyles`.
- **Deadlock potencial em `StartupRegistration`**: `WaitForExit` sem leitura assíncrona de stdout/stderr podia travar se buffer do schtasks enchesse (>4KB). Streams agora são drenados via `ReadToEndAsync` antes do `WaitForExit`; timeout dispara `Kill(entireProcessTree)`.
- **Race em `CertificateCheckService._isChecking`**: campo `bool` substituído por `int` com `Interlocked.CompareExchange/Exchange` — guard atômico entre timer thread e UI thread.
- **`Application.ExecutablePath` → `Environment.ProcessPath`**: mais previsível em publish single-file; aplicado em `StartupRegistration` e `ToastNotifierService.EnsureShortcut`.
- **COM RCW leak em `ToastNotifierService`**: `CShellLink` (com 3 casts subsequentes a `IShellLinkW`/`IPropertyStore`/`IPersistFile`) agora é liberado via `Marshal.FinalReleaseComObject` em `finally` — evita acúmulo de ref counts em apps tray longevos.
- **Rotação de log não-fatal**: falha em `File.Move` (antivirus, lock) não interrompe mais a gravação; logs continuam sendo persistidos mesmo que o cap de tamanho não seja respeitado.
- **Null guards públicos**: `CertificateCheckService.RunCheck`, `MarkNotified` e `ComputeSnapshotHash` agora rejeitam `null` com `ArgumentNullException.ThrowIfNull`.
- **README desatualizado**: linha "inicialização via HKCU\\Run" corrigida para refletir a prioridade do Task Scheduler.
- **`_singleInstanceMutex` nunca era `Dispose()`**: handle vazado no shutdown. Agora `Dispose()` é chamado no `finally`; `ReleaseMutex` envolto em try/catch para não mascarar exceção original.
- **Bug detectado pelos novos testes**: `JsonSettingsStore` deserializava envelope com `JsonDocument.TryGetProperty` case-sensitive (`"version"` vs `"Version"`). Corrigido com `EnumerateObject` insensitive + `PropertyNameCaseInsensitive=true` no JsonOptions.
- **Teste flaky descoberto rodando perto da meia-noite**: `RunCheck_SkipsWhenTimeNotReachedAndFlagIsFalse` usava `DailyCheckTime = 23:59`; quebrou ao executar exatamente às `23:59:xx` (hora atual > horário configurado). Substituído por `TimeSpan.FromHours(48)`, garantidamente inatingível (`now.TimeOfDay < 24h` sempre).

### Documentação

- `ExpiryBucket` e `CertificateNotificationState` receberam XML docs explicando acoplamento semântico com thresholds padrão.
- `README.md` atualizado: arquitetura, regras, Task Scheduler, Publish-Release, testes.
