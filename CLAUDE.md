# Notas do projeto (para o Claude e para quem mexer no código)

- **Compilar só no Docker:** `.\build.ps1` → `dist\PokeDeck.exe` (pasta fora do git). Não instalar SDKs no PC. Se o
  PokeDeck estiver aberto, o build para e pede para fechar: não gerar em outra pasta.
- **Não abrir o exe de dentro do Claude** (Start-Process etc.): o Claude é um app MSIX e o Windows redireciona o
  AppData do processo para a pasta do pacote dele; logins e config feitos ali somem ao abrir pelo Explorer. Quem abre
  é o usuário (ou `explorer.exe "<caminho do exe>"`).
- **Releases:** publicar uma release com tag `vX.Y.Z` roda `.github/workflows/release.yml`, que compila com o
  Dockerfile e anexa o `PokeDeck.exe`. `v0.1.0` publicada em 26/11/2026. O repo é público e amigos usam as releases:
  manter compatibilidade do `config.json` entre versões.
- **Protocolos:** espelhados do [PokeIdleMulti](https://github.com/artbruno35/PokeIdleMulti). Antes de mexer em
  jogo/Twitch/Kick, conferir como ele faz.
- **Anti-bot:** nada de cliques sintéticos; ações no jogo pelas mensagens do socket do próprio jogo; pontos/resgates
  rodam dentro da página real do site (AgentPage); todo intervalo passa pelo `Jitter`.
- **Onde fica cada coisa:** lógica pura e testada em `src/PokeDeck.Core` (testes em `tests/`), host WebView2 em
  `src/PokeDeck`, interface Svelte em `ui/`.
