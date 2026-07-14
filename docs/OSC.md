# Entrada OSC (Fase 9 T9.4)

O VideoWallRW escuta OSC 1.0 (mensagens simples, sem bundles) em UDP na porta
**7000** (fixa por enquanto — sem UI de configuração ainda). Útil para TouchOSC,
Bitfocus Companion, ou `oscsend` (liblo).

## Endereços suportados

| Endereço | Args | Efeito |
|---|---|---|
| `/vwrw/layer/{n}/clip/{m}/trigger` | — | Dispara o clip da coluna `m` na layer `n` |
| `/vwrw/layer/{n}/opacity` | `f` (0–1) | Opacidade da layer `n` |
| `/vwrw/column/{n}/trigger` | — | Dispara a coluna `n` em todas as layers |
| `/vwrw/scene/{nome}/trigger` | — | Dispara a cena salva com esse nome (T7.5) |
| `/vwrw/master/fader` | `f` (0–1) | Master fader |
| `/vwrw/blackout` | `i` (0 ou 1) | Liga/desliga blackout |
| `/vwrw/pause` | `i` (0 ou 1) | Pausa/retoma o transporte global |

`{n}`/`{m}` são índices numéricos (0-based, mesma indexação da grade na UI).
`{nome}` é o nome exato da cena (comparação exata, sensível a maiúsculas).

Endereços não reconhecidos são descartados silenciosamente (log em nível Debug).

## Testar com `oscsend` (liblo)

```
oscsend localhost 7000 /vwrw/column/0/trigger
oscsend localhost 7000 /vwrw/master/fader f 0.5
oscsend localhost 7000 /vwrw/blackout i 1
```

## Limitações conhecidas

- Sem autenticação — não exponha a porta 7000 fora da LAN confiável.
- Porta fixa (7000), sem UI para trocar — mudar exige recompilar
  (`OscService.Start(port: ...)` em `App.xaml.cs`).
- Sem suporte a OSC bundles (`#bundle`), só mensagens simples — suficiente para
  TouchOSC/Companion, que enviam mensagem a mensagem.
