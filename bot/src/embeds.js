/**
 * Construction des embeds Discord — conformes à la maquette validée.
 * Retourne des objets embed "bruts" (compatibles discord.js), testables sans connexion.
 */
import { prizeLabel } from './rewards.js';

const FOOTER = '🔥 Le Camp du Feu Sacré';

const TIER_COLORS = {
  commun: 0x9aa0a6,
  peucommun: 0x4caf50,
  rare: 0x3f8cff,
  tresrare: 0xb36bff,
};
const COLOR_GOLD = 0xd4a94e;
const COLOR_ADMIN = 0xf0b232;

function tierColor(tier) {
  return tier?.color ? parseInt(tier.color.replace('#', ''), 16) : TIER_COLORS[tier?.id] ?? COLOR_GOLD;
}

function tierTag(tier) {
  return `${tier.emoji ? tier.emoji + ' ' : ''}**${tier.label}**`;
}

/**
 * 🎁 Annonce simple du gain — sans AUCUNE mention de livraison.
 * Utilisée quand discord.announceDelivery = false (défaut).
 */
export function rewardWon(playername, prize) {
  return {
    color: tierColor(prize.tier),
    title: `🎁 ${playername} a fait tourner la Roue de la Fortune !`,
    fields: [{ name: 'Récompense', value: `${tierTag(prize.tier)} : ${prizeLabel(prize)}` }],
    footer: { text: `${FOOTER} · Merci pour ton vote !` },
  };
}

/** 🎁 Gain immédiat (joueur en ligne, récompense livrée). */
export function rewardDelivered(playername, prize) {
  return {
    color: tierColor(prize.tier),
    title: `🎁 ${playername} a fait tourner la Roue de la Fortune !`,
    fields: [{ name: 'Récompense', value: `${tierTag(prize.tier)} : ${prizeLabel(prize)}` }],
    footer: { text: `${FOOTER} · Livrée en jeu à l’instant · Merci pour ton vote !` },
  };
}

/** ⏳ Gain en file d'attente (joueur hors ligne). */
export function rewardQueued(playername, prize) {
  return {
    color: tierColor(prize.tier),
    title: `🎁 ${playername} a fait tourner la Roue de la Fortune !`,
    fields: [
      { name: 'Récompense', value: `${tierTag(prize.tier)} : ${prizeLabel(prize)}` },
      { name: 'Livraison', value: `⏳ ${playername} est hors ligne — sa récompense l’attend à sa prochaine connexion.` },
    ],
    footer: { text: `${FOOTER} · En file d’attente` },
  };
}

/** 📦 Livraison différée effectuée. */
export function rewardDeliveredLate(playername, prize) {
  return {
    color: tierColor(prize.tier),
    title: `📦 Récompense livrée à ${playername}`,
    description: `Sa récompense de vote vient d’être déposée dans son inventaire : ${tierTag(prize.tier)} : ${prizeLabel(prize)}`,
    footer: { text: `${FOOTER} · Bon retour parmi nous, Viking !` },
  };
}

/** ⚠️ Admin : vote au pseudo inconnu / vote introuvable au claim. */
export function adminUnattributed(playername, reason) {
  return {
    color: COLOR_ADMIN,
    title: '⚠️ Vote non attribué',
    description: reason
      ?? `Le pseudo « **${playername}** » ne correspond à aucun joueur connu du serveur. Aucune récompense envoyée.`,
    footer: { text: 'Vote reçu via API Top-Serveurs' },
  };
}

/** ❌ Admin : échec de livraison (ValheimRestApi injoignable ou refus). */
export function adminDeliveryFailed(playername, detail) {
  return {
    color: COLOR_ADMIN,
    title: '❌ Échec de livraison',
    description: `Livraison impossible pour **${playername}** : ${detail}. La récompense reste en file d’attente.`,
    footer: { text: 'Instance : Valheim Mod' },
  };
}

/** 🗑️ Admin : récompense expirée (jamais livrée). */
export function adminExpired(entry, maxAgeDays) {
  return {
    color: COLOR_ADMIN,
    title: '🗑️ Récompense expirée',
    description: `La récompense de **${entry.playername}** (${entry.prizeText}) n’a jamais pu être livrée en ${maxAgeDays} jours (joueur jamais reconnecté ou pseudo inexistant). Retirée de la file.`,
    footer: { text: 'File d’attente' },
  };
}

