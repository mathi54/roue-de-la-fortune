/**
 * La Roue de la Fortune — point d'entrée.
 * Connexion Discord, boucles de polling (votes, file d'attente, podium mensuel),
 * mise à jour du tableau des gains épinglé.
 */
import { Client, GatewayIntentBits } from 'discord.js';
import { resolve } from 'node:path';
import { loadConfig } from './config.js';
import { TopServeursClient } from './topserveurs.js';
import { ValheimClient } from './valheim.js';
import { Store } from './store.js';
import { processVotes, deliverQueue, runMonthlyIfDue } from './core.js';
import * as embeds from './embeds.js';

const { config, rewards, root } = loadConfig();

const log = (msg) => console.log(`[${new Date().toISOString()}] ${msg}`);

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

function loop(fn, intervalSec, label) {
  const run = () => fn().catch((err) => log(`${label} : ${err.message}`));
  run();
  setInterval(run, intervalSec * 1000);
}

client.once('clientReady', async () => {
  log(`Connecté en tant que ${client.user.tag} — La Roue de la Fortune est en place ⚔️`);
  await upsertRewardsTable();
  loop(() => processVotes(ctx), config.topServeurs.pollIntervalSec ?? 60, 'processVotes');
  loop(() => deliverQueue(ctx), config.queue?.retryIntervalSec ?? 60, 'deliverQueue');
  loop(() => runMonthlyIfDue(ctx), 300, 'monthly');
});

client.login(config.discord.token).catch((err) => {
  console.error(`Connexion Discord impossible : ${err.message}`);
  process.exit(1);
});
