/**
 * La Roue de la Fortune — point d'entrée.
 * Connexion Discord, boucles de polling (votes, file d'attente, podium mensuel),
 * mise à jour du tableau des gains épinglé.
 */
import { Client, GatewayIntentBits } from 'discord.js';
import { createServer } from 'node:http';
import { resolve } from 'node:path';
import { loadConfig } from './config.js';
import { TopServeursClient } from './topserveurs.js';
import { ValheimClient } from './valheim.js';
import { Store } from './store.js';
import { processVotes, deliverQueue, runMonthlyIfDue, fakeVote, safeLoop } from './core.js';
import * as embeds from './embeds.js';
import { prizeLabel } from './rewards.js';
import { Logger } from './logger.js';

const { config, rewards, root } = loadConfig();

const logger = new Logger(resolve(root, 'logs', 'roue.log'));
const log = (msg, level = 'INFO') => logger.log(msg, level);

const client = new Client({ intents: [GatewayIntentBits.Guilds] });
const ts = new TopServeursClient(config.topServeurs.serverToken);
const valheim = new ValheimClient(config.valheim);
const store = new Store(resolve(root, 'state.json'));

async function send(channelId, embed) {
  try {
    const channel = await client.channels.fetch(channelId);
    await channel.send({ embeds: [embed] });
  } catch (err) {
    log(`Envoi Discord impossible (salon ${channelId}) : ${err.message}`);
  }
}

// discord.announceDelivery (défaut false) : si false, le salon public annonce
// uniquement le gain, sans aucune mention de livraison (pas d'embed "livrée",
// pas de champ "en file d'attente"). Le suivi reste dans les logs et le salon admin.
const announceDelivery = config.discord.announceDelivery === true;

async function sendText(channelId, content) {
  try {
    const channel = await client.channels.fetch(channelId);
    await channel.send({ content });
  } catch (err) {
    log(`Envoi Discord impossible (salon ${channelId}) : ${err.message}`);
  }
}

const notify = {
  async public(kind, { playername, prize }) {
    // Votant extérieur au serveur : simple ligne façon webhook Top-Serveurs.
    if (kind === 'voteOnly') {
      await sendText(config.discord.channels.public, `**${playername}** vient de voter pour le serveur !`);
      return;
    }
    if (!announceDelivery) {
      if (kind === 'deliveredLate') return; // pas d'annonce de livraison différée
      await send(config.discord.channels.public, embeds.rewardWon(playername, prize));
      return;
    }
    const embed = {
      delivered: () => embeds.rewardDelivered(playername, prize),
      queued: () => embeds.rewardQueued(playername, prize),
      deliveredLate: () => embeds.rewardDeliveredLate(playername, prize),
    }[kind]();
    await send(config.discord.channels.public, embed);
  },
  async admin(kind, payload) {
    const embed = {
      unattributed: () => embeds.adminUnattributed(payload.playername, payload.reason),
      deliveryFailed: () => embeds.adminDeliveryFailed(payload.playername, payload.detail),
      expired: () => embeds.adminExpired(payload.entry, payload.maxAgeDays),
    }[kind]();
    await send(config.discord.channels.admin, embed);
  },
  async podium(monthLabel, ranked) {
    const channelId = config.discord.channels.classement ?? config.discord.channels.public;
    await send(channelId, embeds.monthlyPodium(monthLabel, ranked));
  },
};

const ctx = { ts, valheim, store, rewards, config, notify, log, rng: Math.random };

/**
 * Mini API d'administration locale (config.admin) — réservée à 127.0.0.1, jamais exposée.
 *   POST /fakevote  body {"playername":"kris"}  header X-Auth-Token: <config.admin.token>
 * Simule un vote sans passer par Top-Serveurs (tirage, embed, give/file identiques).
 * Désactivée si config.admin.token est absent.
 */
let adminServer = null;

