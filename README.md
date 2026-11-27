# PokeDeck

Até 4 contas do [pokeidle.io](https://pokeidle.io/app) numa janela, com recompra, ULTRA/Economia, lives leves da
Twitch/Kick, baú da Twitch, resgate automático do bônus, guarda do bônus e extensões por conta. Protocolos espelhados do
[PokeIdleMulti](https://github.com/artbruno35/PokeIdleMulti); tempos e regras nossos, todos com variação aleatória.

> Projeto de fã, não oficial, sem ligação com o pokeidle.io. Use por sua conta e respeite as regras do jogo.

## Instalar

1. Abra a [**última release**](https://github.com/PedroAugustoPTC/poke-deck-pc/releases/latest) e baixe o
   `PokeDeck.exe` (em *Assets*).
2. Ponha numa pasta qualquer e abra. Não precisa instalar nada: o exe já leva tudo. Precisa do WebView2 Runtime, que
   já vem no Windows 10/11 (se faltar, o app avisa).
3. O exe não é assinado: se aparecer "O Windows protegeu o computador", clique em **Mais informações → Executar assim
   mesmo**. Alguns antivírus também estranham exe novo sem assinatura.
4. Na barra de cima, **⏻ Ligar todas** (ou ligue uma conta) e entre no jogo em cada uma.

**Atualizar:** feche o app, baixe o exe da release nova e troque pelo antigo. Logins e configurações ficam em
`%LOCALAPPDATA%\PokeDeck` (não no exe) e continuam. **Não compartilhe essa pasta:** tem os seus logins.

## Compilar (só Docker, nada instalado no PC)

```powershell
.\build.ps1
```

Sai sempre em `dist\PokeDeck.exe` (pasta fora do git; feche o app antes de compilar). Um arquivo, sem instalar .NET. Precisa do WebView2 Runtime (já vem no Windows 10/11).
O build roda os testes do núcleo antes de gerar o exe.

## Publicar uma versão

Releases → *Draft a new release* → tag nova (`v0.2.0`) → *Publish* (ou `gh release create v0.2.0 --target main`).
Isso dispara `.github/workflows/release.yml`, que compila com o mesmo Dockerfile e anexa o `PokeDeck.exe` à release
(uns 5 min). A versão da tag aparece no título da janela. Histórico: `v0.1.0` (26/11/2026), primeira versão.

## Usar

1. Abra o exe. Entre no jogo em cada conta (o login fica no perfil da conta).
2. **⚙ Config** → aba da conta: nome, ULTRA/Economia, recompra (confira os valores: gasta ouro de verdade), lives,
   bônus a resgatar e guarda. Aba **Lives**: canais e quantas lives; aba **Tempos**: todos os intervalos.
3. Na conta com lives: botões **T**/**K** no cabeçalho abrem a janela para entrar na Twitch/Kick daquela conta;
   **🎁T**/**🎁K** ligam e desligam o resgate do bônus de cada site.
4. **Horário de bloqueio** (Config › Lives): antes de resgatar, cada conta confere se o buff dela já passa desse horário
   (já passou hoje → vale o de amanhã); se passa, desliga o 🎁 daquele site e registra. Religar à mão pede confirmação.
5. **🗳** no cabeçalho: o voto no IdleHub.io liberado (ou o tempo até liberar). Clique e vote na página que abre.
6. **⇄ Eco** na barra: o que você faz no jogo de uma conta se repete nas outras (chat, mensagens e ações num pokémon
   específico não se repetem).

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
| Bloqueio do resgate | `src/PokeDeck.Core/RedeemBlock.cs` | Hora atual + buff que falta ≥ próximo horário de bloqueio → não resgata e desliga o 🎁 do site |
| Voto | `src/PokeDeck.Core/IdleHub.cs` | `POST /idlehub/me` na página do jogo (o pedido da urna); o voto é feito por você na página do IdleHub |
| Eco | `src/PokeDeck.Core/Echo.cs` | Repete no socket das outras contas as mensagens que o jogo mandou logo após um clique seu (sem cliques sintéticos) |
| Interface | `ui/` (Svelte) | Embutida no exe; as páginas do jogo ficam por cima das vagas que ela desenha |

## Riscos

Protocolos não documentados (socket do jogo, GQL da Twitch, API da Kick) mudam sem aviso. Automação pode violar as
regras do jogo/Twitch/Kick: uso por conta própria.
