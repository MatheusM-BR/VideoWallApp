namespace VideoWallRW.App.Manual;

/// <summary>Um bloco do manual. Texto aceita **negrito** e `tecla`.</summary>
public abstract record ManualBlock;
public sealed record Paragraph(string Text) : ManualBlock;
public sealed record Bullets(params string[] Items) : ManualBlock;
public sealed record Steps(params string[] Items) : ManualBlock;
public sealed record Picture(string File, string Caption) : ManualBlock;
public sealed record Tip(string Text) : ManualBlock;
public sealed record Warning(string Text) : ManualBlock;
public sealed record KeyTable(params (string Keys, string Action)[] Rows) : ManualBlock;
public sealed record Subtitle(string Text) : ManualBlock;

public sealed record ManualSection(string Title, string Summary, params ManualBlock[] Blocks);

/// <summary>
/// Conteúdo do manual (botão "📖 Manual" no topo da tela principal). Os prints ficam em
/// Assets/Manual/*.png e são gerados pelas verificações de interface com dados fictícios
/// (tests/VideoWallRW.UiChecks/make-manual-prints.ps1) — rode de novo quando a tela mudar.
/// </summary>
public static class ManualContent
{
    public static IReadOnlyList<ManualSection> Sections { get; } =
    [
        new("Visão geral", "O que é cada parte da tela principal",
            new Paragraph("O RWVideoWall coloca vídeos, imagens e câmeras NDI nos telões do estúdio. A tela principal é dividida em blocos que ficam sempre no mesmo lugar:"),
            new Picture("home.png", "Tela principal (dados de demonstração)"),
            new Bullets(
                "**Topo** — menus Arquivo e Ferramentas, Agenda, Sequência, **📖 Manual**, **📡 Controle remoto**; à direita, o controle **SAÍDA** (intensidade geral), **AUTO** (troca automática) e **BLACKOUT**.",
                "**Composição** — a grade: cada linha é uma **camada**, cada coluna é um momento do programa. Cada célula guarda um clipe (vídeo, imagem ou câmera NDI).",
                "**Cenas** — grades inteiras salvas (ex.: Abertura, Programação, Intervalo). Clicar numa cena troca a grade de operação.",
                "**Saída / Clipe selecionado** (canto inferior esquerdo) — miniatura do que está no ar e do clipe que você está editando.",
                "**Clipe** (inspetor) — reprodução, troca automática, tamanho e posição do clipe selecionado.",
                "**Biblioteca** (direita) — arquivos do disco (pasta padrão `C:\\Video Wall`) e fontes NDI da rede.",
                "**Saídas** (rodapé) — liga/desliga cada telão e escolhe o que ele mostra."),
            new Subtitle("Cores"),
            new Bullets(
                "**Azul** — seleção e edição (o que você está mexendo).",
                "**Verde** — no ar / habilitado (clipe ATIVO, saída ligada, AUTO ligado).",
                "**Vermelho** — blackout ou erro."),
            new Tip("O selo **▶ ATIVO** mostra o clipe conectado à camada. Ele não confirma que o telão físico está recebendo sinal — confira a régua Saídas.")),

        new("Montar a grade", "Colocar mídia nas células, camadas e colunas",
            new Steps(
                "Na **Biblioteca → Arquivos**, navegue até a pasta da mídia (📂 escolhe outra pasta, ⬆ sobe um nível).",
                "**Arraste** o arquivo para a célula desejada — ou selecione a célula e dê **duplo-clique** no arquivo.",
                "Use **+ Mídia** para importar vários arquivos de uma vez, **+ Camada** e **+ Coluna** para aumentar a grade."),
            new Bullets(
                "Clique no **nome** do clipe para selecionar e editar (não vai ao ar). Clique na **miniatura** para disparar (vai ao ar).",
                "Botão direito na célula: limpar, editar imagem, vincular/desvincular células.",
                "Botão direito no número da coluna: limpar, clonar, adicionar antes/depois, remover coluna.",
                "**Ctrl+clique** marca várias células; **Vincular células** cria um **grupo de troca** (🔗): trocar a mídia de uma troca a de todas.",
                "`Ctrl+C` / `Ctrl+V` copiam e colam o clipe selecionado. ↶ e ↷ desfazem e refazem alterações da grade."),
            new Tip("Mídia nova entra com **Próximo clipe** ao terminar, e imagem entra com **7 segundos** no ar — ajuste no inspetor se precisar.")),

        new("Operar ao vivo", "Disparar, troca automática, saída e blackout",
            new Bullets(
                "**Miniatura** da célula: coloca o clipe no ar naquela camada.",
                "**Número da coluna** (ou teclas `1`…`9`, `0`): dispara a coluna inteira em todas as camadas — exceto camadas **📌 fixas**.",
                "**AUTO · LIG.** — cada clipe, ao terminar, chama o próximo conforme a regra de troca. **AUTO · DESL.** segura cada camada no clipe atual (o vídeo continua tocando; não é pausa).",
                "**SAÍDA** — intensidade geral de todos os telões (0–100%).",
                "**BLACKOUT** — corta todas as saídas na hora; clicar de novo restaura (o vídeo não volta ao início).",
                "**Sequência** — liga o sequenciador de colunas configurado em Ferramentas → Instalação."),
            new Picture("home-blackout.png", "Blackout ativo: o botão e o monitor de saída ficam vermelhos"),
            new Subtitle("Camadas"),
            new Bullets(
                "**B** (bypass) tira a camada da composição; **S** (solo) mostra só ela.",
                "**Opac.** — opacidade; **Trans.** — tempo de transição (dissolve) ao trocar de clipe, em segundos.",
                "A lista ao lado do nome define o que a camada faz quando cada clipe termina (Manter, Próximo, Anterior, Aleatório, Primeiro).",
                "**📌 Fixa** (menu da camada): disparar coluna pula essa camada — use na linha da câmera/entrada ao vivo.",
                "✕ remove a camada (sempre fica pelo menos uma).")),

        new("Ajustar o clipe", "Tempo, troca automática, tamanho e posição",
            new Paragraph("Selecione o clipe clicando no **nome** dele. O inspetor **CLIPE** mostra as opções. Se o clipe estiver no ar, aparece o aviso **CLIPE ATIVO** — as mudanças aparecem no telão na hora."),
            new Subtitle("Reprodução"),
            new Bullets(
                "**Nome** — como o clipe aparece na grade.",
                "**Entrada / saída** — trecho do vídeo que toca (a barra azul também ajusta: arraste as pontas).",
                "**Duração** — tempo no ar de uma imagem (ou trecho do vídeo).",
                "**Velocidade** e **Volume** (Silenciar corta o som deste clipe)."),
            new Subtitle("Troca automática"),
            new Bullets(
                "**Modo**: Repetir · Ida e volta · Tocar e manter (para no último quadro) · Tocar e retirar.",
                "**Ao terminar**: Manter clipe · Próximo · Anterior · Aleatório · Primeiro clipe da camada."),
            new Subtitle("Tamanho e posição"),
            new Bullets(
                "**Enquadramento**: Ajustar (Fit, cabe inteiro), Preencher (Fill, corta as sobras), Esticar (deforma), Original (1:1).",
                "Arraste a imagem no quadro para mover; as alças redimensionam. X/Y e Largura/Altura em pixels.",
                "Nos campos: arrastar ajusta, **Shift** = ajuste fino, **duplo-clique** = centro/tamanho padrão, **botão direito** = digitar o valor.",
                "No quadro: setas movem 1 px, `Shift`+setas 10 px, `Home` centraliza. **↺ Redefinir** volta ao enquadramento.")),

        new("Cenas e projetos", "Várias grades, salvar e recuperação",
            new Bullets(
                "**+ Nova cena** cria uma grade em branco e já abre nela. Clicar numa cena troca a grade inteira de operação.",
                "Botão direito na cena: **Renomear** / **Excluir**. `PageDown` / `PageUp` percorrem as cenas.",
                "**Arquivo → Salvar** (`Ctrl+S`) grava o projeto (.vwrw) com cenas, saídas e telões ligados.",
                "O programa faz **autosave a cada 2 minutos** numa cópia separada — nunca em cima do seu arquivo.",
                "Se o programa fechar de forma inesperada, na próxima abertura a barra de status avisa e mostra onde ficou o relatório.")),

        new("Biblioteca e NDI", "Arquivos do disco e câmeras da rede",
            new Picture("sources.png", "Biblioteca → Fontes / NDI"),
            new Bullets(
                "**Arquivos** abre em `C:\\Video Wall`. Clique num arquivo para ver a miniatura embaixo.",
                "Depois de salvar uma imagem no editor ou baixar um banner da Agenda, a Biblioteca abre **direto na pasta** com o arquivo selecionado.",
                "**Fontes / NDI** lista as câmeras e computadores que transmitem NDI na rede. Arraste para a grade ou dê duplo-clique.",
                "**Recentes** guarda as últimas mídias usadas; clicar numa delas mostra a miniatura no painel de baixo, igual à lista de pastas."),
            new Tip("A conexão NDI fica aberta por 90 segundos depois que a câmera sai do ar: voltar para ela (inclusive pelo AUTO) é instantâneo. Enquanto está no telão, o tally da câmera acende."),
            new Warning("Se uma fonte NDI não aparece: confira se o computador está na mesma rede da câmera e se o Firewall do Windows permite o RWVideoWall.")),

        new("Telões e saídas", "Ligar telas e mapear recortes",
            new Paragraph("Na régua **Saídas** (rodapé), marque a tela para ligar e escolha a origem (composição inteira ou uma camada). **↻** atualiza a lista de monitores."),
            new Picture("outputs.png", "Ferramentas → Mapear saídas"),
            new Bullets(
                "**Telas**: cada monitor/TV. Arraste no cenário para mover e redimensionar.",
                "**TV física**: Deitada (16:9) ou Em pé ⟳/⟲ (TV girada — recorte 9:16 com rotação).",
                "**Recortes**: que parte da composição vai para cada tela (entrada) e onde aparece (saída). Espelhar H/V e Duplicar.",
                "**Correção de cor**: brilho, contraste e gamma por tela.",
                "**Perfil**: salve e carregue mapeamentos prontos (ex.: estúdio A, estúdio B).")),

        new("Agenda de leilões", "Banners e documentos do site",
            new Picture("agenda.png", "Agenda: leilões do dia, prévia do banner e documentos"),
            new Steps(
                "Clique em **Agenda** no topo. Use ‹ › ou **Hoje** para escolher o dia.",
                "Selecione o leilão na lista. A prévia do banner aparece ao lado.",
                "**Salvar banner** grava em `C:\\Video Wall\\Agenda\\<data>-<leilão>\\` com o **mesmo nome da pasta** — e a Biblioteca abre nele.",
                "**Salvar e abrir no editor** já leva o banner para ajustes (recorte 16:9, texto etc.).",
                "Em **Documentos do evento**, baixe o **Catálogo** ou o **Regulamento** em PDF — abre o importador de páginas."),
            new Picture("agenda-documents.png", "Documentos do evento")),

        new("Páginas de PDF", "Transformar uma página em imagem",
            new Picture("pdf.png", "Importador de PDF"),
            new Steps(
                "Escolha a página na lista à esquerda.",
                "Ajuste a **Resolução (DPI)** — 150 a 300 é suficiente para telão.",
                "**Exportar e abrir no editor**: a página vira PNG na pasta do PDF e abre no editor.")),

        new("Editor de imagem", "Visão geral, salvar e arrastar para a grade",
            new Picture("editor-layers.png", "Editor de imagem — painel Camadas"),
            new Paragraph("Abra pelo botão direito numa célula com imagem (**Editar imagem...**), pela Agenda ou pelo PDF. As categorias ficam no topo; as opções da ferramenta, à direita."),
            new Bullets(
                "Por padrão as **camadas ficam mescladas**: a foto é a própria imagem, e forma, texto ou imagem adicionada podem ser ajustados enquanto estão selecionados — ao trocar de ferramenta, clicar fora ou criar outro elemento, eles se fundem na foto (`Ctrl+Z` desfaz). Clique em **▧ Camadas: mescladas** (rodapé ou painel Camadas) para ligar o **modo camadas** e manter cada elemento separado, editável depois.",
                "Na barra superior, à direita: **conta-gotas**, **balde** e a **cor atual** (clique para trocar), em qualquer ferramenta.",
                "**🧲 Ímãs** (rodapé da área de edição): ao mover uma camada ela gruda no centro e nas bordas da tela e de outras camadas, com uma linha guia rosa. Segure `Alt` para mover livre.",
                "Roda do mouse aproxima/afasta (no ponto do mouse). **Segure a roda e arraste** para mover a visualização, ou use as **barras de rolagem**; `Shift`+roda rola para os lados. **Ajustar à tela** volta ao enquadramento.",
                "**Desfazer / Refazer** no topo (até 20 passos)."),
            new Subtitle("Salvar"),
            new Bullets(
                "**Salvar imagem** grava na pasta da imagem original como **`16x9 - Edited.png`** (paisagem) ou **`9x16 - Edited.png`** (retrato).",
                "Se já existir, a nova entra como **`v2`**, **`v3`**… — nenhuma edição anterior é apagada. Na mesma sessão do editor, salvar de novo atualiza o mesmo arquivo.",
                "Depois de salvar, a Biblioteca abre na pasta com o arquivo selecionado.",
                "**Arraste para a grade** (chip azul) salva e já arrasta a imagem para uma célula.",
                "Se o editor foi aberto a partir de uma célula, salvar já atualiza o clipe — inclusive no ar.")),

        new("Editor · Pincéis", "Pincel, borracha, balde e conta-gotas",
            new Picture("editor-brush.png", "Pincéis com ícones"),
            new Bullets(
                "**Pincel** pinta com a cor e o tamanho escolhidos.",
                "**Borracha**: numa camada abre transparência (aparece o fundo); no fundo, volta à cor de fundo.",
                "**Preencher** (balde) pinta uma área de cor parecida — ajuste a **Tolerância**.",
                "**Conta-gotas** pega a cor de qualquer ponto da imagem e **já passa para o balde** — o próximo clique preenche com a cor pega. As cores pegas ficam no **Histórico**.",
                "O painel mostra **Pintando em:** — com as camadas mescladas é sempre a **Imagem**. No modo camadas, os pincéis agem na camada de imagem selecionada (contorno azul tracejado); começando o traço **fora** dela, a tinta vai para o fundo.")),

        new("Editor · Formas e texto", "Inserir como camadas editáveis",
            new Picture("editor-shape.png", "Formas 2D"),
            new Subtitle("Formas 2D"),
            new Steps(
                "Escolha Retângulo, Elipse ou Linha, a cor, Preenchida e a espessura.",
                "**Arraste** na imagem — ao **soltar o clique**, a forma já vira camada (não precisa concluir).",
                "Arraste por dentro para mover; puxe os cantos para redimensionar (`Shift` mantém a proporção).",
                "Com a forma selecionada, mudar cor, preenchimento ou espessura altera ela. `Del` exclui."),
            new Picture("editor-text.png", "Texto"),
            new Subtitle("Texto"),
            new Steps(
                "Digite o texto no campo.",
                "**Clique** na imagem para inserir no **Tamanho** do campo — ou **arraste uma caixa**: a altura dela define o tamanho da letra.",
                "O texto vira camada: arraste para mover, cantos para redimensionar (sem perder nitidez).",
                "Com o texto selecionado, editar o campo, o tamanho ou a cor muda ele na hora.")),

        new("Editor · Seleção", "Marcar uma área e mover ou esticar só ela",
            new Paragraph("A ferramenta **⬚ Seleção** transforma só um pedaço da imagem — por exemplo, alongar uma faixa de fundo até a borda sem mexer no resto."),
            new Picture("editor-select.png", "Área selecionada sendo esticada para a direita"),
            new Steps(
                "Clique em **Seleção** e **arraste** na imagem para marcar a área (`Shift` deixa quadrada). `Ctrl+A` seleciona a imagem inteira.",
                "Arraste **por dentro** para mover, ou puxe as **alças**: as dos **cantos** esticam nos dois sentidos (`Shift` mantém a proporção); as do **meio dos lados** esticam num sentido só.",
                "**Enter** (ou **Aplicar**) grava. Clicar fora ou trocar de ferramenta também grava. **Esc** (ou `Ctrl+Z`) cancela e a imagem volta como estava."),
            new Bullets(
                "Os campos **Largura/Altura** definem o tamanho exato em pixels.",
                "**Tela inteira**, **↔ Largura** e **↕ Altura** esticam a área até as bordas da imagem.",
                "**Apagar** (ou `Delete`) limpa a área — fica a cor de fundo.",
                "Setas movem 1 px (`Shift` 10 px). Enquanto a área está solta, o lugar de onde ela saiu fica com a cor de fundo."),
            new Tip("No **modo camadas**, a seleção age na camada de imagem que está debaixo dela; com as camadas mescladas (padrão), age na imagem.")),

        new("Editor · Camadas, tela e recorte", "Organizar, mudar o tamanho da tela e recortar",
            new Subtitle("Camadas"),
            new Bullets(
                "Lista com todas as camadas (🖼 imagem, T texto, ◆ forma); a de baixo da lista fica por cima.",
                "Caixa ao lado do nome mostra/oculta. ▲ ▼ mudam a ordem. **Excluir** remove.",
                "**Centralizar**, **Centro ↔**, **Centro ↕** e **Ocupar a tela** posicionam a camada selecionada.",
                "**+ Adicionar imagem** traz um logo/selo como camada nova. **Mesclar** grava tudo no fundo (só quando quiser fixar).",
                "Setas movem 1 px (`Shift` 10 px) a camada selecionada."),
            new Picture("editor-canvas.png", "Tela: mudar o tamanho da arte"),
            new Subtitle("Tela"),
            new Bullets(
                "Digite largura/altura (px ou %) ou arraste as guias nas bordas. A arte fica centralizada na tela nova.",
                "O botão **Aplicar tamanho** fica **azul** quando há um tamanho novo pendente e volta ao **cinza** depois de aplicado.",
                "**Redimensionar imagem com a tela**: marcado estica junto; desmarcado só aumenta a área (útil para transformar 16:9 em 9:16 com fundo).",
                "**Tela transparente**: a área nova entra sem cor (para logos que vão por cima de outra arte)."),
            new Picture("editor-crop.png", "Recortar"),
            new Subtitle("Recortar"),
            new Bullets(
                "Escolha o enquadramento (Original, Livre, 16:9, 9:16, 1:1, 4:3, Composição, Slices).",
                "Arraste dentro do quadro para mover, nas alças para ajustar. `Enter` aplica, `Esc` redefine.")),

        new("Controle remoto", "Operar o telão pelo celular",
            new Picture("remote.png", "Janela Controle remoto (exemplo ligado)"),
            new Paragraph("Com o controle remoto ligado, um celular, tablet ou computador **na mesma rede** abre uma página com a grade e consegue disparar clipes, colunas e cenas, mexer na opacidade das camadas e no master, e dar blackout."),
            new Steps(
                "Clique em **📡 Controle remoto** no topo da tela principal.",
                "Clique em **Ligar controle remoto**. A janela mostra o **endereço** desta máquina (ex.: `http://192.168.0.20:8080`) e o **PIN**.",
                "No celular (mesma rede), abra o endereço no navegador e digite o PIN."),
            new Bullets(
                "Ele **começa sempre desligado** e o PIN **muda a cada vez que liga**.",
                "Com ele ligado, o botão do topo fica verde e mostra quantos aparelhos estão conectados.",
                "5 PINs errados bloqueiam o aparelho por 2 minutos. Desligar derruba quem estiver conectado."),
            new Warning("Se o celular não abrir a página: confira se está na mesma rede e permita o RWVideoWall no Firewall do Windows (redes privadas) — o Windows pergunta na primeira vez."),
            new Subtitle("Entrada OSC (Companion, TouchOSC)"),
            new Paragraph("Para controlar por um Stream Deck/Companion ou TouchOSC, ligue **Ferramentas → Entrada OSC (porta 7000)**. Ela **começa desligada** a cada abertura e **não tem senha** — ligue só quando for usar. Os endereços (disparar clipe, coluna, cena, opacidade, master, blackout) estão em `docs/OSC.md`.")),

        new("Instalação fixa", "Sequência, horários e pastas monitoradas",
            new Picture("installation.png", "Ferramentas → Instalação"),
            new Bullets(
                "**Iniciar com o Windows** abre o programa no modo instalação (retoma o último projeto e o que estava no ar).",
                "**Sequenciador**: passos de coluna + segundos; **Repetir** recomeça. Liga pelo botão **Sequência** do topo.",
                "**Agenda**: regras por horário e dia da semana (disparar coluna, cena, blackout, iniciar/parar sequência).",
                "**Pastas monitoradas**: arquivos novos numa pasta entram sozinhos numa camada."),
            new Tip("Se o motor de vídeo falhar (driver da placa, por exemplo), o programa salva o estado, reinicia sozinho e religa os telões. Se falhar 3 vezes em 10 minutos, ele para e mostra o erro — aí verifique placa e driver.")),

        new("Conversor HAP", "Vídeos leves para muitas camadas",
            new Picture("hap.png", "Ferramentas → Conversor HAP"),
            new Paragraph("HAP é um formato que a placa de vídeo decodifica quase sem usar processador — ideal para várias camadas de vídeo ao mesmo tempo ou 4K. Adicione os arquivos, escolha o perfil e o destino e clique em **Converter tudo**. Use o arquivo convertido na grade normalmente."),
            new Subtitle("Decodificar vídeo pela GPU (experimental)"),
            new Paragraph("Para vídeos comuns (MP4 H.264/H.265), **Ferramentas → Decodificar vídeo pela GPU** usa a placa de vídeo em vez do processador — ajuda com 4K e várias camadas de vídeo. Começa desligado e vale para os clipes abertos depois de ligar. Se algum vídeo não funcionar pela GPU, ele volta sozinho para o processador sem sair do ar."),
            new Tip("Teste numa máquina antes de usar no estúdio: ligue, toque os vídeos de um evento e acompanhe o uso de CPU e de \"Video Decode\" da GPU no Gerenciador de Tarefas.")),


        new("Atalhos de teclado", "Padrões e como mudar",
            new KeyTable(
                ("1 … 9, 0", "Disparar colunas 1 a 10"),
                ("Shift+1 … Shift+0", "Disparar colunas 11 a 20"),
                ("Ctrl+Espaço", "Liga/desliga a troca automática (AUTO)"),
                ("Espaço", "AUTO, fora de campos de texto"),
                ("B", "Blackout"),
                ("X / Ctrl+X", "Retirar a camada selecionada / todas"),
                ("F", "Ativar/desativar saídas"),
                ("PageDown / PageUp", "Próxima / cena anterior"),
                ("Ctrl+S / Ctrl+O / Ctrl+N", "Salvar / abrir / novo projeto"),
                ("Ctrl+C / Ctrl+V", "Copiar / colar clipe"),
                ("Ctrl+Z / Ctrl+Y", "Desfazer / refazer no editor"),
                ("Ctrl+A", "Editor: selecionar a imagem inteira (Seleção)")),
            new Picture("keymap.png", "Ferramentas → Atalhos de teclado"),
            new Paragraph("Em **Ferramentas → Atalhos de teclado** dá para trocar qualquer tecla e, nos disparos de coluna, escolher qual camada recebe o comando. **Restaurar padrão** volta à tabela acima.")),

        new("Solução de problemas", "O que fazer quando algo não sai como esperado",
            new Bullets(
                "**Telão preto com a grade funcionando** — confira BLACKOUT, SAÍDA em 100%, a régua Saídas (tela marcada) e o cabo/monitor.",
                "**Barra de status: \"o engine de vídeo falhou e o programa foi reiniciado\"** — o programa se recuperou sozinho. Se repetir, atualize o driver da placa de vídeo.",
                "**Clipe não entra no ar** — arquivo apagado/movido ou corrompido: o programa pula e segue com o que está no ar. Arraste o arquivo de novo.",
                "**Câmera NDI demora ou não aparece** — mesma rede, Firewall liberado, nome da fonte na aba Fontes / NDI.",
                "**Celular não abre o controle remoto** — controle ligado? mesma rede? Firewall?",
                "**Logs** ficam em `%LocalAppData%\\VideoWallRW\\logs` e relatórios de fechamento inesperado em `%LocalAppData%\\VideoWallRW\\crashes` — envie para análise quando algo se repetir.")),
    ];
}
