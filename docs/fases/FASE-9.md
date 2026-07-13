# Fase 9 — Extras: desktop capture, Spout, MIDI/OSC, remote web

## Objetivo
Fase guarda-chuva dos recursos que completam o produto. **Cada tarefa é
independente** — priorizar conforme a necessidade real do momento (não precisa
executar em ordem nem completar tudo).

## Pré-requisitos
- Fase 8 concluída. Escolher as tarefas desta fase conforme demanda.

---

## Tarefas (independentes entre si)

### T9.1 — Desktop capture
- `Windows.Graphics.Capture` (WinRT via CsWinRT): capturar monitor ou janela;
  o frame já nasce como textura D3D11 no nosso device (`Direct3D11CaptureFramePool`
  com o device do engine) — custo ~zero, só `CopyResource`.
- `DesktopCaptureSource : IFrameSource` + descriptor `DesktopSourceDescriptor
  (MonitorDeviceName | WindowTitle)`; picker na aba Fontes (lista monitores e
  janelas top-level).
- Janela fechada / monitor removido → `Arming` + retry (padrão da fase 5).
- Cuidado: capturar um monitor que é SAÍDA do próprio programa = feedback
  infinito — detectar e avisar (permitir mesmo assim; às vezes é intencional).

### T9.2 — Spout (compartilhamento GPU entre apps)
- Receiver primeiro (mandar é raro no nosso caso): protocolo Spout = textura
  D3D11 compartilhada + sendername em memória compartilhada/registry.
  Implementar leitura do diretório de senders do Spout (memory-mapped
  `SpoutSenderNames`) + `OpenSharedResource` da textura → `SpoutSource :
  IFrameSource`. Referência: SpoutDX (código aberto, MIT) — portar a lógica, não
  linkar C++.
- Sender (expor a composição como Spout) fica no backlog salvo demanda.

### T9.3 — MIDI
- NuGet `Melanchall.DryWetMidi` no App; `MidiService`: escolher device de
  entrada, **MIDI learn** (modo "aprender": clicar num controle da UI → mexer no
  controlador → binding gravado).
- Alvos mapeáveis: trigger de célula/coluna/cena, opacity de layer (CC),
  master fader (CC), blackout, pausa master.
- Persistir em `%AppData%\VideoWallRW\midimap.json`; indicador de atividade
  MIDI na barra de status.

### T9.4 — OSC (entrada)
- UDP listener (porta config, default 7000); esquema de endereços:
  `/vwrw/layer/{n}/clip/{m}/trigger`, `/vwrw/layer/{n}/opacity {float}`,
  `/vwrw/column/{n}/trigger`, `/vwrw/scene/{nome}/trigger`,
  `/vwrw/master/fader {float}`, `/vwrw/blackout {0|1}`, `/vwrw/pause {0|1}`.
- Parser OSC é pequeno — implementar (spec 1.0, só mensagens, sem bundles no
  MVP) ou usar NuGet leve. Documentar os endereços em `docs/OSC.md` (é API
  pública para TouchOSC/Companion).

### T9.5 — Controle remoto web
- Kestrel embutido (porta 8080 config): serve página única (HTML+JS vanilla,
  embutida como resource) + WebSocket com o estado da grade (nomes, thumbs
  base64 pequenas, clip ativo) e comandos (trigger célula/coluna/cena, opacity,
  fader, blackout).
- Reusar exatamente os `EngineCommand` existentes (o servidor é só mais um
  produtor da fila — arquitetura paga aqui).
- Segurança mínima: PIN de 4 dígitos configurável; bind só na LAN por default.
  Caso de uso: operador ajusta o telão andando pelo local com o celular.

### T9.6 — Refinamentos de saída (conforme demanda)
- **Edge blend** para projetores: overlap entre slices com rampa gamma nas
  bordas (shader do passe de saída; UI: largura do blend por aresta do slice).
- **Corner-pin warp** por slice (homografia 4 pontos, para projeção em
  superfície torta).
- **Máscara** por slice (retângulos de recorte; LED com formato irregular).
- Saída **NDI out** (a composição vira fonte NDI na rede — o SDK já está no
  projeto desde a fase 5).

### T9.7 — Qualidade de vida (backlog priorizável)
- GIF animado como fonte; Ken Burns em imagens (zoom/pan lento configurável).
- Decks (bancos de grade) como abas.
- Etiquetas coloridas + busca/filtro de células.
- Modo ensaio: saídas simuladas em janelas normais redimensionáveis.
- Relatório de mídia no import (codec problemático → sugerir conversão HAP).
- Exportar/importar keymap e midimap.

---

## DoD
Por tarefa executada, na entrega:
- T9.1: capturar um navegador em janela e um monitor inteiro na grade, 60fps.
- T9.2: receber TouchDesigner/Resolume via Spout na grade.
- T9.3: disparar coluna e controlar opacity por um controlador físico via learn.
- T9.4: disparar célula via `oscsend` externo conforme `docs/OSC.md`.
- T9.5: pelo celular na mesma rede: ver a grade, disparar clips, fader e
  blackout com PIN.
- T9.6/T9.7: DoD definido caso a caso ao puxar a tarefa.