function startAdminApi() {
  const cfg = config.admin;
  if (!cfg?.token) { log('API admin désactivée (config.admin.token absent).'); return; }
  const port = cfg.port ?? 52859;

  adminServer = createServer(async (req, res) => {
    const reply = (status, body) => {
      res.writeHead(status, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(body));
    };
    if (req.headers['x-auth-token'] !== cfg.token) return reply(401, { success: false, error: 'token invalide' });

    if (req.method === 'GET' && req.url.startsWith('/logs')) {
      const parsedUrl = new URL(req.url, 'http://127.0.0.1');
      const limitParam = parseInt(parsedUrl.searchParams.get('limit') || '100', 10);
      const limit = isNaN(limitParam) ? 100 : limitParam;
      const logs = logger.getLogs(limit);
      return reply(200, { success: true, count: logs.length, logs });
    }

    if (req.method !== 'POST' || req.url !== '/fakevote') return reply(404, { success: false, error: 'route inconnue' });

    let raw = '';
    for await (const chunk of req) { raw += chunk; if (raw.length > 4096) return reply(413, { success: false, error: 'corps trop long' }); }
    let body;
    try { body = JSON.parse(raw || '{}'); } catch { return reply(400, { success: false, error: 'JSON invalide' }); }

    try {
      const result = await fakeVote(ctx, body.playername);
      reply(200, { success: true, outcome: result.outcome, target: result.target,
                   prize: result.prize ? prizeLabel(result.prize) : null });
    } catch (err) {
      reply(400, { success: false, error: err.message });
    }
  });

  adminServer.on('error', (err) => log(`API admin : ${err.message}`));
  adminServer.listen(port, '127.0.0.1', () => log(`API admin à l'écoute sur http://127.0.0.1:${port} (POST /fakevote, GET /logs)`));
}

/** Publie ou met à jour le message épinglé "Tableau des gains". */
async function upsertRewardsTable() {
  try {
    const channel = await client.channels.fetch(config.discord.channels.public);
    const embed = embeds.rewardsTable(rewards);
    if (store.pinnedMessageId) {
      const msg = await channel.messages.fetch(store.pinnedMessageId).catch(() => null);
      if (msg) { await msg.edit({ embeds: [embed] }); return; }
    }
    const msg = await channel.send({ embeds: [embed] });
    await msg.pin().catch(() => log('Épinglage impossible (permission "Gérer les messages" manquante ?)'));
    store.pinnedMessageId = msg.id;
  } catch (err) {
    log(`Tableau des gains non publié : ${err.message}`);
  }
}

const stoppers = [];

client.once('clientReady', async () => {
  log(`Connecté en tant que ${client.user.tag} — La Roue de la Fortune est en place ⚔️`);
  await upsertRewardsTable();
  startAdminApi();
  stoppers.push(safeLoop(() => processVotes(ctx), config.topServeurs.pollIntervalSec ?? 60, 'processVotes', log));
  stoppers.push(safeLoop(() => deliverQueue(ctx), config.queue?.retryIntervalSec ?? 60, 'deliverQueue', log));
  stoppers.push(safeLoop(() => runMonthlyIfDue(ctx), 300, 'monthly', log));
});

let isShuttingDown = false;
export function gracefulShutdown(signal = 'SIGTERM') {
  if (isShuttingDown) return;
  isShuttingDown = true;
  log(`Arrêt gracieux suite au signal ${signal}...`);

  for (const stop of stoppers) {
    try { stop(); } catch { /* ignore */ }
  }

  if (adminServer) {
    try { adminServer.close(); } catch { /* ignore */ }
  }

  try {
    client.destroy();
  } catch { /* ignore */ }

  log('Ressources libérées. Arrêt complet.');
  process.exit(0);
}

process.on('SIGINT', () => gracefulShutdown('SIGINT'));
process.on('SIGTERM', () => gracefulShutdown('SIGTERM'));

client.login(config.discord.token).catch((err) => {
  console.error(`Connexion Discord impossible : ${err.message}`);
  process.exit(1);
});
