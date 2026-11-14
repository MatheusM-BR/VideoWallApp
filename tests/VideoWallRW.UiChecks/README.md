# Verificações WPF

Execute da raiz do repositório em Windows:

```powershell
dotnet run --project tests/VideoWallRW.UiChecks/VideoWallRW.UiChecks.csproj
dotnet run --project tests/VideoWallRW.UiChecks/VideoWallRW.UiChecks.csproj -- --auxiliary
```

A primeira execução verifica o inspetor, entrada de dados, teclado, automação acessível e ligação com o modelo. Também renderiza a janela principal em três tamanhos e quatro densidades. A segunda cobre as janelas auxiliares e interações do editor com mídia fictícia. Qualquer asserção malsucedida encerra com erro.

O motor de saída não é iniciado. As imagens geradas ficam em `artifacts/ui-review/`. As renderizações de densidade não substituem testes em monitores físicos com diferentes escalas do Windows.
