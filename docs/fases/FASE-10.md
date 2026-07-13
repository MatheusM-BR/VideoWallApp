# Fase 10 — Módulo Agenda: banner de eventos (programaleiloes.com) + editor de imagem

## Objetivo
Fluxo completo de captura de arte do evento sem sair do programa:
**agenda por data → evento → banner em alta + catálogo PDF → página do PDF vira
imagem → editor embutido (cortar/esticar/pintar/proporção) → imagem na grade.**

O operador monta o telão do leilão do dia em minutos, sem navegador nem Paint.

## Posição no roadmap
Depende apenas da **Fase 3 concluída** (grade + seleção de célula + imagens).
Pode ser desenvolvida **em paralelo** às fases 4–8 — não toca no engine de render
(exceto um comando novo `ReloadClip`).

## Estrutura do site (verificada em 13/07/2026 — ver Armadilhas sobre mudanças)

| O quê | Como |
|-------|------|
| Agenda do dia | `GET https://programaleiloes.com/agenda/{dd-MM-yyyy}` — **server-rendered** (HttpClient puro funciona, sem WebView) |
| Lista de eventos | `div.leiloes-area ul li a[href^="/agenda/leilao/"]` |
| Campos do evento | dentro do `<a>`: `.dia-semana`, `.data .dia`, `.data .mes`, `.horario .hora-tempo`, `.transmissao img@src` (logo do canal), `.informacoes .titulo`, `.informacoes .categoria`, `.informacoes .local`, `.informacoes .cidade` |
| Id/slug do evento | do href: `/agenda/leilao/{id}-{slug}` (ex.: `90494-live-mega-remate-maquinas-e-implementos`) |
| Página do evento | título em `h1`; **banner**: `a.fancybox@href` → `/files/leiloes/{HASH}.jpg` (**original em alta**; o `img.folder` interno é preview 640px em `/images/leiloes/1/640/0/{HASH}/{slug}.jpg`) |
| Downloads | `div.left.downloads a`: catálogo `/leilao/download/{id}-catalogo-do-leilao-em-pdf` (serve `application/pdf` inline — observado 35MB+) e regulamento `/leilao/regulamento/{id}-{slug}` (também PDF) |
| Fallback de banner | `meta[property="og:image"]` (340×340 — só se `a.fancybox` não existir) |

> Nota: a classe `fancybox-image` só existe no lightbox em runtime (gerada por JS).
> No HTML-fonte o seletor estável é `a.fancybox`.

## Entregáveis
1. Projeto `src/VideoWallRW.Agenda` (cliente HTTP + parser + downloads; sem UI).
2. UI "Agenda de Leilões" (navegação por data, lista, painel do evento).
3. Download de banner em alta + PDF com progresso; PDF → PNG por página.
4. Editor de imagem (crop com presets de proporção, esticar, pintar, texto, undo).
5. Biblioteca de assets do projeto + integração com a grade (`enviar para célula`,
   `editar da célula`, `ReloadClip`).

NuGet: `HtmlAgilityPack` (Agenda), `PDFtoImage` (pdfium; Agenda), `SkiaSharp` +
`SkiaSharp.Views.WPF` (App — editor).

---

## Tarefas

### T10.1 — VideoWallRW.Agenda: cliente e parser
```csharp
public sealed record AgendaEvent(int Id, string Slug, string Url, string Title,
    string Category, string City, string Venue, TimeOnly? Time, DateOnly Date,
    string? ChannelLogoUrl);
public sealed record EventDetails(string Title, string? BannerUrl,
    string? BannerPreviewUrl, IReadOnlyList<EventDownload> Downloads);
public sealed record EventDownload(string Url, string Label, DownloadKind Kind); // Catalog | Regulation | Other

public sealed class AgendaClient
{
    public Task<IReadOnlyList<AgendaEvent>> GetEventsAsync(DateOnly date, CancellationToken ct);
    public Task<EventDetails> GetEventAsync(string eventUrl, CancellationToken ct);
    public Task DownloadFileAsync(string url, string destPath,
        IProgress<double>? progress, CancellationToken ct);   // stream p/ disco, nunca MemoryStream
}
```
- `HttpClient` único: base URL **configurável** (settings; default
  `https://programaleiloes.com`), User-Agent identificável (`VideoWallRW/1.0`),
  timeout 30s, 1 retry em falha transitória.
