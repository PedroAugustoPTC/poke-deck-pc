<script>
  import { onMount, tick } from 'svelte';

  const host = window.chrome?.webview;
  const send = (o) => host?.postMessage(o);

  let config = $state(null);
  let status = $state({ accounts: [], log: [], twitchLive: {}, kickLive: {} });
  let panel = $state(null); // null | 'config' | 'log'
  let draft = $state(null);
  let tab = $state(0);
  let twitchText = $state('');
  let kickText = $state('');
  const bodies = [];

  let fullscreen = $state(false);
  let logTab = $state('redeems');
  let logAccount = $state(-1);
  let logSite = $state('');
  window.addEventListener('keydown', (e) => {
    if (e.key === 'F11') {
      e.preventDefault();
      send({ t: 'fullscreen' });
    }
  });
  let extLink = $state('');
  let extTargets = $state([true, true, true, true]);
  let extBusy = $state(false);
  let extMsg = $state(null);
  function installExt() {
    extBusy = true;
    extMsg = null;
    send({ t: 'extInstall', link: extLink, accounts: extTargets.map((on, i) => (on ? i : -1)).filter((i) => i >= 0) });
  }

  host?.addEventListener('message', (e) => {
    const m = e.data;
    if (m?.t === 'config') config = m.config;
    else if (m?.t === 'status') status = m;
    else if (m?.t === 'fullscreen') fullscreen = m.on;
    else if (m?.t === 'extResult') {
      extBusy = false;
      extMsg = m;
      if (m.ok) extLink = '';
    }
  });

  const acc = (i) => status.accounts.find((a) => a.i === i) ?? {};
  const shown = $derived(
    !config ? [] : config.Layout === 'focus' ? [config.Focus] : config.Accounts.map((a, i) => (a.On ? i : -1)).filter((i) => i >= 0),
  );
  const off = $derived(!config ? [] : config.Accounts.map((a, i) => (a.On ? -1 : i)).filter((i) => i >= 0));

  // As páginas do jogo são janelas nativas por cima destas vagas: o app precisa saber onde cada uma está.
  function report() {
    if (!config) return;
    const slots = panel ? [] : shown.map((i) => {
      const r = bodies[i]?.getBoundingClientRect();
      return r && r.width > 0 ? { i, x: r.left, y: r.top, w: r.width, h: r.height } : null;
    }).filter(Boolean);
    send({ t: 'layout', dpr: window.devicePixelRatio, slots });
  }
  $effect(() => {
    void config?.Layout, config?.Focus, panel, shown.length;
    tick().then(() => requestAnimationFrame(report));
  });

  onMount(() => {
    send({ t: 'ready' });
    const ro = new ResizeObserver(() => requestAnimationFrame(report));
    ro.observe(document.body);
    return () => ro.disconnect();
  });

  function save(next) {
    send({ t: 'config', config: $state.snapshot(next) });
  }
  function setLayout(layout, focus = config.Focus) {
    save({ ...$state.snapshot(config), Layout: layout, Focus: focus });
  }
  function toggleAll(key) {
    const c = $state.snapshot(config);
    const on = !c.Accounts.filter((a) => a.On).every((a) => a[key]);
    c.Accounts.forEach((a) => (a[key] = on));
    save(c);
  }
  function setAccount(i, patch) {
    const c = $state.snapshot(config);
    Object.assign(c.Accounts[i], patch);
    save(c);
  }

  function openConfig() {
    draft = $state.snapshot(config);
    copied = null;
    twitchText = draft.TwitchChannels.join('\n');
    kickText = draft.KickChannels.join('\n');
    panel = 'config';
  }
  let copied = $state(null);
  function copyRestock(from) {
    const rules = $state.snapshot(draft.Accounts[from].Restock);
    draft.Accounts.forEach((a, i) => {
      if (i !== from) a.Restock = structuredClone(rules);
    });
    copied = from;
  }

  function saveConfig() {
    const lines = (t) => t.split(/[\s,]+/).map((x) => x.trim()).filter(Boolean);
    draft.TwitchChannels = lines(twitchText);
    draft.KickChannels = lines(kickText);
    for (const a of draft.Accounts) {
      a.RedeemTwitch ||= null;
      a.RedeemKick ||= null;
    }
    save(draft);
    panel = null;
  }

  // A dica dos botões T/K: as lives assistidas, se o aviso de assistindo passou (✓ hora) e o saldo/custo do bônus.
  function livesTip(site, logged, lives, notes) {
    if (!logged) return `${site}: sem login nesta conta.\nClique para entrar.`;
    const rows = (lives ?? []).map((l) => `• ${l.channel} — ${l.watch === '…' ? 'conectando' : l.watch || 'conectando'}${l.points ? ` · pontos ${l.points}` : ''}`);
    return [`${site}: ${rows.length ? `assistindo ${rows.length}` : 'nenhuma live aberta (ninguém da lista no ar)'}`, ...rows,
      ...notes.filter(Boolean).map((n) => `⚠ ${n}`), '', `Clique para abrir a ${site} desta conta.`].join('\n');
  }

  // A dica do 🗳: o estado do voto no IdleHub e quanto falta para o próximo diamante.
  function voteTip(v) {
    const head = v.voted ? 'Voto registrado no IdleHub.io. Conferindo no jogo quando libera o próximo…'
      : v.ready ? `Voto liberado no IdleHub.io${v.nick ? ` para ${v.nick}` : ''}. Clique para votar: quem vota é você, na página do IdleHub (na primeira vez, entre com o Google ou o Discord; o login fica salvo nesta conta).`
      : `Esta conta já votou. O próximo voto libera em ${left(v.left)}.`;
    if (!(v.perReward > 0)) return head;
    const missing = Math.max(1, v.perReward - v.inPair);
    return `${head}
${v.votes} votos · ${missing === 1 ? 'falta 1 voto' : `faltam ${missing} votos`} para o próximo 💎 (${v.perPair} 💎 a cada ${v.perReward} votos)`;
  }

  const BUFF = { xp: 'XP', captura: 'Captura', shiny: 'Shiny' };
  const buffs = (map) => Object.entries(map ?? {}).filter(([, sec]) => sec > 0);
  const fmt = (n) => (n == null ? '—' : Math.round(n).toLocaleString('pt-BR'));
  const left = (s) => (!s ? '—' : s >= 3600 ? `${Math.floor(s / 3600)}h${String(Math.floor((s % 3600) / 60)).padStart(2, '0')}` : `${Math.max(1, Math.floor(s / 60))}min`);
  const PHASE = { game: ['no jogo', 'ok'], login: ['tela de login', 'warn'], loading: ['carregando…', 'muted'], failed: ['não carregou', 'bad'], elsewhere: ['aberta em outro lugar', 'bad'], off: ['desligada', 'muted'] };
  const KINDS = [['', 'não resgatar'], ['xp', '+15% XP (300)'], ['captura', '+15% Captura (500)'], ['shiny', '+15% Shiny (1000)']];
