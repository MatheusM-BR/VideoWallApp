# Distribuição — rodando em máquinas diferentes

O programa foi desenhado para rodar em PCs variados (CPU, memória e GPU
diferentes). Este documento define os requisitos, como gerar o build portátil
e o que varia de máquina para máquina.

## Requisitos da máquina de exibição (show/instalação)

| Item | Mínimo | Observação |
|------|--------|------------|
| SO | Windows 10 x64 (1809+) ou Windows 11 | |
| CPU | Qualquer x64 (Intel/AMD) | O build `win-x64` cobre os dois; ARM64 sob demanda |
| GPU | Qualquer com Direct3D 11 (feature level 11_0) | NVIDIA/AMD/Intel integrada — tudo desde ~2010 serve para abrir; performance de show depende da GPU (ver abaixo) |
| Driver de vídeo | Atualizado | Causa nº 1 de problema em máquina nova é driver velho |
| .NET | **Nenhum** | O build portátil é self-contained — carrega o runtime junto |
| Memória | 8 GB confortável | O consumo real depende de quantos vídeos simultâneos (medir na fase 6 → `docs/PERF.md`) |

## Como o código lida com hardware variado

- **GPU**: o engine pede feature level 11_1 e aceita 11_0; se o device de
  hardware falhar (driver quebrado, VM), cai para **WARP** (software) — o
  programa abre e permite diagnosticar, com aviso de que performance de show
  exige GPU real.
- **Tearing/multi-monitor**: detectado por máquina (`PresentAllowTearing`);
  monitores com taxas diferentes já são tratados pela estratégia de present.
- **Decodificação (fase 6)**: D3D11VA funciona em NVIDIA/AMD/Intel; quando o
  codec/driver não suporta, o fallback para decode por software é automático.
  Máquinas fracas de CPU devem usar conteúdo **HAP**.
- **Capacidade**: cada máquina aguenta um número diferente de vídeos
  simultâneos. A fase 6 cria o roteiro de medição (`docs/PERF.md`) — rode-o em
  cada modelo de PC usado nos shows para saber o limite de cada um.

## Gerando o build portátil

Na máquina de desenvolvimento:

```powershell
.\publish.ps1
```

Sai em `artifacts\publish\VideoWallRW-win-x64\`. **Copie a pasta inteira** para
a máquina de destino (pendrive/rede) e execute `VideoWallRW.App.exe`. Nada de
instalador, nada de .NET, nada de admin.

> Quando o FFmpeg entrar (fase 1) e o NDI (fase 5), as DLLs deles passam a ser
> copiadas junto pelo publish — o requisito "copiar a pasta inteira" continua
> valendo. Única exceção: o runtime NDI tem instalador próprio (licença),
> documentado na fase 5.

## Máquina de desenvolvimento

- .NET SDK 8 — nesta máquina foi instalado em modo usuário
  (`%LOCALAPPDATA%\Microsoft\dotnet`), e os scripts `build.ps1` / `run.ps1` /
  `publish.ps1` já apontam para ele. Em outra máquina de dev, ou instale o SDK
  normal (`winget install Microsoft.DotNet.SDK.8`) ou rode o mesmo
  `dotnet-install.ps1` da Microsoft.
- Clonar o repositório e rodar `.\build.ps1` — o NuGet baixa as dependências
  sozinho na primeira vez.

## Checklist ao estrear uma máquina nova de show

1. Atualizar o driver de vídeo.
2. Copiar a pasta do build portátil e abrir o `VideoWallRW.App.exe`.
3. Conferir no log (`%LocalAppData%\VideoWallRW\logs\`) qual GPU foi detectada
   e se `tearing true` (se aparecer WARP, o driver/GPU precisa de atenção).
4. Ativar as saídas e rodar o padrão de teste da fase 0 por alguns minutos
   (barra deslizando suave = present saudável).
5. (Pós-fase 6) Rodar o roteiro de `docs/PERF.md` para registrar a capacidade
   da máquina.
6. (Instalação fixa, pós-fase 8) Aplicar o checklist de energia/Windows Update
   de `docs/INSTALACAO.md`.
