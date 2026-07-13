# Fase 8 — Modo instalação fixa: autostart, watchdog, sequenciador, agendador

## Objetivo
O programa roda **sozinho, para sempre**: liga com o Windows, retoma o conteúdo
sem interação, se recupera de travamentos, troca conteúdo por playlist e por
horário, e aceita mídia nova via pasta monitorada. DoD dramático de propósito:
reiniciar o PC e ver o conteúdo voltar ao ar sem tocar em nada.

## Pré-requisitos
- Fase 7 concluída (DoD verificado).

## Entregáveis
1. Estado de runtime persistido (`resume`) + partida autônoma (`--kiosk`).
2. `VideoWallRW.Watchdog` (processo supervisor).
3. Sequenciador de colunas (playlist).
4. Agendador por relógio/dia da semana.
5. Watch folder.
6. Tela "Instalação" nas configurações concentrando tudo.

---

## Tarefas

### T8.1 — Estado de runtime e retomada
- `RuntimeState { string ProjectPath; Dictionary<Guid,int> ActiveClips;
  Dictionary<Guid,double> Playheads; double MasterFader; bool SequencerRunning;
  int SequencerStep; }` gravado atomicamente a cada 5s em
  `%LocalAppData%\VideoWallRW\runtime-state.json` (só quando mudou).
- Partida `--kiosk`: sem splash, carregar último projeto, aplicar RuntimeState
  (rearmar clips ativos; playhead de vídeo em loop pode retomar aproximado —
  arredondar para o in-point é aceitável, documentar), ativar saídas, esconder a
  janela principal na bandeja (tray icon: restaurar/console/sair).
- Partida normal (sem flag) nunca rearma sozinha (comportamento da fase 7).

### T8.2 — Autostart
- Configurações → Instalação: toggle "Iniciar com o Windows" → grava/remove
  valor em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
  (`"VideoWallRW" = "...\VideoWallRW.App.exe" --kiosk`). Mostrar o comando na UI
  (transparência para o técnico).
- Documentar em `docs/INSTALACAO.md`: desativar sleep/hibernação, Windows
  Update em horário controlado, plano de energia alto desempenho, ocultar
  notificações (Focus Assist) — checklist do PC de instalação.

### T8.3 — Watchdog
`src/VideoWallRW.Watchdog/` (exe minúsculo, sem dependências do resto):
- App abre named pipe `vwrw-heartbeat` e escreve ping a cada 2s **da render
  thread** (é ela que importa; UI travada não é falha de show).
- Watchdog: se contratado (`--watch <pid>` é lançado PELO app quando o modo
  instalação está ativo), monitora: sem ping por 10s OU processo morto →
  `taskkill` se preciso → relançar `--kiosk` → logar incidente em
  `%LocalAppData%\VideoWallRW\logs\watchdog.log`.
- Anti-flapping: 3 restarts em 5 min → esperar 60s entre tentativas (nunca
  desistir de vez — é uma instalação).
- App encerrado normalmente avisa o watchdog para sair junto (mensagem no pipe).

### T8.4 — Sequenciador de colunas
- Modelo no projeto: `SequencerConfig { bool Enabled; bool Loop;
  List<SequencerStep> Steps; }`, `SequencerStep { int Column; double Seconds; }`.
- Engine-side (roda sem UI): timer no relógio do render; passo atual →
  `TriggerColumn`; respeita "armar próxima coluna" da fase 6 (preroll do passo
  seguinte 2s antes da troca).
- Pausa master pausa também o sequenciador (coerência com o congelamento geral).
- UI: lista reordenável de passos (coluna + duração), play/stop do sequenciador,
  indicador do passo atual e contagem regressiva.

### T8.5 — Agendador
- `ScheduleRule { string Name; bool Enabled; DaysOfWeek Days; TimeOnly Time;
  ScheduleAction Action; }` — ações: disparar coluna N, disparar cena X, iniciar/
  parar sequenciador, blackout on/off, ativar/desativar saídas.
- Avaliador engine-side: checagem a cada 10s do relógio de parede
  (`DateTime.Now`); disparar regra quando cruzar o horário (guardar "última
  execução" para não repetir no mesmo minuto nem re-disparar após restart do
  watchdog dentro do mesmo minuto — persistir no RuntimeState).
- UI: lista de regras com editor simples; log das últimas execuções.

### T8.6 — Watch folder
- Config por layer (opcional): pasta monitorada → as células da layer refletem
  os arquivos da pasta (ordenados por nome), criando/removendo clips
  automaticamente.
- `FileSystemWatcher` + debounce 2s + teste de arquivo completo (abrir com
  acesso exclusivo; se falhar, re-agendar — arquivo ainda copiando).
- Arquivo removido da pasta: se estava no ar, deixa terminar o loop atual e
  limpa; célula some.
- Caso de uso alvo: cliente joga MP4 numa pasta de rede e o conteúdo entra na
  rotação do sequenciador sem ninguém tocar no programa.

### T8.7 — Tela "Instalação"
- Configurações → Instalação: autostart, watchdog on/off, kiosk na bandeja,
  sequenciador (atalho para o editor), agendador (idem), watch folders, botão
  "Testar recuperação" (mata a render thread de propósito — dogfood do watchdog).

---

## Armadilhas conhecidas
- `FileSystemWatcher` em pasta de REDE perde eventos: complementar com
  varredura periódica (30s) comparando snapshot da pasta.
- Relógio de parede muda (NTP, horário de verão): agendador deve tolerar saltos
  — regra "perdida" por salto de até 5 min executa mesmo assim; salto maior,
  ignora (logar).
- Heartbeat pelo pipe não pode bloquear a render thread: escrita fire-and-forget
  com buffer; pipe cheio = descartar (o watchdog só olha o intervalo).
- Restart pelo watchdog durante gravação do RuntimeState: a escrita atômica da
  fase 7 (tmp+replace) já cobre; testar matando o processo em loop.
- Tray app + WPF: fechar a janela ≠ sair do app em modo kiosk (interceptar
  `Closing` → minimizar para bandeja; sair só pelo menu da bandeja).

## DoD — verificação manual
1. Ativar instalação completa (autostart + watchdog + sequenciador) →
   **reiniciar o PC** → conteúdo volta ao ar sozinho, sequenciador rodando do
   passo salvo.
2. Matar o processo no Task Manager → watchdog relança em ≤ 15s, conteúdo volta.
3. "Testar recuperação" (render thread congelada) → mesma coisa.
4. Sequenciador com 3 passos roda 30 min sem drift perceptível nem frame preto
   nas trocas (preroll do próximo passo funcionando).
5. Regra agendada "18:00 dispara cena Noite" executa no horário (testar com
   horário próximo).
6. Jogar MP4 na watch folder (cópia lenta via rede simulada): entra na grade só
   quando a cópia termina; remover o arquivo limpa a célula.
7. Soak de 24h em kiosk: zero interações, conteúdo no ar o tempo todo,
   watchdog.log sem restarts inesperados (ou com restarts que se recuperaram).