- Data na URL com `CultureInfo.InvariantCulture` (`dd-MM-yyyy`).
- Parser HtmlAgilityPack com os seletores da tabela acima em **constantes numa
  única classe** (`AgendaSelectors`) — quando o site mudar, mexe-se num lugar só.
- Parse tolerante: campo ausente → string vazia/null, nunca exceção; zero
  eventos + página sem `.leiloes-area` → erro específico `SiteLayoutChanged`
  (salvar o HTML recebido em `%LocalAppData%\VideoWallRW\logs\` para diagnóstico).
- **Fixtures**: na primeira execução da implementação, salvar o HTML real de uma
  agenda e de um evento em `tests/fixtures/` e escrever testes de parse sobre
  eles (o teste não bate na rede).
- Cache: agenda por data em memória 5 min; previews de banner em
  `%LocalAppData%\VideoWallRW\agenda-cache\` (limpar > 30 dias no startup).

### T10.2 — UI da agenda
- Janela "Agenda de Leilões" (menu Ferramentas + botão na toolbar):
  - Cabeçalho: DatePicker + botões ◀ dia anterior / hoje / dia seguinte ▶
    (abre já no dia atual).
  - Lista de eventos: hora, título, categoria, cidade, logo do canal; loading
    spinner durante fetch; erro de rede → mensagem amigável + botão repetir.
  - Clique no evento → painel de detalhe na mesma janela: preview do banner
    (640px, carregado async), título, e ações:
    **[Salvar banner] [Salvar banner e editar] [Catálogo PDF...] [Regulamento PDF...]**.
- Toda a UI é async/await com `CancellationToken` amarrado ao fechar da janela.

### T10.3 — Download do banner e biblioteca de assets
- Destino: `{pastaDoProjeto}\Assets\Agenda\{yyyy-MM-dd}-{slug}\banner.jpg`
  (projeto não salvo → `%Documents%\VideoWallRW\Assets\...`; configurável).
- Baixar **sempre o href do `a.fancybox`** (original em alta). Sem `a.fancybox`
  → fallback `og:image` com aviso ("banner em baixa resolução").
- Gravar `meta.json` na pasta do evento: URLs de origem, título, data, quando
  baixou — auditoria e re-download.
- `AssetLibrary` (Core): lista leve dos assets do projeto (caminho + origem);
  os clips continuam referenciando `FileImageSource(path)` normal — o asset é
  metadado, não um novo tipo de fonte.
- Pós-salvar: conforme o botão, abre o editor (T10.5) ou pergunta a célula
  destino (T10.6).

### T10.4 — PDF → imagem
- Download com diálogo de progresso (% por `Content-Length`) e **cancelamento** —
  catálogos de 35MB+ são normais. Destino: pasta do evento.
- Visualizador de páginas: `PDFtoImage` renderiza miniaturas (~200px) em
  background, uma a uma (grid preenchendo progressivamente — PDFs de leilão têm
  dezenas de páginas).
- Selecionar página(s) → renderizar em alta: DPI configurável (default 200),
  **maior dimensão limitada a 4096px** (página A3 a 300 DPI explode memória) →
  `page-{n}.png` na pasta do evento → mesmo fluxo pós-salvar do banner.
- Regulamento usa o mesmo fluxo (é PDF).

### T10.5 — Editor de imagem (estilo Paint)
Janela `ImageEditorWindow` com `SKElement` (SkiaSharp). Documento = SKBitmap
base + camada de desenho; salvar achata em PNG.

Ferramentas (toolbar vertical à esquerda, estilo Paint):
- **Crop**: retângulo com handles; **presets de proporção**: livre, 16:9, 9:16,
  1:1, 4:3, **proporção da composição atual** e **proporção de cada slice do
  output** (lidos do projeto — é isso que facilita a vida do operador); campo
  W×H numérico; Enter aplica, Esc cancela.
- **Redimensionar/esticar**: diálogo W×H com "manter proporção" opcional +
  modo transform livre (arrastar handles estica sem manter proporção).
- **Pincel** (tamanho 1–100, cor, opacidade), **borracha**, **conta-gotas**.
- **Formas**: retângulo/elipse/linha, contorno ou preenchido — tarjar telefone,
  cobrir área.
- **Balde** (flood fill com tolerância).
- **Texto**: fonte, tamanho, cor, arrastar para posicionar (sobrepor horário/aviso).
- **Undo/redo**: snapshots de bitmap, máx. 20 níveis (imagens ≤ 4K → aceitável
  e simples; descartar os mais antigos).
- Zoom (Ctrl+scroll, 10%–800%) e pan (espaço+arrastar); fundo quadriculado para
  alpha; barra de status com posição/zoom/tamanho.
- Salvar: **nunca sobrescreve o original** — grava `{nome}-edit.png` ao lado
  (edições subsequentes sobrescrevem o `-edit.png`); botões
  **[Salvar] [Salvar e enviar para célula]**.
- O editor abre qualquer imagem, não só banners (ver T10.6).

### T10.6 — Integração com a grade
- "Enviar para célula": célula selecionada na grade → `UpdateClip` com
  `FileImageSource` do PNG; sem seleção → popup layer/coluna.
- Menu de contexto de célula de imagem: **"Editar imagem..."** → abre T10.5 →
  ao salvar, comando novo `ReloadClip(layerId, column)` no engine (recarrega a
  fonte do disco; se estava no ar, rearma e troca sem frame preto — mesmo fluxo
  de trigger da fase 2).
- Sugestão de nome do clip = título do evento (célula já nasce nomeada).

---

## Armadilhas conhecidas
- **O site é de terceiros e pode mudar o layout.** Por isso: seletores
  centralizados, testes de fixture, erro `SiteLayoutChanged` com HTML salvo.
  Revalidar os seletores (tabela acima) na implementação.
- Baixar o **href** do `a.fancybox`, nunca o `src` do `img` interno (preview
  640px) — a imagem vai para telão.
- `Content-Disposition` do PDF traz nome do servidor (`1772594813L_1.pdf`) —
  ignorar; usar nosso naming padronizado.
- Respeito ao site: só requisições disparadas por ação do usuário, com cache;
  nada de varredura em massa de datas/eventos.
- O banner de cookies é overlay JS — irrelevante para o fetch server-side.
- `PDFtoImage`/pdfium e `SKBitmap` são nativos: `Dispose` rigoroso (leak fácil
  no grid de miniaturas ao trocar de PDF).
- Undo por snapshot: monitorar memória com imagem 4K (20 × ~64MB = muito) —
  limitar snapshots a imagens ≤ 2K de maior dimensão; acima disso, undo de 5 níveis.
- WPF + SkiaSharp: repintar só quando algo muda (`InvalidateVisual` sob demanda),
  não por timer.

## DoD — verificação manual
1. Abrir a Agenda → carrega os eventos de **hoje** automaticamente; navegar 3
   datas para frente/trás funciona.
2. Abrir um evento real → "Salvar banner" grava o JPG **em alta** na pasta do
   evento com `meta.json`.
3. Baixar um catálogo grande (30MB+): progresso real, cancelamento no meio
   funciona e não deixa arquivo corrompido (`.tmp` + rename).
4. Selecionar uma página do PDF → PNG em alta na pasta do evento.
5. Editor: cortar com preset "proporção da composição", tarjar um telefone com
   retângulo preenchido, esticar 10%, escrever um texto, undo/redo de tudo,
   salvar `-edit.png` (original intacto).
6. "Salvar e enviar para célula": imagem aparece na grade nomeada com o título
   do evento e vai ao ar com resize correto.
7. "Editar imagem..." numa célula **no ar** → salvar → telão atualiza sem frame
   preto.
8. Testes de parse (fixtures) passam; derrubar a rede → mensagens amigáveis,
   nada trava.
