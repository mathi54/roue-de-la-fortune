/**
 * Tests de la Roue de la Fortune — logique métier complète avec clients simulés.
 * Lancement : npm test
 */
import assert from 'node:assert/strict';
import { readFileSync, rmSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

import { drawReward, drawAmount, prizeLabel, monthlyPrizeForRank } from '../src/rewards.js';
import { voteName, voteKey, TopServeursClient } from '../src/topserveurs.js';
import { Store } from '../src/store.js';
import { processVotes, deliverQueue, runMonthlyIfDue } from '../src/core.js';
import * as embeds from '../src/embeds.js';

const here = dirname(fileURLToPath(import.meta.url));
const rewards = JSON.parse(readFileSync(resolve(here, '../rewards.example.json'), 'utf8'));

let passed = 0;
const failures = [];
async function test(name, fn) {
  try { await fn(); passed++; console.log(`  ✔ ${name}`); }
  catch (err) { failures.push(name); console.error(`  ✘ ${name}\n    ${err.message}`); }
}

// ---------- Fakes ----------
function fakeNotify() {
  const calls = { public: [], admin: [], podium: [] };
  return {
    calls,
    async public(kind, payload) { calls.public.push({ kind, ...payload }); },
    async admin(kind, payload) { calls.admin.push({ kind, ...payload }); },
    async podium(monthLabel, ranked) { calls.podium.push({ monthLabel, ranked }); },
  };
}

function fakeTs({ votes = [], claims = {}, ranking = [] } = {}) {
  return {
    claimCalls: [],
    async lastVotes() { return votes; },
    async claimUsername(name) {
      this.claimCalls.push(name);
      const r = claims[name];
      if (r instanceof Error) throw r;
      return r ?? 0;
    },
    async playersRanking() { return ranking; },
  };
}

function fakeValheim({ online = [], giveOk = true, playersError = null } = {}) {
  return {
    gives: [],
    async onlinePlayers() {
      if (playersError) throw playersError;
      return online;
    },
    async isOnline(name) {
      return (await this.onlinePlayers()).some((p) => p.toLowerCase() === name.toLowerCase());
    },
    async give(playername, item, amount) {
      this.gives.push({ playername, item, amount });
      return giveOk;
    },
  };
}

const tmp = mkdtempSync(join(tmpdir(), 'voterewards-'));
let storeCounter = 0;
function freshStore() {
  return new Store(join(tmp, `state-${storeCounter++}.json`));
}

function makeCtx(overrides = {}) {
  return {
    ts: fakeTs(),
    valheim: fakeValheim(),
    store: freshStore(),
    rewards,
    config: { queue: { maxAgeDays: 30 }, monthly: { enabled: true, dayOfMonth: 1, hour: 10 } },
    notify: fakeNotify(),
    log: () => {},
    rng: Math.random,
    // Par défaut dans les tests : les joueurs listés sont considérés stables
    // (en ligne depuis plus d'un cycle). Les tests de la fenêtre de stabilité
    // écrasent ce champ.
    _prevOnline: new Set(['mathi', 'ketil', 'freyja', 'thorvald', 'talex']),
    ...overrides,
  };
}

// ---------- rewards.js ----------
console.log('rewards.js');
await test('drawAmount : plage [min,max] inclusive', () => {
  for (let i = 0; i < 200; i++) {
    const n = drawAmount([10, 30]);
    assert.ok(n >= 10 && n <= 30, `hors plage : ${n}`);
  }
  assert.equal(drawAmount(5), 5);
  assert.equal(drawAmount(undefined), 1);
});

await test('drawReward : respecte les poids (rng contrôlé)', () => {
  // rng -> 0 : premier tier (commun), premier item
  const low = drawReward(rewards, () => 0);
  assert.equal(low.tier.id, 'commun');
  // rng -> 0.999 : dernier tier (tresrare)
  const high = drawReward(rewards, () => 0.999);
  assert.equal(high.tier.id, 'tresrare');
});

await test('drawReward : distribution ~conforme sur 20 000 tirages', () => {
  const counts = {};
  for (let i = 0; i < 20000; i++) {
    const { tier } = drawReward(rewards);
    counts[tier.id] = (counts[tier.id] ?? 0) + 1;
  }
  const pct = (id) => (counts[id] ?? 0) / 20000 * 100;
  assert.ok(Math.abs(pct('commun') - 60) < 3, `commun: ${pct('commun')}%`);
  assert.ok(Math.abs(pct('tresrare') - 3) < 1.5, `tresrare: ${pct('tresrare')}%`);
});

await test('prizeLabel : format lisible', () => {
  const label = prizeLabel({ reward: { label: 'Piastres', emoji: '💰' }, amount: 20, tier: {} });
  assert.equal(label, '💰 20 × Piastres');
});

await test('monthlyPrizeForRank : top3 / rank4to5 / au-delà', () => {
  assert.equal(monthlyPrizeForRank(rewards, 1).label, '🏆 Trésor du Jarl');
  assert.equal(monthlyPrizeForRank(rewards, 3).label, '🏆 Trésor du Jarl');
  assert.equal(monthlyPrizeForRank(rewards, 4).label, '🎖️ Bourse du Viking');
  assert.equal(monthlyPrizeForRank(rewards, 5).label, '🎖️ Bourse du Viking');
  assert.equal(monthlyPrizeForRank(rewards, 6), null);
});

// ---------- topserveurs.js ----------
console.log('topserveurs.js');
await test('voteName : tolère les différents noms de champ', () => {
  assert.equal(voteName({ playername: 'Mathi' }), 'Mathi');
  assert.equal(voteName({ pseudo: ' Ketil ' }), 'Ketil');
  assert.equal(voteName({ username: 'Freyja' }), 'Freyja');
  assert.equal(voteName({}), null);
});

await test('voteKey : stable et insensible à la casse', () => {
  assert.equal(voteKey({ playername: 'Mathi', datetime: '2026-08-13 10:00' }),
    voteKey({ playername: 'MATHI', datetime: '2026-08-13 10:00' }));
});

await test('TopServeursClient : parse les réponses (fetch simulé)', async () => {
  const responses = {
    '/votes/last': { status: 200, body: { code: 200, success: true, votes: [{ playername: 'Mathi', datetime: 'd1' }] } },
    '/votes/claim-username': { status: 200, body: { code: 200, success: true, claimed: 1, message: 'ok' } },
  };
  const client = new TopServeursClient('tok', async (url) => {
    const found = Object.entries(responses).find(([path]) => url.pathname.endsWith(path));
    return { status: found[1].status, ok: true, json: async () => found[1].body };
  });
  const votes = await client.lastVotes();
  assert.equal(votes.length, 1);
  assert.equal(await client.claimUsername('Mathi'), 1);
});

await test('ValheimClient : décode le format réel de JsonBuilder (players = chaînes JSON échappées)', async () => {
  const { ValheimClient, extractPlayerName } = await import('../src/valheim.js');
  // Format réellement renvoyé par le mod : tableau de CHAÎNES contenant du JSON
  const realResponse = {
    success: true,
    count: 2,
    players: [
      '{"name":"Grudu","steam_id":"76561198035607011","uid":123,"position":{"x":1,"y":2,"z":3}}',
      '{"name":"Ketil","steam_id":"765611980356070XX","uid":456,"position":{"x":4,"y":5,"z":6}}',
    ],
  };
  const client = new ValheimClient({ baseUrl: 'http://127.0.0.1:52858' }, async () =>
    ({ ok: true, json: async () => realResponse }));
  assert.deepEqual(await client.onlinePlayers(), ['Grudu', 'Ketil']);
  assert.equal(await client.isOnline('grudu'), true);
  // Tolérance aux trois formats possibles
  assert.equal(extractPlayerName({ name: 'Mathi' }), 'Mathi');
  assert.equal(extractPlayerName('Mathi'), 'Mathi');
  assert.equal(extractPlayerName('{"name":"Mathi"}'), 'Mathi');
  assert.equal(extractPlayerName('{invalid json'), null);
});

await test('ValheimClient : parse le /players objet + header X-Auth-Token', async () => {
  const { ValheimClient } = await import('../src/valheim.js');
  const requests = [];
  const client = new ValheimClient({ baseUrl: 'http://127.0.0.1:8080', apiToken: 'secret' }, async (url, opts = {}) => {
    requests.push({ url: String(url), headers: opts.headers });
    if (String(url).endsWith('/players')) {
      return { ok: true, json: async () => ({ success: true, count: 2, players: [
        { name: 'Mathi', steam_id: '765...', position: { x: 1, y: 2, z: 3 } },
        { name: 'Ketil', steam_id: '765...', position: { x: 4, y: 5, z: 6 } },
      ] }) };
    }
    return { ok: true, json: async () => ({ success: true, drops: 1 }) };
  });
  const players = await client.onlinePlayers();
  assert.deepEqual(players, ['Mathi', 'Ketil']);
  assert.equal(await client.isOnline('MATHI'), true);
  await client.give('Mathi', 'Coins', 20);
  assert.ok(requests.every((r) => r.headers['X-Auth-Token'] === 'secret'));
});

// ---------- store.js ----------
console.log('store.js');
await test('Store : persistance seen/queue et rechargement', () => {
  const path = join(tmp, 'persist.json');
  const s1 = new Store(path);
  s1.markSeen('mathi|d1');
  s1.enqueue({ playername: 'Ketil', item: 'Coins', amount: 20, prizeText: '💰 20 × Piastres', tierId: 'commun' });
  const s2 = new Store(path);
  assert.ok(s2.hasSeen('mathi|d1'));
  assert.equal(s2.queue.length, 1);
  rmSync(path);
});

await test('Store : takeDeliverable insensible à la casse, expireQueue', () => {
  let clock = 0;
  const s = new Store(join(tmp, 'q.json'), () => clock);
  s.enqueue({ playername: 'Ketil', item: 'Coins', amount: 20, prizeText: 'x', tierId: 'commun' });
  s.enqueue({ playername: 'Freyja', item: 'Ruby', amount: 1, prizeText: 'y', tierId: 'commun' });
  const d = s.takeDeliverable(['KETIL']);
  assert.equal(d.length, 1);
  assert.equal(d[0].playername, 'Ketil');
  assert.equal(s.queue.length, 1);
  clock = 31 * 24 * 3600 * 1000; // 31 jours plus tard
  const expired = s.expireQueue(30);
  assert.equal(expired.length, 1);
  assert.equal(expired[0].playername, 'Freyja');
  assert.equal(s.queue.length, 0);
});

// ---------- core.js : processVotes ----------
console.log('core.js — processVotes');
await test('vote + joueur en ligne → claim, give, annonce "delivered"', async () => {
  const ctx = makeCtx({
    ts: fakeTs({ votes: [{ playername: 'Mathi', datetime: 'd1' }], claims: { Mathi: 1 } }),
    valheim: fakeValheim({ online: ['Mathi'] }),
  });
  await processVotes(ctx);
  assert.equal(ctx.ts.claimCalls.length, 1);
  assert.equal(ctx.valheim.gives.length, 1);
  assert.equal(ctx.valheim.gives[0].playername, 'Mathi');
  assert.equal(ctx.notify.calls.public[0].kind, 'delivered');
  assert.equal(ctx.store.queue.length, 0);
});

await test('vote + joueur hors ligne → mise en file + annonce "queued"', async () => {
  const ctx = makeCtx({
    ts: fakeTs({ votes: [{ playername: 'Ketil', datetime: 'd2' }], claims: { Ketil: 1 } }),
    valheim: fakeValheim({ online: [] }),
  });
  await processVotes(ctx);
  assert.equal(ctx.valheim.gives.length, 0);
  assert.equal(ctx.store.queue.length, 1);
  assert.equal(ctx.notify.calls.public[0].kind, 'queued');
});

await test('vote déjà réclamé (claimed=2) → silencieux, pas de double récompense', async () => {
  const ctx = makeCtx({
    ts: fakeTs({ votes: [{ playername: 'Mathi', datetime: 'd3' }], claims: { Mathi: 2 } }),
  });
  await processVotes(ctx);
  assert.equal(ctx.valheim.gives.length, 0);
  assert.equal(ctx.store.queue.length, 0);
  assert.equal(ctx.notify.calls.public.length, 0);
  assert.equal(ctx.notify.calls.admin.length, 0);
});

await test('même vote vu deux fois dans l\'heure → un seul claim', async () => {
  const votes = [{ playername: 'Mathi', datetime: 'd5' }];
  const ctx = makeCtx({
    ts: fakeTs({ votes, claims: { Mathi: 1 } }),
    valheim: fakeValheim({ online: ['Mathi'] }),
  });
  await processVotes(ctx);
  await processVotes(ctx); // second cycle de poll, même vote présent
  assert.equal(ctx.ts.claimCalls.length, 1);
  assert.equal(ctx.valheim.gives.length, 1);
});

await test('re-vote 1h30 plus tard (datetime différent) → nouvelle récompense', async () => {
  const ts = fakeTs({ votes: [{ playername: 'Mathi', datetime: 'd6' }], claims: { Mathi: 1 } });
  const ctx = makeCtx({ ts, valheim: fakeValheim({ online: ['Mathi'] }) });
  await processVotes(ctx);
  ts.lastVotes = async () => [{ playername: 'Mathi', datetime: 'd7' }];
  await processVotes(ctx);
  assert.equal(ctx.valheim.gives.length, 2);
});

await test('erreur réseau au claim → vote NON marqué vu, retenté au cycle suivant', async () => {
  const ts = fakeTs({ votes: [{ playername: 'Mathi', datetime: 'd8' }], claims: { Mathi: new Error('timeout') } });
  const ctx = makeCtx({ ts, valheim: fakeValheim({ online: ['Mathi'] }) });
  await processVotes(ctx);
  assert.equal(ctx.valheim.gives.length, 0);
  ts.claimUsername = async function (name) { this.claimCalls.push(name); return 1; };
  await processVotes(ctx);
  assert.equal(ctx.valheim.gives.length, 1);
});

await test('ValheimRestApi injoignable au moment du vote → file + annonce "queued"', async () => {
  const ctx = makeCtx({
    ts: fakeTs({ votes: [{ playername: 'Freyja', datetime: 'd9' }], claims: { Freyja: 1 } }),
    valheim: fakeValheim({ playersError: new Error('ECONNREFUSED') }),
  });
  await processVotes(ctx);
  assert.equal(ctx.store.queue.length, 1);
  assert.equal(ctx.notify.calls.public[0].kind, 'queued');
});

await test('give refusé alors que le joueur est en ligne → file + log admin', async () => {
  const ctx = makeCtx({
    ts: fakeTs({ votes: [{ playername: 'Mathi', datetime: 'd10' }], claims: { Mathi: 1 } }),
    valheim: fakeValheim({ online: ['Mathi'], giveOk: false }),
  });
  await processVotes(ctx);
  assert.equal(ctx.store.queue.length, 1);
  assert.equal(ctx.notify.calls.admin[0].kind, 'deliveryFailed');
});

await test('votant extérieur au serveur → simple annonce "voteOnly", pas de récompense ni de file', async () => {
  const ctx = makeCtx({
    ts: fakeTs({ votes: [{ playername: 'Ben oui', datetime: 'du1' }], claims: { 'Ben oui': 1 } }),
    valheim: fakeValheim({ online: [] }),
  });
  ctx.store.learnPlayers(['Grudu', 'Ketil']); // le bot connaît déjà des joueurs
  await processVotes(ctx);
  assert.equal(ctx.store.queue.length, 0);
  assert.equal(ctx.notify.calls.public.length, 1);
  assert.equal(ctx.notify.calls.public[0].kind, 'voteOnly');
  assert.equal(ctx.notify.calls.public[0].playername, 'Ben oui');
  assert.equal(ctx.notify.calls.admin.length, 0);
});

await test('claim introuvable (claimed=0) → silencieux aussi, aucun message Discord', async () => {
  const ctx = makeCtx({
    ts: fakeTs({ votes: [{ playername: 'Grudu', datetime: 'du0' }], claims: { Grudu: 0 } }),
  });
  ctx.store.learnPlayers(['Grudu']);
  await processVotes(ctx);
  assert.equal(ctx.notify.calls.admin.length, 0);
  assert.equal(ctx.notify.calls.public.length, 0);
  assert.equal(ctx.store.queue.length, 0);
});

await test('joueur connu (appris via /players, casse différente) → récompense normale', async () => {
  const ctx = makeCtx({
    ts: fakeTs({ votes: [{ playername: 'grudu', datetime: 'du2' }], claims: { grudu: 1 } }),
    valheim: fakeValheim({ online: [] }),
  });
  ctx.store.learnPlayers(['Grudu']);
  await processVotes(ctx);
  assert.equal(ctx.store.queue.length, 1);
  assert.equal(ctx.notify.calls.admin.length, 0);
});

await test('démarrage à froid (aucun joueur encore appris) → filtre inactif, vote accepté', async () => {
  const ctx = makeCtx({
    ts: fakeTs({ votes: [{ playername: 'Nouveau', datetime: 'du3' }], claims: { Nouveau: 1 } }),
    valheim: fakeValheim({ online: [] }),
  });
  await processVotes(ctx);
  assert.equal(ctx.store.queue.length, 1);
});

await test('deliverQueue apprend les joueurs en ligne dans knownPlayers', async () => {
  const ctx = makeCtx({ valheim: fakeValheim({ online: ['Grudu', 'Freyja'] }) });
  await deliverQueue(ctx);
  assert.ok(ctx.store.isKnownPlayer('grudu') && ctx.store.isKnownPlayer('FREYJA'));
});

await test('alias : « Ketil » vote → récompense pour « Andromaque », claim au pseudo de vote', async () => {
  const ctx = makeCtx({
    ts: fakeTs({ votes: [{ playername: 'Ketil', datetime: 'da1' }], claims: { Ketil: 1 } }),
    valheim: fakeValheim({ online: [] }),
  });
  ctx.config.aliases = { 'ketil': 'Andromaque' };
  ctx.store.learnPlayers(['Andromaque']);
  await processVotes(ctx);
  assert.deepEqual(ctx.ts.claimCalls, ['Ketil']); // le claim utilise le pseudo tel que voté
  assert.equal(ctx.store.queue.length, 1);
  assert.equal(ctx.store.queue[0].playername, 'Andromaque');
  assert.equal(ctx.notify.calls.public[0].playername, 'Andromaque');
});

await test('alias : insensible à la casse, et sans alias la règle du nom exact demeure', async () => {
  const ctx = makeCtx({
    ts: fakeTs({ votes: [{ playername: 'KRIS', datetime: 'da2' }, { playername: 'Inconnu', datetime: 'da3' }],
                 claims: { KRIS: 1, Inconnu: 1 } }),
    valheim: fakeValheim({ online: [] }),
  });
  ctx.config.aliases = { 'Kris': 'Paikan24' };
  ctx.store.learnPlayers(['Paikan24']);
  await processVotes(ctx);
  assert.equal(ctx.store.queue.length, 1); // KRIS → Paikan24 ; Inconnu → voteOnly
  assert.equal(ctx.store.queue[0].playername, 'Paikan24');
  assert.ok(ctx.notify.calls.public.some((c) => c.kind === 'voteOnly' && c.playername === 'Inconnu'));
});

// ---------- core.js : deliverQueue ----------
console.log('core.js — deliverQueue');
await test('joueur se connecte → livraison différée + annonce "deliveredLate"', async () => {
  const ctx = makeCtx({ valheim: fakeValheim({ online: ['Ketil'] }) });
  ctx.store.enqueue({ playername: 'Ketil', item: 'MeadStaminaMinor', amount: 3, prizeText: '🍯 3 × Hydromel', tierId: 'peucommun' });
  await deliverQueue(ctx);
  assert.equal(ctx.valheim.gives.length, 1);
  assert.equal(ctx.store.queue.length, 0);
  assert.equal(ctx.notify.calls.public[0].kind, 'deliveredLate');
});

await test('fenêtre de stabilité : pas de livraison au 1er cycle (écran de chargement), livrée au 2e', async () => {
  const ctx = makeCtx({ valheim: fakeValheim({ online: ['Grudu'] }), _prevOnline: new Set() });
  ctx.store.enqueue({ playername: 'Grudu', item: 'Amber', amount: 3, prizeText: '🟠 3 × Ambre', tierId: 'commun' });
  await deliverQueue(ctx); // 1er cycle : Grudu vient d'apparaître → PAS de give
  assert.equal(ctx.valheim.gives.length, 0);
  assert.equal(ctx.store.queue.length, 1);
  await deliverQueue(ctx); // 2e cycle : Grudu vu deux fois → give
  assert.equal(ctx.valheim.gives.length, 1);
  assert.equal(ctx.store.queue.length, 0);
});

await test('fenêtre de stabilité : vote d\'un joueur fraîchement connecté → mis en file, pas de give direct', async () => {
  const ctx = makeCtx({
    ts: fakeTs({ votes: [{ playername: 'Grudu', datetime: 'ds1' }], claims: { Grudu: 1 } }),
    valheim: fakeValheim({ online: ['Grudu'] }),
    _prevOnline: new Set(),
  });
  await processVotes(ctx);
  assert.equal(ctx.valheim.gives.length, 0);
  assert.equal(ctx.store.queue.length, 1);
  assert.equal(ctx.notify.calls.public[0].kind, 'queued');
});

await test('give différé échoue → remise en file, pas de perte', async () => {
  const ctx = makeCtx({ valheim: fakeValheim({ online: ['Ketil'], giveOk: false }) });
  ctx.store.enqueue({ playername: 'Ketil', item: 'Coins', amount: 20, prizeText: 'x', tierId: 'commun' });
  await deliverQueue(ctx);
  assert.equal(ctx.store.queue.length, 1);
  assert.equal(ctx.notify.calls.admin[0].kind, 'deliveryFailed');
});

await test('entrée expirée → retirée + log admin "expired"', async () => {
  let clock = 0;
  const store = new Store(join(tmp, 'exp.json'), () => clock);
  store.enqueue({ playername: 'MathiVeutDuGras', item: 'Coins', amount: 20, prizeText: 'x', tierId: 'commun' });
  clock = 31 * 24 * 3600 * 1000;
  const ctx = makeCtx({ store, valheim: fakeValheim({ online: [] }) });
  await deliverQueue(ctx);
  assert.equal(store.queue.length, 0);
  assert.equal(ctx.notify.calls.admin[0].kind, 'expired');
});

// ---------- core.js : podium mensuel ----------
console.log('core.js — podium mensuel');
const ranking = [
  { playername: 'Mathi', votes: 42 },
  { playername: 'Ketil', votes: 38 },
  { playername: 'Freyja', votes: 30 },
  { playername: 'Thorvald', votes: 22 },
  { playername: 'Talex', votes: 19 },
  { playername: 'Zork', votes: 12 },
];

await test('le 1er du mois à 10h → distribution top 3 + rangs 4-5, pas le 6e', async () => {
  const ctx = makeCtx({
    ts: fakeTs({ ranking }),
    valheim: fakeValheim({ online: ['Mathi'] }),
  });
  await runMonthlyIfDue(ctx, new Date(2026, 8, 1, 10, 5)); // 1er sept. 2026 10:05
  assert.equal(ctx.notify.calls.podium.length, 1);
  const names = new Set(ctx.store.queue.map((e) => e.playername).concat(ctx.valheim.gives.map((g) => g.playername)));
  assert.ok(names.has('Ketil') && names.has('Talex'));
  assert.ok(!names.has('Zork'));
  // Mathi (en ligne) a reçu ses items du Trésor du Jarl immédiatement
  assert.ok(ctx.valheim.gives.some((g) => g.playername === 'Mathi'));
});

await test('pas le bon jour → rien ne se passe', async () => {
  const ctx = makeCtx({ ts: fakeTs({ ranking }) });
  await runMonthlyIfDue(ctx, new Date(2026, 8, 15, 12, 0));
  assert.equal(ctx.notify.calls.podium.length, 0);
});

await test('déjà distribué ce mois-ci → aucune double distribution', async () => {
  const ctx = makeCtx({ ts: fakeTs({ ranking }) });
  await runMonthlyIfDue(ctx, new Date(2026, 8, 1, 10, 5));
  const queued = ctx.store.queue.length;
  await runMonthlyIfDue(ctx, new Date(2026, 8, 1, 11, 0));
  assert.equal(ctx.store.queue.length, queued);
  assert.equal(ctx.notify.calls.podium.length, 1);
});

// ---------- embeds.js ----------
console.log('embeds.js');
await test('les embeds contiennent les infos clés de la maquette', () => {
  const prize = { tier: rewards.tiers[2], reward: rewards.tiers[2].rewards[0], amount: 1 };
  const e1 = embeds.rewardDelivered('Mathi', prize);
  assert.ok(e1.title.includes('Mathi') && e1.title.includes('Roue de la Fortune'));
  assert.equal(e1.color, 0x3f8cff); // couleur du tier rare
  const e2 = embeds.rewardQueued('Ketil', prize);
  assert.ok(e2.fields.some((f) => f.value.includes('hors ligne')));
  const table = embeds.rewardsTable(rewards);
  assert.equal(table.fields.length, rewards.tiers.length);
  assert.ok(table.fields[0].name.includes('60 %'));
  const podium = embeds.monthlyPodium('juillet 2026', [
    { playername: 'Mathi', votes: 42, prizeText: '🏆 Trésor du Jarl' },
  ]);
  assert.ok(podium.description.includes('🥇') && podium.description.includes('Mathi'));
});

// ---------- bilan ----------
rmSync(tmp, { recursive: true, force: true });
console.log(`\n${passed} tests réussis, ${failures.length} échec(s)`);
if (failures.length) {
  console.error('Échecs :', failures.join(' | '));
  process.exit(1);
}
