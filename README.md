# RW VideoWall (VideoWallRW)

Media server para Windows no estilo Resolume Arena, feito para as telas de fundo dos
estúdios da Remate Web: grade de camadas × colunas, fontes de arquivo (vídeo, imagem,
HAP), NDI, saída multi-tela com recortes (Advanced Output), autopilot, editor de imagem,
módulo Agenda (banners de leilão), OSC e controle remoto web.

Stack: C# / .NET 8, WPF, Direct3D 11 (Vortice), FFmpeg (FFmpeg.AutoGen), NDI, NAudio.

## Estrutura

| Pasta | O que é |
|---|---|
| `src/VideoWallRW.App` | Interface WPF, editor de imagem, Agenda, OSC, controle remoto |
| `src/VideoWallRW.Engine` | Render thread D3D11, composição, saídas e slices |
| `src/VideoWallRW.Media` | Decodificação FFmpeg/HAP, áudio, NDI |
| `src/VideoWallRW.Core` | Modelo de domínio, comandos, persistência (sem dependência de UI/GPU) |
| `src/VideoWallRW.Agenda` | Raspagem da agenda de leilões |
| `src/VideoWallRW.Watchdog` | Supervisor que reinicia o app em instalações sem operador |
| `tests/` | Testes xUnit (Core, Agenda) e checagens de layout (UiChecks) |
| `installer/` | Script Inno Setup do instalador |
| `docs/` | Arquitetura, fases, operação, desempenho |

## Preparar a máquina de desenvolvimento

1. **.NET 8 SDK** — `winget install Microsoft.DotNet.SDK.8` (ou o `build.ps1`, que usa
   um SDK instalado em modo usuário em `%LOCALAPPDATA%\Microsoft\dotnet`).
2. **DLLs de terceiros (não vêm no repositório)** — coloque em:
   - `third_party/ffmpeg/`: `avcodec-61.dll`, `avdevice-61.dll`, `avfilter-10.dll`,
     `avformat-61.dll`, `avutil-59.dll`, `swresample-5.dll`, `swscale-8.dll`
     — FFmpeg 7.x **shared**, build LGPL win64
     ([gyan.dev](https://www.gyan.dev/ffmpeg/builds/) ou
     [BtbN](https://github.com/BtbN/FFmpeg-Builds)).
   - `third_party/ndi/`: `Processing.NDI.Lib.x64.dll` — do NDI SDK/Runtime
     ([ndi.video](https://ndi.video/for-developers/ndi-sdk/)). A licença já está no repo.

   Sem as DLLs do FFmpeg o app não inicia; sem a do NDI ele roda, mas a aba Sources
   mostra o NDI como indisponível.

## Compilar, rodar e testar

```powershell
.\build.ps1                                  # dotnet build com warnings como erro
.\run.ps1                                    # compila e abre o app
dotnet test tests/VideoWallRW.Core.Tests     # testes de núcleo
dotnet test tests/VideoWallRW.Agenda.Tests   # parser da agenda
.\publish.ps1                                # build portátil em artifacts\publish\
.\build-installer.ps1 -Version 1.2.0         # instalador (PowerShell 7 + Inno Setup)
```

## Parâmetros de linha de comando

| Argumento | Efeito |
|---|---|
| `--kiosk` | Modo instalação: abre maximizado e retoma o último projeto e o que estava no ar |
| `--resume` | Usado no reinício automático depois de uma falha do engine de vídeo |
| `--verify-package` | Só verifica a integridade do pacote instalado e sai |

## Documentação

- [ARQUITETURA.md](ARQUITETURA.md): princípios, threads, modelo de domínio
- [docs/DESENVOLVIMENTO.md](docs/DESENVOLVIMENTO.md): convenções e status das fases
- [docs/INSTALACAO.md](docs/INSTALACAO.md): modo instalação fixa e watchdog
- [docs/CONTROLE-REMOTO.md](docs/CONTROLE-REMOTO.md) e [docs/OSC.md](docs/OSC.md)
- [docs/PERF.md](docs/PERF.md): dimensionamento e medições
- [THIRD-PARTY.md](THIRD-PARTY.md): licenças de terceiros
