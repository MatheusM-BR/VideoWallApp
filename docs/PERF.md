# Capacidade e desempenho — VideoWallRW

Este documento é o "manual de dimensionamento" para instalações: quanto conteúdo
simultâneo uma máquina aguenta, e como medir isso de novo quando o hardware mudar.

## Metodologia

Para cada cenário, rode o programa com o cenário montado, deixe estabilizar por
30s e leia:
- **FPS por saída** (barra de status do painel, ou `EngineStats.Outputs`).
- **Drops** (`⚠ N drops` na barra de status — frames com frametime > 1.5× o alvo
  de 60fps, acumulados desde a última leitura ~0.5s).
- **CPU/GPU** no Gerenciador de Tarefas (aba Desempenho → GPU mostra "Video
  Decode" separado de "3D" — decode por software aparece como uso de CPU alto
  e "Video Decode" da GPU baixo/zero).
- **Memória do processo** (deve ficar estável ao longo de um soak de 1h+; qualquer
  crescimento contínuo é vazamento).

Repita cada cenário por pelo menos 2 minutos antes de anotar — os primeiros
segundos após um trigger têm ruído do preroll.

## Cenários de referência

| Cenário | FPS esperado | Drops esperados | Observações |
|---|---|---|---|
| 1× vídeo H.264 1080p60 | 60fps estável | 0 | decode por software (sem D3D11VA — ver nota abaixo) |
| 4× vídeo H.264 1080p60 simultâneos | 60fps estável | ~0 | ponto de partida para saber quantos "layers pesados" a máquina aguenta |
| 6× vídeo HAP 1080p (Hap1) simultâneos | 60fps estável | 0 | HAP não usa decode de CPU — deve escalar muito melhor que H.264 |
| 1× vídeo H.264 4K30 | 30fps estável | 0 | decode por software de 4K é pesado — se cair frames, esse é o teto |
| 2× vídeo HAP 4K30 | 30fps+ | 0 | comparar CPU aqui vs. o cenário H.264 4K acima |

## O que já foi medido

- **Latência de disparo (Trigger→ar)**: medida via log em `LayerRenderer`
  (`docs/fases/FASE-6.md` T6.6). Em teste com um clip HAP 256×256 já armado,
  ficou em **~5ms** — bem abaixo da meta de <100ms. Vídeo H.264 comum deve ficar
  na mesma faixa para arquivos locais já em cache do SO (o gargalo real costuma
  ser o primeiro `avformat_open_input`, que em disco frio pode levar dezenas de
  ms; em SSD local normalmente também fica bem abaixo de 100ms).
- **HAP1/HAP5 decode**: validado funcionalmente (imagem correta, sem lixo) via
  leitura direta de pixels da GPU, incluindo um arquivo real de produção
  convertido pelo próprio conversor em lote (T6.5) e tocado de volta pelo
  `HapVideoSource` (T6.4). Não foi medido *throughput* de múltiplos HAP
  simultâneos neste ambiente — precisa da tabela acima rodada na máquina alvo.

## O que ainda não foi medido (precisa da máquina de instalação real)

Este ambiente de desenvolvimento não permite rodar os cenários multi-clip da
tabela acima com confiança (sem GPU dedicada exclusiva para o teste, sem
controle sobre carga concorrente do sistema). **Rode a tabela de cenários na
máquina real antes de fechar o dimensionamento de uma instalação.**

## Decode por hardware (D3D11VA) — experimental, desligado por padrão

Liga em **Ferramentas → Decodificar vídeo pela GPU (experimental)** (fica salvo em
`%AppData%\VideoWallRW\settings.json` e vale para os clipes abertos depois).

Como funciona: o FFmpeg decodifica H.264/H.265/VP9 na placa de vídeo, num device
D3D11 **próprio** (não o do render), e o quadro volta para a memória (NV12) pelo mesmo
caminho do decode por software. Não há interop de textura entre devices — a parte do
plano original (T6.1–T6.3) mais sensível a driver —, então o ganho é o de tirar a
decodificação do processador, com uma cópia GPU→memória por quadro.

Segurança: codec sem D3D11VA (HAP, ProRes, imagens) segue no processador; se a GPU
recusar o vídeo na abertura, ele abre por software; se falhar no meio da reprodução, o
mesmo clipe é reaberto no processador a partir do ponto onde estava e a GPU é desligada
até o fim da sessão. O log mostra `decodificação GPU (D3D11VA)` ou `processador` por clipe.

**Antes de ligar em produção**, rode a tabela de cenários acima com a opção desligada e
ligada na máquina do estúdio e compare CPU/GPU ("Video Decode" no Gerenciador de Tarefas).
Zero-copy (textura NV12 direto no render) continua como evolução futura.

## Como estender esta tabela

Ao rodar os cenários na máquina de instalação, adicione uma seção "Máquina X"
com CPU/GPU/RAM e os números medidos, para servir de referência comparativa em
futuras instalações com hardware parecido.