/** 📌 Tableau des gains (message épinglé, généré depuis rewards.json). */
export function rewardsTable(rewards) {
  const totalWeight = rewards.tiers.reduce((s, t) => s + t.weight, 0);
  return {
    color: COLOR_GOLD,
    title: '⚔️ La Roue de la Fortune — Tableau des gains',
    description:
      'Vote pour le serveur sur Top-Serveurs avec ton **pseudo exact en jeu** et fais tourner la roue ! Un vote possible toutes les 1h30.',
    fields: rewards.tiers.map((t) => ({
      name: `${t.emoji ?? ''} ${t.label} — ${Math.round((t.weight / totalWeight) * 100)} %`,
      value: t.rewards.map((r) => `${r.emoji ?? ''} ${r.label}`.trim()).join(' · '),
    })),
    footer: { text: '⚠️ Pseudo différent de ton pseudo en jeu = récompense perdue. À toi de bien l’écrire !' },
  };
}

/** 🏆 Podium mensuel des votants. */
export function monthlyPodium(monthLabel, ranked) {
  const medals = ['🥇', '🥈', '🥉', '4️⃣', '5️⃣'];
  const lines = ranked.map((r, i) =>
    `${medals[i] ?? `${i + 1}.`} **${r.playername}** — ${r.votes} votes → ${r.prizeText}`);
  return {
    color: COLOR_GOLD,
    title: `🏆 Meilleurs votants — ${monthLabel}`,
    description: lines.join('\n') || 'Aucun vote ce mois-ci.',
    footer: { text: `${FOOTER} · Merci à tous les votants !` },
  };
}

/** 📦 Récompenses en attente pour un joueur (commande slash /mes-recompenses). */
export function myPendingRewards(playername, entries) {
  if (!entries || !entries.length) {
    return {
      color: COLOR_GOLD,
      title: `📦 Récompenses en attente — ${playername}`,
      description: `Tu n'as aucune récompense en attente de livraison pour le moment.\nConnecte-toi en jeu ou vote sur Top-Serveurs pour faire tourner la roue !`,
      footer: { text: FOOTER },
    };
  }

  const lines = entries.map((e, idx) => {
    const dateStr = e.queuedAt ? `<t:${Math.floor(e.queuedAt / 1000)}:R>` : 'récemment';
    return `**${idx + 1}.** ${e.prizeText} *(en attente ${dateStr})*`;
  });

  return {
    color: COLOR_GOLD,
    title: `📦 Récompenses en attente — ${playername} (${entries.length})`,
    description: `Ces récompenses te seront livrées dès que tu seras connecté au serveur :\n\n${lines.join('\n')}`,
    footer: { text: `${FOOTER} · Reconnecte-toi en jeu pour les recevoir !` },
  };
}

/** 🏆 Classement du mois en cours (commande slash /roue-classement). */
export function currentRankingEmbed(players) {
  const medals = ['🥇', '🥈', '🥉', '4️⃣', '5️⃣', '6️⃣', '7️⃣', '8️⃣', '9️⃣', '🔟'];
  const top10 = (players || []).slice(0, 10);
  const lines = top10.map((p, i) => {
    const name = p.playername ?? p.pseudo ?? p.username ?? p.name ?? 'Inconnu';
    const votes = p.votes ?? p.count ?? 0;
    return `${medals[i] ?? `${i + 1}.`} **${name}** — ${votes} vote(s)`;
  });

  return {
    color: COLOR_GOLD,
    title: '🏆 Classement des votants du mois en cours',
    description: lines.join('\n') || 'Aucun vote enregistré pour l’instant ce mois-ci.',
    footer: { text: `${FOOTER} · Le 1er du mois à 10h, le Top 5 remporte le Trésor du Jarl et la Bourse du Viking !` },
  };
}

/** 🎉 Annonce de palier communautaire franchi. */
export function milestoneReached(milestone, totalVotes, votersCount) {
  return {
    color: COLOR_GOLD,
    title: `🎉 Palier Communautaire Débloqué ! (${totalVotes} votes)`,
    description: `Le serveur vient d'atteindre le palier de **${milestone.votes} votes** ce mois-ci !\n\n🎁 **Récompense collective :** ${milestone.reward.emoji ? milestone.reward.emoji + ' ' : ''}${milestone.reward.label}\n\n*Tous les **${votersCount} vikings** ayant voté ce mois-ci reçoivent ce cadeau dans leur file d'attente !*`,
    footer: { text: `${FOOTER} · Merci pour votre soutien au Camp du Feu Sacré !` },
  };
}