</script>

<div class="app">
  <header class="bar">
    <strong class="logo">
      <svg width="20" height="20" viewBox="0 0 160 160" aria-hidden="true">
        <rect width="160" height="160" rx="36" fill="#1b1e27" /><rect x="30" y="30" width="46" height="46" rx="10" fill="#f2c94c" />
        <rect x="84" y="30" width="46" height="46" rx="10" fill="#3a4157" /><rect x="30" y="84" width="46" height="46" rx="10" fill="#3a4157" />
        <rect x="84" y="84" width="46" height="46" rx="10" fill="#3a4157" /><circle cx="107" cy="107" r="7" fill="#a970ff" /><circle cx="53" cy="107" r="7" fill="#53fc18" />
      </svg>
      PokeDeck
    </strong>
    {#if config}
      <div class="group">
        <button class:on={config.Layout === 'grid'} onclick={() => setLayout('grid')}>Grade</button>
        <button class:on={config.Layout === 'cols'} onclick={() => setLayout('cols')}>Colunas</button>
      </div>
      <div class="group">
        <button class:on={config.Accounts.filter((a) => a.On).every((a) => a.Ultra)} onclick={() => toggleAll('Ultra')} title="Modo ULTRA Economia do jogo (recarrega as contas)">⚡ ULTRA</button>
        <button class:on={config.Accounts.filter((a) => a.On).every((a) => a.Economy)} onclick={() => toggleAll('Economy')} title="Modo Economia do jogo (recarrega as contas)">🔋 Economia</button>
        <button class:on={config.Accounts.filter((a) => a.On).every((a) => a.Muted)} onclick={() => toggleAll('Muted')}>🔇 Mudo</button>
        <button class:on={status.echo} onclick={() => send({ t: 'echo', on: !status.echo })}
          title="Eco: o que você faz no jogo de uma conta se repete nas outras (a mesma ação, pelo jogo de cada uma). Chat, mensagens e ações num pokémon específico não se repetem.">⇄ Eco{status.echo ? ' ligado' : ''}</button>
        {#if off.length}
          <button onclick={() => save({ ...$state.snapshot(config), Accounts: config.Accounts.map((a) => ({ ...$state.snapshot(a), On: true })) })}>⏻ Ligar todas</button>
        {:else}
          <button onclick={() => save({ ...$state.snapshot(config), Accounts: config.Accounts.map((a) => ({ ...$state.snapshot(a), On: false })) })}>⏻ Desligar todas</button>
        {/if}
      </div>
      <span class="spacer"></span>
      {#each off as i}
        <button onclick={() => setAccount(i, { On: true })} title="Ligar">⏻ {config.Accounts[i].Name}</button>
      {/each}
      <button class:on={fullscreen} onclick={() => send({ t: 'fullscreen' })} title="Tela cheia (F11)">⛶ Tela cheia</button>
      <button onclick={() => send({ t: 'checkLives' })} title="Conferir agora quem está ao vivo">⟳ Lives</button>
      <button class:on={panel === 'log'} onclick={() => (panel = panel === 'log' ? null : 'log')}>Registro</button>
      <button class:on={panel === 'config'} onclick={() => (panel === 'config' ? (panel = null) : openConfig())}>⚙ Config</button>
    {/if}
  </header>

  {#if config}
    <main class="deck {config.Layout}" style:--n={shown.length}>
      {#each shown as i (i)}
        {@const a = acc(i)}
        {@const c = config.Accounts[i]}
        {@const ph = PHASE[a.phase] ?? PHASE.loading}
        <section class="pane" class:active={a.active}>
          <div class="head">
            <span class="dot {ph[1]}" title={ph[0]}></span>
            <strong>{c.Name}</strong>
            <span class="chip gold" title="Ouro">💰 {fmt(a.gold)}</span>
            {#each [['tw', 'T', 'Twitch', a.bonus?.twitch], ['kk', 'K', 'Kick', a.bonus?.kick]] as [cls, tag, site, map]}
              {#if map}
                <span class="chip {cls}" title="Bônus da {site} ativos no jogo (tempo que falta)">{tag} {buffs(map).map(([k, sec]) => `${BUFF[k] ?? k} ${left(sec)}`).join(' · ') || '—'}</span>
              {/if}
            {/each}
            <span class="spacer"></span>
            {#if c.Lives}
              <span class="lives">
                <button class="mini tw" class:dim={!a.lives?.twitchLogged} onclick={() => send({ t: 'login', i, site: 'twitch' })}
                  title={livesTip('Twitch', a.lives?.twitchLogged, a.lives?.twitch, [a.lives?.twitchStopped, a.lives?.pubsub])}>T {a.lives?.twitch?.length ?? 0}</button>
                <button class="mini kk" class:dim={!a.lives?.kickLogged} onclick={() => send({ t: 'login', i, site: 'kick' })}
                  title={livesTip('Kick', a.lives?.kickLogged, a.lives?.kick, [a.lives?.kickStopped])}>K {a.lives?.kick?.length ?? 0}</button>
              </span>
            {/if}
            {#each (a.extensions ?? []).filter((e) => e.Enabled && e.Page) as e (e.Id)}
              <button class="mini" onclick={() => send({ t: 'extPopup', i, id: e.Id })} title={e.Name}>🧩 {e.Name.slice(0, 12)}</button>
            {/each}
            {#if a.vote}
              <button class="mini" class:vote={a.vote.ready} onclick={() => send({ t: 'vote', i })} title={voteTip(a.vote)}>
                🗳 {a.vote.voted ? '✓ Votou' : a.vote.ready ? 'Votar' : left(a.vote.left)}</button>
            {/if}
            <button class="mini" onclick={() => setAccount(i, { Restock: { ...c.Restock, On: !c.Restock.On } })} class:on={c.Restock.On} title={c.Restock.On ? `Recompra ligada${a.restock?.paused ? ' (pausada)' : ''}` : 'Recompra desligada'}>🛒</button>
            <button class="mini" onclick={() => send({ t: 'reload', i })} title="Recarregar">⟳</button>
            <button class="mini" onclick={() => (config.Layout === 'focus' ? setLayout('grid') : setLayout('focus', i))} title="Ampliar / voltar">⤢</button>
            <button class="mini" onclick={() => setAccount(i, { On: false })} title="Desligar esta conta">⏻</button>
          </div>
          <div class="body" bind:this={bodies[i]}>
            {#if panel}<div class="cover">{ph[0]}</div>{/if}
          </div>
        </section>
      {:else}
        <div class="empty">Todas as contas desligadas. Ligue uma na barra de cima.</div>
      {/each}
    </main>
  {/if}

  {#if panel === 'log'}
    <div class="overlay">
      <div class="sheet">
        <nav class="tabs">
          <button class:on={logTab === 'redeems'} onclick={() => (logTab = 'redeems')}>Resgates e lives</button>
          <button class:on={logTab === 'all'} onclick={() => (logTab = 'all')}>Geral</button>
          {#if logTab === 'redeems'}
            <span class="spacer"></span>
            <select bind:value={logAccount}>
              <option value={-1}>Todas as contas</option>
              {#each config.Accounts as a, i}<option value={i}>{a.Name}</option>{/each}
            </select>
            <select bind:value={logSite}><option value="">Twitch e Kick</option><option>Twitch</option><option>Kick</option></select>
          {/if}
        </nav>
        {#if logTab === 'redeems'}
          {@const rows = (status.redeems ?? []).filter((r) => (logAccount < 0 || r.Account === logAccount) && (!logSite || r.Site === logSite))}
          <table class="grid redeems">
            <thead><tr><th>Hora</th><th>Conta</th><th>Site</th><th>Canal</th><th>Evento</th><th>Detalhe</th></tr></thead>
            <tbody>
              {#each rows as r}
                <tr>
                  <td class="muted">{r.At}</td>
                  <td>{config.Accounts[r.Account]?.Name ?? `Conta ${r.Account + 1}`}</td>
                  <td class={r.Site === 'Twitch' ? 'tw' : 'kk'}>{r.Site}</td>
                  <td>{r.Channel}</td>
                  <td><span class="kind k-{r.Kind.replace(' ', '-')}">{r.Kind}</span></td>
                  <td>{r.Text}</td>
                </tr>
              {:else}
                <tr><td colspan="6" class="muted">Nada ainda. Resgates, baús, recusas e a guarda aparecem aqui.</td></tr>
              {/each}
            </tbody>
          </table>
        {:else}
          <pre class="log">{status.log.join('\n')}</pre>
        {/if}
      </div>
    </div>
  {/if}

  {#if panel === 'config' && draft}
    <div class="overlay">
      <div class="sheet">
        <nav class="tabs">
          {#each draft.Accounts as a, i}<button class:on={tab === i} onclick={() => (tab = i)}>{a.Name}</button>{/each}
          <button class:on={tab === 'lives'} onclick={() => (tab = 'lives')}>Lives</button>
          <button class:on={tab === 'timing'} onclick={() => (tab = 'timing')}>Tempos e desempenho</button>
          <button class:on={tab === 'ext'} onclick={() => (tab = 'ext')}>Extensões</button>
          <span class="spacer"></span>
          <button onclick={() => (panel = null)}>Cancelar</button>
          <button class="on" onclick={saveConfig}>Salvar</button>
        </nav>

        {#if typeof tab === 'number'}
          {@const a = draft.Accounts[tab]}
          {@const s = acc(tab)}
          <div class="form">
            <h3>Conta</h3>
            <label>Nome <input bind:value={a.Name} /></label>
            <label><input type="checkbox" bind:checked={a.On} /> Ligada</label>
            <label><input type="checkbox" bind:checked={a.Ultra} /> Modo ULTRA</label>
            <label><input type="checkbox" bind:checked={a.Economy} /> Modo Economia</label>
            <label><input type="checkbox" bind:checked={a.ReduceMotion} /> Menos animações</label>
            <label><input type="checkbox" bind:checked={a.Muted} /> Mudo</label>
            <label>Zoom <input type="number" step="0.1" min="0.5" max="1.5" bind:value={a.Zoom} /></label>

            <h3>Recompra</h3>
            <button onclick={() => copyRestock(tab)} title="Copia estas regras de recompra (inclusive ligada/desligada) para as outras contas; salve para valer">
              {copied === tab ? '✓ Copiado (clique em Salvar)' : 'Aplicar esta recompra a todas as contas'}
            </button>
            <label><input type="checkbox" bind:checked={a.Restock.On} /> Ligada</label>
            <label>Ouro mínimo <input type="number" min="0" bind:value={a.Restock.GoldFloor} /></label>
            <label><input type="checkbox" bind:checked={a.Restock.PotionFirst} /> Poção da recompra em 1º no "Usar Poções"</label>
            <table class="rules">
              <thead><tr><th></th><th>Item</th><th>Quando tiver menos de</th><th>Comprar</th></tr></thead>
              <tbody>
                <tr>
                  <td><input type="checkbox" bind:checked={a.Restock.Balls.On} /> Pokebola</td>
                  <td>
                    {#if s.ballCatalog?.length}
                      <select bind:value={a.Restock.Balls.Id}>{#each s.ballCatalog as b}<option value={b.id}>{b.name} ({fmt(b.price)})</option>{/each}</select>
                    {:else}<input type="number" min="1" bind:value={a.Restock.Balls.Id} title="Id da pokebola (abra o jogo para ver a lista)" />{/if}
                  </td>
                  <td><input type="number" min="0" bind:value={a.Restock.Balls.Below} /></td>
                  <td><input type="number" min="0" bind:value={a.Restock.Balls.Buy} /></td>
                </tr>
                <tr>
                  <td><input type="checkbox" bind:checked={a.Restock.Potions.On} /> Poções</td>
                  <td>
                    <select bind:value={a.Restock.Potions.Id}>
                      <option value={0}>melhor custo-benefício</option>
                      {#each s.potionList ?? [] as p}<option value={Number(p.id)}>{p.name} ({fmt(p.price)})</option>{/each}
                    </select>
                  </td>
                  <td><input type="number" min="0" bind:value={a.Restock.Potions.Below} /></td>
                  <td><input type="number" min="0" bind:value={a.Restock.Potions.Buy} /></td>
                </tr>
                <tr>
                  <td><input type="checkbox" bind:checked={a.Restock.Revives.On} /> Revives</td>
                  <td class="muted">o mais barato</td>
                  <td><input type="number" min="0" bind:value={a.Restock.Revives.Below} /></td>
                  <td><input type="number" min="0" bind:value={a.Restock.Revives.Buy} /></td>
                </tr>
              </tbody>
            </table>

            <p class="muted">Lives, resgates e guarda desta conta ficam na aba <strong>Lives</strong>.</p>
          </div>
        {:else if tab === 'lives'}
          {@const lines = (t) => t.split(/[\s,]+/).filter(Boolean)}
          {@const liveNow = (map) => Object.entries(map ?? {}).filter(([, v]) => v).map(([k]) => k)}
          <p class="muted">Um canal por linha, na ordem de prioridade. O app abre as primeiras que estiverem no ar.</p>
          <div class="cols2">
            <div class="card">
              <div class="card-head"><span class="pill tw">Twitch</span> canais, em ordem de prioridade <span class="spacer"></span><span class="muted">{lines(twitchText).length} canais</span></div>
              <textarea rows="12" bind:value={twitchText}></textarea>
              <div class="row between">
                <span>Lives abertas por conta</span>
                <span class="stepper"><button onclick={() => (draft.TwitchLives = Math.max(0, draft.TwitchLives - 1))}>−</button><b>{draft.TwitchLives}</b><button onclick={() => (draft.TwitchLives = Math.min(5, draft.TwitchLives + 1))}>+</button></span>
              </div>
              <div class="row between">
                <label><input type="checkbox" bind:checked={draft.ClaimChest} /> Pegar o baú de bônus sozinho</label>
                <span class="muted">+50 a cada ~15 min</span>
              </div>
              <span class="muted small">No ar agora: {liveNow(status.twitchLive).join(', ') || 'nenhum'}</span>
            </div>
            <div class="card">
              <div class="card-head"><span class="pill kk">Kick</span> canais, em ordem de prioridade <span class="spacer"></span><span class="muted">{lines(kickText).length} canais</span></div>
              <textarea rows="12" bind:value={kickText}></textarea>
              <div class="row between">
                <span>Lives abertas por conta <span class="muted">(a Kick só conta 2)</span></span>
                <span class="stepper"><button onclick={() => (draft.KickLives = Math.max(0, draft.KickLives - 1))}>−</button><b>{draft.KickLives}</b><button onclick={() => (draft.KickLives = Math.min(5, draft.KickLives + 1))}>+</button></span>
              </div>
              <span class="muted small">No ar agora: {liveNow(status.kickLive).join(', ') || 'nenhum'}</span>
            </div>
          </div>
          <div class="card">
            <div class="card-head"><strong>Por conta</strong><span class="spacer"></span><span class="muted">vincule a Kick e a Twitch no jogo em cada conta que resgata</span></div>
            <table class="grid">
              <thead>
                <tr>
                  <th>Conta</th>
                  <th title="A conta assiste às lives com a Twitch/Kick logada nela">Assiste às lives</th>
                  <th title="O bônus do PokeIdle resgatado com os pontos dos canais da Kick">Resgatar na Kick</th>
                  <th title="O bônus do PokeIdle resgatado com os pontos dos canais da Twitch">Resgatar na Twitch</th>
                  <th title="Perto de vencer, resgata num canal com pontos parados">Guarda</th>
                </tr>
              </thead>
              <tbody>
                {#each draft.Accounts as a}
                  <tr>
                    <td>{a.Name}</td>
                    <td><input type="checkbox" class="switch" bind:checked={a.Lives} /></td>
                    <td><select bind:value={a.RedeemKick}>{#each KINDS as [v, t]}<option value={v || null}>{t}</option>{/each}</select></td>
                    <td><select bind:value={a.RedeemTwitch}>{#each KINDS as [v, t]}<option value={v || null}>{t}</option>{/each}</select></td>
                    <td><input type="checkbox" class="switch" bind:checked={a.Guard} /></td>
                  </tr>
                {/each}
              </tbody>
            </table>
          </div>
          <p class="muted small">As lives abertas são reconferidas ao salvar.</p>
        {:else if tab === 'ext'}
          <div class="form">
            <p class="muted">Cada conta tem as próprias extensões. Cole o link da Chrome Web Store, marque as contas e instale. A conta recarrega para a extensão entrar.</p>
            <label class="wide">Link <input class="grow" bind:value={extLink} placeholder="https://chromewebstore.google.com/detail/..." /></label>
            <div class="row">
              <label><input type="checkbox" checked={extTargets.every(Boolean)} onchange={(e) => (extTargets = extTargets.map(() => e.currentTarget.checked))} /> <strong>Todas</strong></label>
              {#each draft.Accounts as acct, i}<label><input type="checkbox" bind:checked={extTargets[i]} /> {acct.Name}</label>{/each}
              <button class="on" disabled={extBusy} onclick={installExt}>{extBusy ? 'Instalando…' : 'Instalar'}</button>
            </div>
            {#if extMsg}<p class:bad={!extMsg.ok}>{extMsg.msg}</p>{/if}
            {#each draft.Accounts as acct, i}
              <h3>{acct.Name}</h3>
              {#each acc(i).extensions ?? [] as e (e.Id)}
                <div class="row">
                  <label><input type="checkbox" checked={e.Enabled} onchange={(ev) => send({ t: 'extToggle', i, id: e.Id, on: ev.currentTarget.checked })} /> {e.Name}</label>
                  {#if e.Page}<button class="mini" onclick={() => send({ t: 'extPopup', i, id: e.Id })}>abrir</button>{/if}
                  <button class="mini danger" onclick={() => confirm(`Remover ${e.Name} de ${acct.Name}?`) && send({ t: 'extRemove', i, id: e.Id })}>remover</button>
                </div>
              {:else}<span class="muted">nenhuma extensão</span>{/each}
            {/each}
          </div>
        {:else}
          <div class="form">
            <p class="muted">Todo intervalo varia ± a variação, para nada sair em ritmo exato.</p>
            <label>Variação (0–0,5) <input type="number" step="0.05" min="0" max="0.5" bind:value={draft.Timing.JitterPct} /></label>
            <label>Conferir quem está ao vivo (s) <input type="number" min="60" bind:value={draft.Timing.LiveCheckSec} /></label>
            <label>Pontos/baú da Twitch (s) <input type="number" min="60" bind:value={draft.Timing.TwitchPointsSec} /></label>
            <label>Pontos da Kick (s) <input type="number" min="60" bind:value={draft.Timing.KickPointsSec} /></label>
            <label>Guarda: conferir a cada (s) <input type="number" min="60" bind:value={draft.Timing.GuardSec} /></label>
            <label>Guarda: agir quando faltar menos de (min) <input type="number" min="5" bind:value={draft.Timing.GuardThresholdMin} /></label>
            <label>Compras: intervalo entre pedidos (s) <input type="number" min="2" bind:value={draft.Timing.BuyGapMinSec} /> a <input type="number" min="2" bind:value={draft.Timing.BuyGapMaxSec} /></label>
            <label>Resgate recusado: tentar de novo em (min) <input type="number" min="5" bind:value={draft.Timing.RedeemRetryMin} /></label>
            <h3>Desempenho</h3>
            <p class="muted">A conta em uso (a última que você clicou, com borda amarela) desenha fluida; as outras desenham devagar. A caça não muda.</p>
            <label>Quadros por segundo da conta em uso (60 = sem limite) <input type="number" min="10" max="60" bind:value={draft.Timing.ActiveFps} /></label>
            <label>Quadros por segundo das outras contas <input type="number" min="1" max="60" bind:value={draft.Timing.BackgroundFps} /></label>
            <label>Animação dos sprites das outras contas (ms; 220 = normal) <input type="number" min="220" max="3000" step="110" bind:value={draft.Timing.BackgroundSpriteMs} /></label>
          </div>
        {/if}
      </div>
    </div>
  {/if}
</div>

<style>
  .app { display: flex; flex-direction: column; height: 100%; }
  .bar { display: flex; align-items: center; gap: 10px; padding: 6px 10px; background: var(--panel); border-bottom: 1px solid var(--line); }
  .logo { color: var(--accent); letter-spacing: 0.5px; display: inline-flex; align-items: center; gap: 7px; }
  .group { display: flex; gap: 4px; }
  .spacer { flex: 1; }
  .muted { color: var(--muted); }

  .deck { flex: 1; display: grid; gap: 6px; padding: 6px; min-height: 0; }
  .deck.grid { grid-template-columns: repeat(min(var(--n), 2), 1fr); grid-auto-rows: 1fr; }
  .deck.cols { grid-template-columns: repeat(var(--n), 1fr); }
  .deck.focus { grid-template-columns: 1fr; }
  .pane { display: flex; flex-direction: column; min-height: 0; background: var(--panel); border: 1px solid var(--line); border-radius: 8px; overflow: hidden; }
  .pane.active { border-color: var(--accent); }
  .head { display: flex; align-items: center; gap: 6px; padding: 4px 6px; flex-wrap: wrap; border-bottom: 1px solid var(--line); }
  .body { flex: 1; position: relative; min-height: 0; background: var(--bg); }
  .cover { position: absolute; inset: 0; display: grid; place-items: center; color: var(--muted); }
  .empty { display: grid; place-items: center; color: var(--muted); }

  .dot { width: 9px; height: 9px; border-radius: 50%; background: var(--muted); }
  .dot.ok { background: var(--ok); }
  .dot.warn { background: var(--warn); }
  .dot.bad { background: var(--bad); }
  .chip { padding: 1px 7px; border-radius: 10px; background: var(--panel-2); color: var(--muted); white-space: nowrap; }
  .chip.gold { color: var(--accent); }
  .chip.active { color: var(--text); }
  .chip.tw, .mini.tw { color: var(--twitch); }
  .chip.kk, .mini.kk { color: var(--kick); }
  .mini { padding: 1px 7px; }
  .mini.dim { opacity: 0.45; }
  .mini.vote { color: var(--accent); font-weight: 600; }
  .lives { display: inline-flex; gap: 4px; }

  .overlay { position: fixed; inset: 44px 0 0 0; background: rgba(10, 11, 15, 0.92); display: grid; place-items: center; }
  .sheet { width: min(980px, 96vw); max-height: 92%; overflow: auto; background: var(--panel); border: 1px solid var(--line); border-radius: 10px; padding: 14px 18px; user-select: text; }
  .tabs { display: flex; gap: 6px; flex-wrap: wrap; position: sticky; top: -14px; background: var(--panel); padding: 6px 0 10px; }
  .form { display: flex; flex-direction: column; gap: 9px; align-items: flex-start; }
  h2, h3 { margin: 8px 0 2px; font-weight: 600; }
  h3 { color: var(--accent); font-size: 13px; }
  .rules td, .rules th { padding: 3px 8px 3px 0; text-align: left; font-weight: normal; color: var(--muted); }
  .rules td { color: var(--text); }
  .cols2 { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; width: 100%; }
  .card { background: var(--bg); border: 1px solid var(--line); border-radius: 10px; padding: 12px 14px; display: flex; flex-direction: column; gap: 10px; margin-top: 10px; }
  .card-head { display: flex; align-items: center; gap: 8px; }
  .pill { padding: 2px 10px; border-radius: 10px; background: var(--panel-2); }
  .pill.tw { color: var(--twitch); }
  .pill.kk { color: var(--kick); }
  .between { justify-content: space-between; }
  .small { font-size: 12px; }
  .stepper { display: inline-flex; align-items: center; border: 1px solid var(--line); border-radius: 6px; }
  .stepper button { border: none; border-radius: 0; background: transparent; padding: 3px 12px; }
  .stepper b { min-width: 36px; text-align: center; font-weight: 600; }
  .grid { width: 100%; border-collapse: collapse; }
  .grid th { text-align: left; font-weight: normal; color: var(--muted); padding: 6px 8px; border-bottom: 1px solid var(--line); }
  .grid td { padding: 6px 8px; border-bottom: 1px solid var(--line); }
  .grid select { width: 100%; }
  .switch { appearance: none; width: 34px; height: 18px; border-radius: 9px; background: var(--line); position: relative; cursor: pointer; border: none; padding: 0; }
  .switch::after { content: ''; position: absolute; top: 2px; left: 2px; width: 14px; height: 14px; border-radius: 50%; background: var(--muted); transition: left 0.15s; }
  .switch:checked { background: #6c4bd8; }
  .switch:checked::after { left: 18px; background: #fff; }
  .redeems td.tw { color: var(--twitch); }
  .redeems td.kk { color: var(--kick); }
  .kind { padding: 1px 8px; border-radius: 9px; background: var(--panel-2); white-space: nowrap; }
  .k-resgate, .k-baú { color: var(--ok); }
  .k-recusado, .k-baú-recusado, .k-devolvido { color: var(--bad); }
  .k-gasto-fora { color: var(--warn); }
  .k-guarda { color: var(--accent); }
  .row { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
  .wide { width: 100%; }
  .grow { flex: 1; }
  .bad { color: var(--bad); }
  .log { font: 12px/1.45 Consolas, monospace; white-space: pre-wrap; margin: 0; }
</style>
