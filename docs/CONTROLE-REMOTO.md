# Controle remoto web (Fase 9 T9.5)

O app sobe um servidor Kestrel embutido em `http://<ip-da-máquina>:8080` — sem
instalação, sem app separado. Um celular/tablet na mesma rede acessa essa URL pelo
navegador e vê a grade em tempo real (nomes de layer/coluna, sem thumbs).

## PIN

Um PIN de 6 dígitos é sorteado a cada início do app e fica no log
(`%LocalAppData%\VideoWallRW\logs\app-*.log`, linha "Controle remoto web em...").
Ainda não é configurável pela UI (backlog) — reiniciar o app troca o PIN.

PIN errado 5 vezes seguidas bloqueia aquele IP por 2 minutos (e cada erro
espera 1s antes da resposta), pra que o PIN não saia por força bruta.

## O que dá pra fazer

- Ver a grade (layers × colunas, nomes dos clipes).
- Disparar um clip (clique numa célula).
- BLACKOUT (liga/desliga).
- Master fader.

Comandos passam pelos mesmos `EngineCommand` que a UI local usa — o navegador é só
mais um produtor da fila do engine, sem caminho especial no motor de renderização.

## Limitações conhecidas

- Porta fixa (8080), sem UI para trocar.
- Sem HTTPS — não exponha fora de uma LAN confiável.
- Estado empurrado a cada 1s (poll simples), não é diff granular — para uma grade
  muito grande isso é desperdício de banda, mas suficiente para uso normal.
- Sem "disparar cena" nem "trigger de coluna inteira" na UI web ainda (o protocolo
  já aceita `trigger_column`/`trigger_scene`, só falta o botão na página).
