# Instalação fixa (modo quiosque) — checklist

Guia para colocar o VideoWallRW numa máquina dedicada (telão/vitrine/instalação
permanente), sem operador presente no dia a dia. Cobre a Fase 8.

## 1. Publicar

```
dotnet publish src/VideoWallRW.App -c Release -r win-x64 --self-contained false -o dist
dotnet publish src/VideoWallRW.Watchdog -c Release -r win-x64 --self-contained false -o dist
```

`VideoWallRW.App.exe` e `VideoWallRW.Watchdog.exe` devem ficar **na mesma pasta**
(`dist\`) — o watchdog procura o app ao lado dele por padrão (pode passar o caminho
completo como argumento se preferir outro layout).

## 2. Windows: logon automático + sem tela de bloqueio

- Crie um usuário dedicado (ex.: `videowall`) sem senha ou com logon automático
  configurado (`netplwiz` → desmarcar "usuários devem inserir senha").
- Desative suspensão/hibernação e o protetor de tela (Configurações → Energia).
- Desative atualizações automáticas em horário de exibição, se possível (elas
  reiniciam a máquina sem aviso).

## 3. Projeto e saídas

- Monte o projeto (`.vwrw`) normalmente pelo app, com as telas/slices configuradas
  em "Advanced Output" e testadas em produção.
- Salve o projeto **uma vez pela UI normal** (não em modo kiosk) — o modo kiosk só
  retoma um projeto já salvo, nunca cria um novo.
- Configure sequenciador / agenda / pastas monitoradas em "⚙ Instalação..." (T8.4–T8.6).

## 4. Inicialização automática

Na janela "⚙ Instalação...", marque **"Iniciar com o Windows (--kiosk)"**. Isso grava
`VideoWallRW.App.exe --kiosk` em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
para o usuário logado — sem instalador, sem elevação.

Se você usa o watchdog (recomendado — ele reinicia o app se travar), registre o
**watchdog** para iniciar com o Windows em vez do app direto (ex.: um atalho na
pasta Inicializar apontando para `VideoWallRW.Watchdog.exe`), e desmarque o toggle
acima (senão os dois tentam iniciar o app).

## 5. Watchdog (opcional, recomendado)

`VideoWallRW.Watchdog.exe` inicia `VideoWallRW.App.exe --kiosk`, escuta um heartbeat
por named pipe (`vwrw-heartbeat`) que o app envia a cada 3s pela UI thread. Sem
heartbeat por 10s (app travado) ou o processo caindo sozinho, o watchdog mata e
reinicia. Anti-flapping: mais de 3 reinícios em 5 minutos → espera 60s antes de
tentar de novo. Log em `%LocalAppData%\VideoWallRW\logs\watchdog.log`.

## 6. Testar recuperação de crash

1. Com o app aberto e um projeto carregado, force o encerramento pelo Gerenciador
   de Tarefas (simula uma queda de energia/crash, não um fechamento normal).
2. Reinicie o app normalmente (sem `--kiosk`): ele deve oferecer recuperar o
   autosave (T7.3).
3. Em modo `--kiosk`, o mesmo cenário **não pergunta** — ele retoma sozinho o
   último projeto salvo (`RuntimeState`, gravado a cada 5s) sem diálogo, porque
   não há operador para responder.

## O que NÃO está automatizado

- Driver de GPU / Windows Update continuam por conta do administrador da máquina.
- Sem instalador MSI — a "instalação" é copiar a pasta `dist\` e configurar os
  passos acima manualmente.
- O watchdog não monitora as próprias janelas de saída travarem individualmente
  (ex.: um monitor desconectado) — isso já é tratado dentro do app (saída cai,
  as demais continuam), não é motivo para reiniciar o processo inteiro.
