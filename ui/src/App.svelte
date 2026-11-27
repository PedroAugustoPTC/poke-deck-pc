<script>
  import { onMount, tick } from 'svelte';

  const host = window.chrome?.webview;
  const send = (o) => host?.postMessage(o);

  let config = $state(null);
  let status = $state({ accounts: [], log: [], twitchLive: {}, kickLive: {} });
  let panel = $state(null); // null | 'config' | 'log' | 'ask'
  let ask = $state(null); // a pergunta do painel 'ask': { text, yes, run, alt?: { label, run } }
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
    if (e.key === 'Escape' && panel === 'ask') panel = null;
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
  // Observa cada vaga, não só a janela: o cabeçalho quebra linha quando os dados chegam e a vaga encolhe sem a janela mudar.
  const ro = new ResizeObserver(() => requestAnimationFrame(report));
  $effect(() => {
    void config?.Layout, config?.Focus, panel, shown.length;
    tick().then(() => {
      for (const i of shown) if (bodies[i]) ro.observe(bodies[i]);
      requestAnimationFrame(report);
    });
  });

  onMount(() => {
    send({ t: 'ready' });
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
    draft.RedeemBlockAt = hhmm(draft.RedeemBlockAt);
    draft.Accounts.forEach((a, i) => {
      a.RedeemTwitch ||= null;
      a.RedeemKick ||= null;
      // Ligado/desligado é do 🎁 do cabeçalho (e do bloqueio): o painel aberto não desfaz o que mudou nesse meio-tempo.
      a.RedeemTwitchOn = config.Accounts[i].RedeemTwitchOn;
      a.RedeemKickOn = config.Accounts[i].RedeemKickOn;
    });
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
    return `${head}\n${v.votes} votos · ${missing === 1 ? 'falta 1 voto' : `faltam ${missing} votos`} para o próximo 💎 (${v.perPair} 💎 a cada ${v.perReward} votos)`;
  }

  // Versão nova no GitHub: pergunta antes, porque o app fecha e abre de novo (as contas recarregam).
  function askUpdate() {
    const u = status.update;
    ask = {
      yes: u.error ? 'Tentar de novo' : 'Atualizar agora',
      run: () => send({ t: 'updateNow' }),
      alt: { label: 'Abrir a página da release', run: () => send({ t: 'updatePage' }) },
      text: `Nova versão do PokeDeck: ${u.version}\n\n${u.notes.trim()}\n\nO PokeDeck baixa a versão nova, fecha e abre de novo sozinho. As contas recarregam; logins e configurações ficam.` +
        (u.error ? `\n\n⚠ A última tentativa falhou: ${u.error}` : ''),
    };
    panel = 'ask';
  }

  // O 🎁 de cada site: liga/desliga o resgate. Religar com o buff já passando do horário de bloqueio pede confirmação.
  function toggleRedeem(i, site) {
    const c = config.Accounts[i];
    const key = site === 'twitch' ? 'RedeemTwitchOn' : 'RedeemKickOn';
    const kind = site === 'twitch' ? c.RedeemTwitch : c.RedeemKick;
    const name = site === 'twitch' ? 'Twitch' : 'Kick';
    if (!c[key] && acc(i).blockCovered?.[site]) {
      const sec = acc(i).bonus?.[site]?.[kind] ?? 0;
      const end = new Date(Date.now() + sec * 1000);
      const until = end.toLocaleString('pt-BR', { hour: '2-digit', minute: '2-digit', ...(end.toDateString() === new Date().toDateString() ? {} : { day: '2-digit', month: '2-digit' }) });
      // Não é confirm(): as páginas do jogo são janelas nativas por cima e cobririam a caixa. Um painel esconde o jogo.
      ask = { yes: 'Ligar mesmo assim', run: () => setAccount(i, { [key]: true }), text: `${c.Name}: o bônus ${BUFF[kind] ?? kind} da ${name} ainda dura ${left(sec)} (até ${until}), que já passa do horário de bloqueio (${config.RedeemBlockAt}).\n\nLigar o resgate mesmo assim? Ele resgata normalmente e o bloqueio volta a valer depois das ${config.RedeemBlockAt}.` };
      panel = 'ask';
      return;
    }
    setAccount(i, { [key]: !c[key] });
  }
  function redeemTip(c, site) {
    const kind = site === 'twitch' ? c.RedeemTwitch : c.RedeemKick;
    const on = site === 'twitch' ? c.RedeemTwitchOn : c.RedeemKickOn;
    const name = site === 'twitch' ? 'Twitch' : 'Kick';
    if (!kind) return `Resgate da ${name}: nenhum prêmio escolhido (Config › Lives).`;
    return `Resgate da ${name} (${BUFF[kind] ?? kind}): ${on ? 'ligado' : 'desligado'}.` +
      (config.RedeemBlockAt ? `\nHorário de bloqueio: ${config.RedeemBlockAt} (não resgata se o buff já passa dele).` : '') + `\nClique para ` + (on ? 'desligar.' : 'ligar.');
  }

  // O horário digitado ("22", "22:30", "2230", "22h30", "9:05") como "HH:mm"; vazio ou inválido: null.
  function hhmm(text) {
    const m = /^(\d{1,2})(?:[:h]?(\d{2}))?h?$/i.exec(String(text ?? '').trim());
    if (!m || +m[1] > 23 || +(m[2] ?? 0) > 59) return null;
    return `${m[1].padStart(2, '0')}:${m[2] ?? '00'}`;
  }

  const BUFF = { xp: 'XP', captura: 'Captura', shiny: 'Shiny' };
  const buffs = (map) => Object.entries(map ?? {}).filter(([, sec]) => sec > 0);
  const fmt = (n) => (n == null ? '—' : Math.round(n).toLocaleString('pt-BR'));
  const left = (s) => (!s ? '—' : s >= 3600 ? `${Math.floor(s / 3600)}h${String(Math.floor((s % 3600) / 60)).padStart(2, '0')}` : `${Math.max(1, Math.floor(s / 60))}min`);
  const PHASE = { game: ['no jogo', 'ok'], login: ['tela de login', 'warn'], loading: ['carregando…', 'muted'], failed: ['não carregou', 'bad'], elsewhere: ['aberta em outro lugar', 'bad'], off: ['desligada', 'muted'] };
  // Ícones de traço (grade 16×16, cor do texto) no lugar dos emoji.
  const ICONS = {
    grid: '<rect x="2" y="2" width="5" height="5"/><rect x="9" y="2" width="5" height="5"/><rect x="2" y="9" width="5" height="5"/><rect x="9" y="9" width="5" height="5"/>',
    cols: '<rect x="2" y="2" width="3" height="12"/><rect x="6.5" y="2" width="3" height="12"/><rect x="11" y="2" width="3" height="12"/>',
    bolt: '<path d="M9 1.5 3.5 9H8l-1 5.5L12.5 7H8z"/>',
    battery: '<rect x="1.5" y="5" width="11.5" height="6" rx="1"/><path d="M14.5 7v2M4.5 7.5v1M7 7.5v1"/>',
    mute: '<path d="M2.5 6h2.5L8.5 3v10L5 10H2.5z"/><path d="m11 6 4 4M15 6l-4 4"/>',
    power: '<path d="M8 1.8v5.4"/><path d="M4.6 4.2a5 5 0 1 0 6.8 0"/>',
    full: '<path d="M2 6V2h4M10 2h4v4M14 10v4h-4M6 14H2v-4"/>',
    refresh: '<path d="M13.5 8a5.5 5.5 0 1 1-1.6-3.9"/><path d="M13.5 2v3h-3"/>',
    list: '<path d="M5.5 4h8.5M5.5 8h8.5M5.5 12h8.5M2 4h.5M2 8h.5M2 12h.5"/>',
    gear: '<circle cx="8" cy="8" r="2.2"/><path d="M8 1.5v2M8 12.5v2M1.5 8h2M12.5 8h2M3.4 3.4l1.4 1.4M11.2 11.2l1.4 1.4M3.4 12.6l1.4-1.4M11.2 4.8l1.4-1.4"/>',
    gift: '<rect x="2" y="6" width="12" height="3"/><path d="M3 9v5h10V9M8 6v8"/><path d="M8 6C6.4 6 4.6 5.4 5.1 3.8S7.6 4.1 8 6c.4-1.9 2.4-3.8 2.9-2.2S9.6 6 8 6z"/>',
    cart: '<path d="M1.5 2.5h2l1.5 8h7.5l1.5-6H4.2"/><circle cx="6" cy="13.2" r="1"/><circle cx="11.5" cy="13.2" r="1"/>',
    expand: '<path d="M9.5 2.5h4v4M13.5 2.5 9 7M6.5 13.5h-4v-4M2.5 13.5 7 9"/>',
    puzzle: '<path d="M3 5h3a1.5 1.5 0 1 1 3 0h3v3a1.5 1.5 0 1 1 0 3v3H3z"/>',
    vote: '<path d="M2.5 9h11v5h-11z"/><path d="M5 9V2.5h6V9M6.5 5.5l1 1 2-2"/>',
    check: '<path d="m3 8.5 3 3 7-7"/>',
    up: '<path d="M8 13.5v-11M3.5 7 8 2.5 12.5 7"/>',
    down: '<path d="M8 2.5v11M3.5 9 8 13.5 12.5 9"/>',
  };
  // O ícone do app (o mesmo desenho de src/PokeDeck/icon.svg).
  const LOGO = '<rect x="6" y="1" width="9" height="12" fill="#480e1e"/><rect x="7" y="2" width="7" height="10" fill="#722c3f"/><rect x="7" y="2" width="7" height="1" fill="#914850"/>' +
    '<rect x="4" y="2" width="9" height="12" fill="#480e1e"/><rect x="5" y="3" width="7" height="10" fill="#914850"/><rect x="5" y="3" width="7" height="1" fill="#bd6951"/>' +
    '<rect x="2" y="3" width="9" height="12" fill="#480e1e"/><rect x="3" y="4" width="7" height="10" fill="#bd6951"/><rect x="3" y="4" width="7" height="1" fill="#f1d3b7"/>' +
    '<rect x="3" y="5" width="1" height="8" fill="#df824c"/><rect x="3" y="13" width="7" height="1" fill="#914850"/><rect x="6" y="6" width="1" height="5" fill="#e2a93f"/>' +
    '<rect x="4" y="8" width="5" height="1" fill="#e2a93f"/><rect x="6" y="7" width="1" height="3" fill="#ffe07a"/><rect x="5" y="8" width="3" height="1" fill="#ffe07a"/><rect x="6" y="8" width="1" height="1" fill="#fff7d0"/>';
  const KINDS = [['', 'não resgatar'], ['xp', '+15% XP (300)'], ['captura', '+15% Captura (500)'], ['shiny', '+15% Shiny (1000)']];
</script>

<div class="app">
  {#snippet icon(name, size = 14)}
    <svg width={size} height={size} viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">{@html ICONS[name]}</svg>
  {/snippet}

  <header class="bar">
    <strong class="logo">
      <svg width="24" height="24" viewBox="0 0 16 16" shape-rendering="crispEdges" aria-hidden="true">{@html LOGO}</svg>
      PokeDeck
    </strong>
    {#if config}
      <div class="seg" role="group" aria-label="Arrumação">
        <button class:on={config.Layout === 'grid'} onclick={() => setLayout('grid')}>{@render icon('grid', 13)}Grade</button>
        <button class:on={config.Layout === 'cols'} onclick={() => setLayout('cols')}>{@render icon('cols', 13)}Colunas</button>
      </div>
      <span class="sep"></span>
      <div class="group">
        <button class:on={config.Accounts.filter((a) => a.On).every((a) => a.Ultra)} onclick={() => toggleAll('Ultra')} title="Modo ULTRA Economia do jogo (recarrega as contas)">{@render icon('bolt')}ULTRA</button>
        <button class:on={config.Accounts.filter((a) => a.On).every((a) => a.Economy)} onclick={() => toggleAll('Economy')} title="Modo Economia do jogo (recarrega as contas)">{@render icon('battery')}Economia</button>
        <button class:on={config.Accounts.filter((a) => a.On).every((a) => a.Muted)} onclick={() => toggleAll('Muted')}>{@render icon('mute')}Mudo</button>
      </div>
      <span class="sep"></span>
      {#if off.length}
        <button onclick={() => save({ ...$state.snapshot(config), Accounts: config.Accounts.map((a) => ({ ...$state.snapshot(a), On: true })) })}>{@render icon('power')}Ligar todas</button>
      {:else}
        <button onclick={() => save({ ...$state.snapshot(config), Accounts: config.Accounts.map((a) => ({ ...$state.snapshot(a), On: false })) })}>{@render icon('power')}Desligar todas</button>
      {/if}
      <span class="spacer"></span>
      {#if off.length}
        <div class="seg off" role="group" aria-label="Contas desligadas">
          {#each off as i}
            <button onclick={() => setAccount(i, { On: true })} title="Ligar {config.Accounts[i].Name}"><span class="dot"></span>{config.Accounts[i].Name}</button>
          {/each}
        </div>
      {/if}
      {#if status.update}
        <button class="on" class:bad={status.update.error} disabled={status.update.busy} onclick={askUpdate}
          title={status.update.error ? `A atualização falhou: ${status.update.error}` : 'Há uma versão nova do PokeDeck'}>
          {#if status.update.busy}{@render icon('down')}Baixando {status.update.pct}%{:else}{@render icon('up')}Atualizar para {status.update.version}{/if}</button>
      {/if}
      <button class:on={fullscreen} onclick={() => send({ t: 'fullscreen' })} title="Tela cheia (F11)">{@render icon('full')}Tela cheia</button>
      <button onclick={() => send({ t: 'checkLives' })} title="Conferir agora quem está ao vivo">{@render icon('refresh')}Lives</button>
      <button class:on={panel === 'log'} onclick={() => (panel = panel === 'log' ? null : 'log')}>{@render icon('list')}Registro</button>
      <button class:on={panel === 'config'} onclick={() => (panel === 'config' ? (panel = null) : openConfig())}>{@render icon('gear')}Config</button>
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
                {#each [['twitch', 'tw', 'T', c.RedeemTwitch, c.RedeemTwitchOn], ['kick', 'kk', 'K', c.RedeemKick, c.RedeemKickOn]] as [site, cls, tag, kind, on]}
                  <button class="mini {cls}" class:on={kind && on} class:dim={!kind || !on} class:off={kind && !on} disabled={!kind} onclick={() => toggleRedeem(i, site)} title={redeemTip(c, site)}>{@render icon('gift', 13)}{tag}</button>
                {/each}
              </span>
            {/if}
            {#each (a.extensions ?? []).filter((e) => e.Enabled && e.Page) as e (e.Id)}
              <button class="mini" onclick={() => send({ t: 'extPopup', i, id: e.Id })} title={e.Name}>{@render icon('puzzle', 13)}{e.Name.slice(0, 12)}</button>
            {/each}
            {#if a.vote}
              <button class="mini" class:vote={a.vote.ready} onclick={() => send({ t: 'vote', i })} title={voteTip(a.vote)}>
                {@render icon(a.vote.voted ? 'check' : 'vote', 13)}{a.vote.voted ? 'Votou' : a.vote.ready ? 'Votar' : left(a.vote.left)}</button>
            {/if}
            <button class="mini" onclick={() => setAccount(i, { Restock: { ...c.Restock, On: !c.Restock.On } })} class:on={c.Restock.On} title={c.Restock.On ? `Recompra ligada${a.restock?.paused ? ' (pausada)' : ''}` : 'Recompra desligada'}>{@render icon('cart')}</button>
            <button class="mini" onclick={() => send({ t: 'reload', i })} title="Recarregar">{@render icon('refresh', 13)}</button>
            <button class="mini" onclick={() => (config.Layout === 'focus' ? setLayout('grid') : setLayout('focus', i))} title="Ampliar / voltar">{@render icon('expand', 13)}</button>
            <button class="mini stop" onclick={() => setAccount(i, { On: false })} title="Desligar esta conta">{@render icon('power', 13)}</button>
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

  {#if panel === 'ask' && ask}
    <div class="overlay">
      <div class="sheet ask">
        <p>{ask.text}</p>
        <div class="row">
          {#if ask.alt}<button onclick={() => { ask.alt.run(); panel = null; }}>{ask.alt.label}</button>{/if}
          <span class="spacer"></span>
          <button onclick={() => (panel = null)}>Cancelar</button>
          <button class="on" onclick={() => { ask.run(); panel = null; }}>{ask.yes}</button>
        </div>
      </div>
    </div>
  {/if}

  {#if panel === 'log'}
    <div class="overlay">
      <div class="sheet">
        <nav class="tabs">
          <button class="tab" class:on={logTab === 'redeems'} onclick={() => (logTab = 'redeems')}>Resgates e lives</button>
          <button class="tab" class:on={logTab === 'all'} onclick={() => (logTab = 'all')}>Geral</button>
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
          {#each draft.Accounts as a, i}<button class="tab" class:on={tab === i} onclick={() => (tab = i)}>{a.Name}</button>{/each}
          <button class="tab" class:on={tab === 'lives'} onclick={() => (tab = 'lives')}>Lives</button>
          <button class="tab" class:on={tab === 'timing'} onclick={() => (tab = 'timing')}>Tempos e desempenho</button>
          <button class="tab" class:on={tab === 'ext'} onclick={() => (tab = 'ext')}>Extensões</button>
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
            <div class="row">
              <label>Horário de bloqueio do resgate (todas as contas)
                <input class="hhmm" bind:value={draft.RedeemBlockAt} placeholder="ex.: 22:00" maxlength="5"
                  class:bad={draft.RedeemBlockAt && !hhmm(draft.RedeemBlockAt)} title="Horário no formato HH:mm (ex.: 22:00). Vazio: sem bloqueio." /></label>
              <span class="muted small">Antes de resgatar, cada conta confere se o buff dela já passa deste horário (se já passou hoje, vale o de amanhã). Se passa, desliga o resgate daquele site nela e registra. Vazio: sem bloqueio.</span>
            </div>
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
  /* A barra é uma placa de madeira, como as molduras do jogo: fio de luz em cima, veio escuro embaixo. */
  .bar { display: flex; align-items: center; gap: 6px; padding: 6px 10px; background: var(--bark); border-top: 1px solid var(--wood); border-bottom: 2px solid var(--grain); font-family: var(--tight); font-weight: 500; white-space: nowrap; }
  .bar button { height: 28px; }
  .bar button:not(.on) { background: var(--heart); border-color: var(--grain); box-shadow: inset 0 1px 0 rgba(241, 211, 183, 0.22); }
  .bar button:not(.on):hover { border-color: var(--wood-light); }
  .bar button.bad { background: var(--bad); border-color: var(--grain); color: var(--text); box-shadow: none; }
  .logo { display: inline-flex; align-items: center; gap: 8px; padding-right: 12px; margin-right: 2px; border-right: 1px solid var(--heart); font: 600 19px var(--pixel); color: var(--gold-light); }
  .logo svg { image-rendering: pixelated; }
  .group { display: flex; gap: 4px; }
  .sep { width: 1px; height: 20px; margin: 0 4px; background: var(--heart); }
  .seg { display: inline-flex; gap: 2px; padding: 2px; background: var(--grain); border-radius: 5px; }
  .seg.off { background: var(--heart); margin-right: 4px; }
  .bar .seg button { height: 26px; border-radius: 3px; padding: 0 9px; }
  .bar .seg button:not(.on) { background: transparent; border-color: transparent; box-shadow: none; color: var(--wood-light); }
  .bar .seg button:not(.on):hover { color: var(--text); }
  .seg .dot { width: 7px; height: 7px; }
  .spacer { flex: 1; }
  .muted { color: var(--muted); }

  .deck { flex: 1; display: grid; gap: 6px; padding: 6px; min-height: 0; }
  .deck.grid { grid-template-columns: repeat(min(var(--n), 2), 1fr); grid-auto-rows: 1fr; }
  .deck.cols { grid-template-columns: repeat(var(--n), 1fr); }
  .deck.focus { grid-template-columns: 1fr; }
  .pane { display: flex; flex-direction: column; min-height: 0; background: var(--panel); border: 2px solid var(--grain); border-radius: 4px; overflow: hidden; }
  .pane.active { border-color: var(--accent); }
  .head { display: flex; align-items: center; gap: 6px; padding: 4px 6px 4px 10px; flex-wrap: wrap; background: var(--panel-2); border-bottom: 1px solid var(--grain); font-size: 13px; }
  .head strong { font-size: 14px; }
  .body { flex: 1; position: relative; min-height: 0; background: var(--bg); }
  .cover { position: absolute; inset: 0; display: grid; place-items: center; color: var(--muted); }
  .empty { display: grid; place-items: center; color: var(--muted); }

  .dot { flex: none; width: 8px; height: 8px; border-radius: 1px; background: var(--muted); }
  .dot.ok { background: var(--ok); }
  .dot.warn { background: var(--warn); }
  .dot.bad { background: var(--bad); }
  .chip { display: inline-flex; align-items: center; gap: 5px; height: 22px; padding: 0 7px; border-radius: 3px; background: var(--well); color: var(--muted); white-space: nowrap; }
  .chip.active { color: var(--text); }
  .chip.tw, .mini.tw { color: var(--twitch); }
  .chip.kk, .mini.kk { color: var(--kick); }
  .mini { height: 24px; min-width: 28px; justify-content: center; gap: 3px; padding: 0 6px; border-radius: 3px; background: var(--panel); color: var(--muted); font-weight: 600; }
  .mini.on { background: var(--gold-bg); border-color: var(--accent); color: var(--gold-light); box-shadow: none; }
  .mini.stop { color: var(--bad-text); }
  .mini.dim { opacity: 0.45; }
  .mini.off { text-decoration: line-through; }
  .mini.vote { color: var(--gold-light); border-color: var(--accent); }
  .lives { display: inline-flex; gap: 4px; }

  .overlay { position: fixed; inset: 44px 0 0 0; background: rgba(20, 10, 15, 0.92); display: grid; place-items: center; }
  .sheet { width: min(1000px, 96vw); max-height: 92%; overflow: auto; background: var(--panel); border: 1px solid var(--line); border-radius: 4px; padding: 14px 20px 20px; user-select: text; }
  .tabs { display: flex; gap: 2px; flex-wrap: wrap; align-items: flex-end; position: sticky; top: -14px; z-index: 1; background: var(--panel); padding: 8px 0 0; margin-bottom: 14px; border-bottom: 1px solid var(--line); }
  .tabs > button:not(.tab), .tabs > select { margin-bottom: 6px; }
  .tabs > button:not(.tab) + button:not(.tab) { margin-left: 6px; }
  .tabs .tab { height: 34px; padding: 0 14px; border: 1px solid transparent; border-bottom: 0; border-radius: 4px 4px 0 0; background: transparent; color: var(--muted); font-weight: 400; box-shadow: none; }
  .tabs .tab:hover { color: var(--text); }
  .tabs .tab.on { height: 36px; margin-bottom: -1px; background: var(--panel); color: var(--text); font-weight: 600; border-color: var(--line); border-top: 2px solid var(--accent); border-bottom: 1px solid var(--panel); }
  .hhmm { width: 80px; }
  .ask { width: min(520px, 92vw); }
  .ask p { white-space: pre-line; line-height: 1.5; margin: 4px 0 14px; }
  .form { display: flex; flex-direction: column; gap: 10px; align-items: flex-start; }
  h2, h3 { margin: 10px 0 2px; font-weight: 600; }
  h3 { font: 600 16px var(--pixel); color: var(--honey); }
  .rules td, .rules th { padding: 3px 8px 3px 0; text-align: left; font-weight: normal; color: var(--muted); }
  .rules td { color: var(--text); }
  .cols2 { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; width: 100%; }
  .card { background: var(--panel-2); border: 1px solid var(--grain); border-radius: 4px; padding: 14px 16px; display: flex; flex-direction: column; gap: 12px; margin-top: 12px; }
  .card-head { display: flex; align-items: center; gap: 10px; }
  .card-head strong { font: 600 16px var(--pixel); color: var(--honey); }
  .pill { padding: 2px 8px; border-radius: 3px; background: var(--panel); font-weight: 700; }
  .pill.tw { color: var(--twitch); }
  .pill.kk { color: var(--kick); }
  .between { justify-content: space-between; }
  .small { font-size: 13px; }
  .stepper { display: inline-flex; align-items: center; border: 1px solid var(--line); border-radius: 4px; background: var(--bg); }
  .stepper button { border: none; border-radius: 0; background: transparent; padding: 4px 12px; color: var(--wood-light); }
  .stepper b { min-width: 30px; text-align: center; font-weight: 600; font-variant-numeric: tabular-nums; }
  .grid { width: 100%; border-collapse: collapse; }
  .grid th { text-align: left; font-weight: 500; font-size: 13px; color: var(--muted); padding: 8px; border-bottom: 1px solid var(--grain); }
  .grid td { padding: 7px 8px; border-bottom: 1px solid var(--well); }
  .grid select { width: 100%; }
  .switch { appearance: none; width: 38px; height: 22px; border-radius: 4px; background: var(--panel-2); position: relative; cursor: pointer; border: 1px solid var(--line); padding: 0; box-shadow: none; }
  .switch::after { content: ''; position: absolute; top: 2px; left: 2px; width: 16px; height: 16px; border-radius: 2px; background: var(--muted); transition: left 0.12s; }
  .switch:checked { background: var(--gold-rim); border-color: var(--bronze); }
  .switch:checked::after { left: 18px; background: var(--gold-light); box-shadow: inset 0 -2px 0 var(--accent); }
  .redeems td.tw { color: var(--twitch); font-weight: 600; }
  .redeems td.kk { color: var(--kick); font-weight: 600; }
  .redeems tbody tr:nth-child(even) { background: #22141c; }
  .redeems td:first-child { font-variant-numeric: tabular-nums; white-space: nowrap; }
  .kind { padding: 1px 8px; border-radius: 3px; background: var(--well); font-size: 13px; font-weight: 600; white-space: nowrap; }
  .k-baú { color: var(--ok); background: rgba(111, 208, 138, 0.14); }
  .k-resgate { color: var(--gold-light); background: rgba(226, 169, 63, 0.16); }
  .k-guarda { color: var(--honey); background: rgba(231, 168, 109, 0.16); }
  .k-gasto-fora { color: var(--warn); background: rgba(240, 160, 60, 0.14); }
  .k-recusado, .k-baú-recusado, .k-devolvido, .k-bloqueio { color: var(--bad-text); background: rgba(216, 87, 74, 0.2); }
  .row { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
  .wide { width: 100%; }
  .grow { flex: 1; }
  .bad { color: var(--bad-text); }
  input.bad { border-color: var(--bad); }
  .log { font: 12px/1.45 Consolas, monospace; white-space: pre-wrap; margin: 0; }
</style>
