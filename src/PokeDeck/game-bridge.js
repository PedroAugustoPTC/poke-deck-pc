// A ponte com o jogo: acompanha o socket do próprio jogo (o mesmo que os botões usam), remonta o estado da conta a
// partir do `welcome` e dos deltas de `estado`, e manda um retrato para o app (chrome.webview.postMessage). O app decide
// e devolve mensagens pelo `__pd.send`, que vão no mesmo socket, iguais às dos botões do jogo. Nada de cliques sintéticos.
(() => {
  if (window.top !== window || window.__pd || !/(^|\.)pokeidle\.io$/.test(location.hostname)) return;

  const KEYS = ['gold', 'balls', 'items', 'automation', 'activeId', 'nick', 'loja', 'twitch', 'kick'];
  const WANTED = new Set(['welcome', 'estado', 'batalha', 'erro']);
  let socket = null;
  let acc = null;
  let maxHp = new Map();
  let catalog = [];
  let ours = [];
  let mirror = null;
  let mirrorAt = 0;
  let skew = 0;

  const post = (o) => {
    try { window.chrome.webview.postMessage(o); } catch { }
  };

  const Native = window.WebSocket;
  if (typeof Native === 'function') {
    window.WebSocket = new Proxy(Native, {
      construct(target, args, newTarget) {
        const ws = Reflect.construct(target, args, newTarget);
        try {
          if (new URL(String(args[0]), location.href).host === location.host) attach(ws);
        } catch { }
        return ws;
      },
    });
  }

  function attach(ws) {
    socket = ws;
    acc = null;
    ws.addEventListener('message', (e) => {
      if (ws === socket) receive(e.data);
    });
  }

  function receive(data) {
    if (typeof data !== 'string') return;
    const head = /^\{"t":"([^"]*)"/.exec(data);
    if (head && (!WANTED.has(head[1]) || (head[1] === 'batalha' && !data.includes('"k":"compra"')))) return;
    let m;
    try { m = JSON.parse(data); } catch { return; }
    if (m.t === 'welcome') {
      catalog = Array.isArray(m.catalogoBolas) ? m.catalogoBolas : [];
      ours = Array.isArray(m.itensNossos) ? m.itensNossos : [];
      merge(m.estado, true);
      loadMirror();
    } else if (m.t === 'estado') {
      merge(m.estado, false);
    } else if (m.t === 'batalha') {
      for (const e of m.ev ?? []) if (e?.k === 'compra') post({ t: 'bought', qtd: Number(e.qtd) || 0, gasto: Number(e.gasto) || 0 });
    } else if (m.t === 'erro') {
      post({ t: 'refused', msg: String(m.msg || m.chave || 'sem motivo') });
    }
  }

  // Chave ausente é "não mudou"; `cheio` (welcome, reconexão) recomeça do zero.
  function merge(state, full) {
    if (!state || typeof state !== 'object') return;
    if (full || state.cheio || !acc) {
      acc = {};
      maxHp = new Map();
    }
    for (const key of KEYS) if (key in state) acc[key] = state[key];
    if (Array.isArray(state.pokemons)) maxHp = new Map(state.pokemons.map((p) => [p.id, p.maxHp]));
    for (const p of state.pkMud ?? []) maxHp.set(p.id, p.maxHp);
    for (const id of state.pkFora ?? []) maxHp.delete(id);
    if (Number(state.servidorAgora) > 0) skew = Date.now() - Number(state.servidorAgora);
  }

  function loadMirror() {
    if (mirror || Date.now() < mirrorAt) return;
    mirrorAt = Date.now() + 30000;
    fetch('/assets/items.json', { cache: 'no-store' })
      .then((r) => r.json())
      .then((j) => { mirror = (j.items ?? []).filter((i) => i.category === 'heal' || i.category === 'revive'); })
      .catch(() => { });
  }

  function site(block) {
    if (!block || typeof block !== 'object') return null;
    const now = Date.now() - skew;
    const left = {};
    for (const [kind, until] of Object.entries(block.bonus ?? {})) left[kind] = Math.max(0, Math.round((Number(until) - now) / 1000)) || 0;
    return {
      linked: !!(block.vinculado ?? block.login),
      left,
      channels: (Array.isArray(block.oficiais) ? block.oficiais : []).filter((o) => o?.login && o.ativo !== false)
        .map((o) => ({ login: String(o.login), live: !!o.aoVivo })),
    };
  }

  function phase() {
    if (socket?.readyState === 1 && acc) return 'game';
    const p = document.querySelector('#portal'), l = document.querySelector('#login');
    if (p && l && !p.classList.contains('hidden') && !l.classList.contains('hidden')) return 'login';
    if (/outro (lugar|aparelho|dispositivo)/i.test(document.body?.innerText ?? '')) return 'elsewhere';
    return 'loading';
  }

  function snapshot() {
    const s = { t: 'state', phase: phase(), connected: socket?.readyState === 1 && !!acc };
    if (!acc) return s;
    if (!mirror) loadMirror();
    const shop = [];
    const seen = new Set();
    for (const i of [...(mirror ?? []), ...ours]) {
      if ((i?.category !== 'heal' && i?.category !== 'revive') || !(i.npcPrice > 0) || seen.has(i.id)) continue;
      seen.add(i.id);
      shop.push({ id: i.id, name: i.name, category: i.category, npcPrice: i.npcPrice, healAmount: Number(i.healAmount) || 0, healPct: Number(i.healPct) || 0 });
    }
    return Object.assign(s, {
      nick: acc.nick ?? null,
      gold: Number(acc.gold) || 0,
      vip: !!acc.loja?.vip,
      balls: acc.balls ?? {},
      items: acc.items ?? {},
      automation: acc.automation ?? null,
      maxHp: maxHp.get(acc.activeId) ?? 0,
      ballCatalog: catalog.map((b) => ({ id: b.id, nome: b.nome, priceGold: b.priceGold, compravel: !!b.compravel })),
      shop,
      shopLoaded: !!mirror,
      twitch: site(acc.twitch),
      kick: site(acc.kick),
    });
  }

  // Limite de quadros (técnica do PokeIdleMulti): o requestAnimationFrame do jogo espera o próximo quadro permitido
  // por um relógio. window.__pdFps (0 ou 60+: sem limite) e window.__pdSpriteMs são lidos a cada pedido: mudam na hora.
  // O app põe a conta em uso fluida e as outras devagar; a caça não muda, só o desenho.
  const raf = window.requestAnimationFrame.bind(window), caf = window.cancelAnimationFrame.bind(window);
  const pending = new Map();
  let nextId = 1, due = 0, last = -1;
  const limit = () => { const fps = +window.__pdFps || 0; return fps > 0 && fps < 60 ? fps : 0; };
  const frame = (id, cb) => raf((t) => {
    if (!pending.delete(id)) return;
    const fps = limit();
    if (fps && t !== last) { last = t; due = performance.now() + 1000 / fps - 8; }
    cb(t);
  });
  window.requestAnimationFrame = (cb) => {
    const id = nextId++, entry = {};
    pending.set(id, entry);
    const wait = limit() ? due - performance.now() : 0;
    if (wait > 1) entry.timer = setTimeout(() => { if (pending.has(id)) entry.frame = frame(id, cb); }, wait);
    else entry.frame = frame(id, cb);
    return id;
  };
  window.cancelAnimationFrame = (id) => {
    const entry = pending.get(id);
    if (!entry) return;
    pending.delete(id);
    clearTimeout(entry.timer);
    if (entry.frame) caf(entry.frame);
  };

  // Os sprites animados do jogo usam um setInterval de exatos 220 ms: com __pdSpriteMs maior, pula trocas de quadro.
  const nativeInterval = window.setInterval.bind(window);
  window.setInterval = function (fn, ms, ...rest) {
    if (ms !== 220 || typeof fn !== 'function') return nativeInterval(fn, ms, ...rest);
    let lastTick = 0;
    return nativeInterval(function () {
      const want = Math.max(220, +window.__pdSpriteMs || 220);
      if (want > 225) {
        const now = performance.now();
        if (now - lastTick < want - 5) return;
        lastTick = now;
      }
      return fn.apply(this, arguments);
    }, 220, ...rest);
  };

  // F11 dentro do jogo: tela cheia do app (a página do jogo não deve entrar em tela cheia sozinha).
  window.addEventListener('keydown', (e) => {
    if (e.key === 'F11') {
      e.preventDefault();
      post({ t: 'fullscreen' });
    }
  }, true);

  nativeInterval(() => post(snapshot()), 1500);

  Object.defineProperty(window, '__pd', {
    value: Object.freeze({
      send(text) {
        if (socket?.readyState !== 1 || typeof text !== 'string') return false;
        socket.send(text);
        return true;
      },
    }),
  });
})();
