# PokeDeck (pessoal)

Até 4 contas do [pokeidle.io](https://pokeidle.io/app) numa janela, com recompra, ULTRA/Economia, lives leves da
Twitch/Kick, baú da Twitch, resgate automático do bônus e guarda do bônus. Protocolos espelhados do
[PokeIdleMulti](https://github.com/artbruno35/PokeIdleMulti); tempos e regras nossos, todos com variação aleatória.

## Compilar (só Docker, nada instalado no PC)

```powershell
.\build.ps1
```

Sai sempre em `dist\PokeDeck.exe` (pasta fora do git; feche o app antes de compilar). Um arquivo, sem instalar .NET. Precisa do WebView2 Runtime (já vem no Windows 10/11).
O build roda os testes do núcleo antes de gerar o exe.

## Releases

Publicar uma release no GitHub (Releases → *Draft a new release*, tag `v0.2.0`, *Publish*) dispara
`.github/workflows/release.yml`: compila com o mesmo Dockerfile e anexa o `PokeDeck.exe` à release para download.
A versão da tag aparece no título da janela. Para atualizar: feche o app e troque o exe; logins e config ficam.

## Usar

1. Abra o exe. Entre no jogo em cada conta (o login fica no perfil da conta).
2. **⚙ Config** → aba da conta: nome, ULTRA/Economia, recompra (confira os valores: gasta ouro de verdade), lives,
   bônus a resgatar e guarda. Aba **Lives**: canais e quantas lives; aba **Tempos**: todos os intervalos.
3. Na conta com lives: botões **T**/**K** no cabeçalho abrem a janela para entrar na Twitch/Kick daquela conta.

Dados em `%LOCALAPPDATA%\PokeDeck` (`config.json`, `pokedeck.log`, perfis em `WebView2`). Não compartilhe essa pasta:
tem os logins.

## Como funciona

| Parte | Onde | Como |
|---|---|---|
| Jogo | `src/PokeDeck/game-bridge.js` | Hook no WebSocket do próprio jogo; manda o estado ao app; compra com `shop.buy`/`auto.set` (as mensagens dos botões) |
| Recompra | `src/PokeDeck.Core/Restock.cs` | Lote abaixo do limite, ouro mínimo, prioridade poção > revive > bola, pedidos com intervalo aleatório |
| ULTRA/Economia | `src/PokeDeck/Account.cs` | `cfg-ultra`/`cfg-economia` no localStorage antes do jogo abrir; mudou → recarrega (sem cliques sintéticos) |
| Lives leves | `src/PokeDeck.Core/Twitch.cs`, `Kick.cs` | Sem vídeo: `minute-watched` (Twitch) e socket de espectador (Kick) |
| Baú/resgates | `src/PokeDeck.Core/Points.cs`, `src/PokeDeck/AgentPage.cs` | Scripts rodando dentro do player real (escondido) do site: anti-robô e cookies do próprio site |
| Guarda | `Account.GuardAsync` | Bônus perto de vencer → resgata num canal com pontos parados; confere no jogo |
| Interface | `ui/` (Svelte) | Embutida no exe; as páginas do jogo ficam por cima das vagas que ela desenha |

## Riscos

Protocolos não documentados (socket do jogo, GQL da Twitch, API da Kick) mudam sem aviso. Automação pode violar as
regras do jogo/Twitch/Kick: uso por conta própria.
