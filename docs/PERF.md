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

## Decode por hardware (D3D11VA) — não implementado nesta versão

O plano original (`docs/fases/FASE-6.md` T6.1–T6.3) previa decode H.264/H.265/VP9
via D3D11VA com textura NV12 direto na GPU (zero cópia). Essa parte foi
**deliberadamente adiada**: é a peça de maior risco de todo o roadmap (integração
zero-copy entre FFmpeg e D3D11 é notoriamente sensível a driver/GPU específicos,
e exige testes extensivos em hardware real NVIDIA/AMD/Intel que não é possível
validar com confiança neste ambiente). Retomar quando houver acesso a máquinas
de teste reais com GPUs variadas.

**Consequência prática**: todo vídeo H.264/H.265 comum decodifica por software
(CPU) nesta versão. Para muitos layers simultâneos ou 4K+, prefira **HAP**
(T6.4/T6.5) — o codec já entrega o benefício de "decode quase grátis" sem
depender de D3D11VA, e já está implementado e validado.

## Como estender esta tabela

Ao rodar os cenários na máquina de instalação, adicione uma seção "Máquina X"
com CPU/GPU/RAM e os números medidos, para servir de referência comparativa em
futuras instalações com hardware parecido.
