/**
 * Logique métier de la Roue de la Fortune — indépendante de Discord (testable à 100 %).
 * Le contexte (ctx) injecte : ts (TopServeursClient), valheim (ValheimClient),
 * store (Store), rewards, config, notify (annonces), log, rng, now.
 */
import { voteName, voteKey } from './topserveurs.js';
import { drawReward, prizeLabel, monthlyPrizeForRank } from './rewards.js';

/** Cycle principal : détecte les nouveaux votes, les réclame, tire et livre. */
export async function processVotes(ctx) {
  let votes;
  try {
    votes = await ctx.ts.lastVotes();
  } catch (err) {
    ctx.log(`Top-Serveurs injoignable : ${err.message}`);
    return;
  }

  for (const vote of votes) {
    const name = voteName(vote);
    if (!name) continue;
    const key = voteKey(vote);
    if (ctx.store.hasSeen(key)) continue;

    let claimed;
    try {
      claimed = await ctx.ts.claimUsername(name);
    } catch (err) {
      ctx.log(`claim-username(${name}) en erreur : ${err.message} — on réessaiera au prochain cycle`);
      continue; // pas de markSeen : le vote sera retenté
    }

    ctx.store.markSeen(key);

    if (claimed === 2) continue; // déjà récompensé (redémarrage du bot, double poll…)
    if (claimed === 0) {
      // Vote introuvable au claim (fenêtre 2 h dépassée…) : silencieux aussi,
      // même logique anti-flood — trace en console uniquement.
      ctx.log(`Vote de « ${name} » introuvable au claim — ignoré.`);
      continue;
    }

    // Alias : le pseudo de vote peut différer du personnage en jeu.
    const target = resolveAlias(ctx, name);
    if (target !== name) ctx.log(`Alias : vote de « ${name} » → personnage « ${target} ».`);

    // Filtre "joueur du serveur" : le personnage doit avoir été vu au moins une
    // fois en jeu (liste apprise via /players). Un votant EXTÉRIEUR au serveur
    // donne droit à une simple ligne "X vient de voter pour le serveur !"
    // (comme le webhook Top-Serveurs), sans récompense ni tirage — rien d'autre.
    // Sécurité : tant que le bot n'a vu personne (démarrage à froid), filtre inactif.
    if (ctx.config.rejectUnknownPlayers !== false &&
        ctx.store.knownPlayerCount > 0 &&
        !ctx.store.isKnownPlayer(target)) {
      ctx.log(`Vote de « ${name} » (extérieur au serveur) : annonce simple, pas de récompense.`);
      await ctx.notify.public('voteOnly', { playername: name });
      continue;
    }

    // claimed === 1 : vote réclamé, on fait tourner la roue
    const prize = drawReward(ctx.rewards, ctx.rng);
    await deliverOrQueue(ctx, target, prize, vote);
  }
}

/**
 * Table d'alias (config.aliases) : "pseudo de vote" -> "personnage en jeu".
 * Permet aux joueurs de voter avec leur pseudo Discord habituel (Ketil, Kris…)
 * tout en livrant la récompense à leur personnage (Andromaque, Paikan24…).
 * Comparaison insensible à la casse ; sans alias, le pseudo de vote doit être
 * le nom exact du personnage (règle historique).
 */
export function resolveAlias(ctx, name) {
  const aliases = ctx.config.aliases;
  if (!aliases) return name;
  const key = Object.keys(aliases).find((k) => k.toLowerCase() === name.toLowerCase());
  return key ? aliases[key] : name;
}

/**
 * Fenêtre de stabilité : un joueur n'est "livrable" que s'il était déjà
 * en ligne au cycle précédent (~60 s). Évite de livrer pendant l'écran de
 * chargement, où le serveur liste le peer mais où le personnage n'existe
 * pas encore côté client (cause historique de récompenses perdues).
 * ctx._prevOnline est mis à jour uniquement par deliverQueue.
 */
function isStable(ctx, name) {
  return (ctx._prevOnline ?? new Set()).has(name.toLowerCase());
}

async function deliverOrQueue(ctx, name, prize, vote) {
  let online = null; // null = API injoignable (on ne sait pas)
  try {
    online = (await ctx.valheim.isOnline(name)) && isStable(ctx, name);
  } catch (err) {
    ctx.log(`ValheimRestApi injoignable (${err.message}) — récompense de ${name} mise en file`);
  }

  if (online === true) {
    const ok = await safeGive(ctx, name, prize);
    if (ok) {
      await ctx.notify.public('delivered', { playername: name, prize });
      return;
    }
    await ctx.notify.admin('deliveryFailed', { playername: name, detail: 'ValheimRestApi a refusé le give' });
  }

  ctx.store.enqueue({
    playername: name,
    item: prize.reward.item,
    amount: prize.amount,
    prizeText: prizeLabel(prize),
    tierId: prize.tier.id,
    voteDate: vote?.datetime ?? null,
    kind: 'vote',
  });
  await ctx.notify.public('queued', { playername: name, prize });
}

async function safeGive(ctx, name, prize) {
  try {
    return await ctx.valheim.give(name, prize.reward.item, prize.amount,
      `Roue de la Fortune : ${prizeLabel(prize)} !`);
  } catch {
    return false;
  }
}

/** Cycle de livraison différée : livre la file aux joueurs désormais en ligne, purge les expirées. */
export async function deliverQueue(ctx) {
  const maxAgeDays = ctx.config.queue?.maxAgeDays ?? 30;
  for (const expired of ctx.store.expireQueue(maxAgeDays)) {
    await ctx.notify.admin('expired', { entry: expired, maxAgeDays });
  }

  let online;
  try {
    online = await ctx.valheim.onlinePlayers();
  } catch {
    return; // API injoignable : on retentera au prochain cycle
  }

  ctx.store.learnPlayers(online); // apprend qui est joueur du serveur

  // Fenêtre de stabilité : livrable = en ligne maintenant ET au cycle précédent.
  const prev = ctx._prevOnline ?? new Set();
  ctx._prevOnline = new Set(online.map((n) => n.toLowerCase()));
  const stable = online.filter((n) => prev.has(n.toLowerCase()));

  if (ctx.store.queue.length === 0 || stable.length === 0) return;

  for (const entry of ctx.store.takeDeliverable(stable)) {
    let ok = false;
    try {
      ok = await ctx.valheim.give(entry.playername, entry.item, entry.amount,
        entry.kind === 'monthly'
          ? `Podium des votants : ${entry.prizeText} !`
          : `Roue de la Fortune : ${entry.prizeText} !`);
    } catch { /* ok reste false */ }

    if (ok) {
      const pseudoPrize = {
        tier: ctx.rewards.tiers.find((t) => t.id === entry.tierId) ?? ctx.rewards.tiers[0],
        reward: { label: entry.prizeText, item: entry.item },
        amount: 1,
      };
      // prizeText contient déjà quantité + emoji : on l'affiche tel quel
      pseudoPrize.reward.label = entry.prizeText;
      await ctx.notify.public('deliveredLate', { playername: entry.playername, prize: pseudoPrize });
    } else {
      ctx.store.requeue(entry);
      await ctx.notify.admin('deliveryFailed', { playername: entry.playername, detail: 'échec du give différé' });
    }
  }
}

/**
 * Podium mensuel : le jour J (config.monthly.dayOfMonth, défaut 1), récupère le classement
 * du mois PRÉCÉDENT et distribue : gros lot aux rangs 1-3, petit lot aux rangs 4-5.
 * @param {Date} nowDate date courante (injectable pour les tests)
 */
export async function runMonthlyIfDue(ctx, nowDate = new Date()) {
  const cfg = ctx.config.monthly ?? {};
  if (cfg.enabled === false || !ctx.rewards.monthly) return;

  const day = cfg.dayOfMonth ?? 1;
  const hour = cfg.hour ?? 10;
  if (nowDate.getDate() !== day || nowDate.getHours() < hour) return;

  const monthKey = `${nowDate.getFullYear()}-${String(nowDate.getMonth() + 1).padStart(2, '0')}`;
  if (ctx.store.monthlyAlreadyRun(monthKey)) return;

  let players;
  try {
    players = await ctx.ts.playersRanking('lastMonth');
  } catch (err) {
    ctx.log(`players-ranking(lastMonth) en erreur : ${err.message} — retenté au prochain cycle`);
    return;
  }

  ctx.store.markMonthlyRun(monthKey); // marqué AVANT distribution pour ne jamais doubler les lots

  const ranked = [];
  players.slice(0, 5).forEach((p, i) => {
    const rank = i + 1;
    const lot = monthlyPrizeForRank(ctx.rewards, rank);
    if (!lot) return;
    const votename = p.playername ?? p.pseudo ?? p.username ?? p.name;
    if (!votename) return;
    // Podium affiché au pseudo de vote, lot livré au personnage (alias résolu).
    ranked.push({ rank, playername: votename, deliverTo: resolveAlias(ctx, votename),
                  votes: p.votes ?? p.count ?? '?', prizeText: lot.label, lot });
  });

  for (const r of ranked) {
    // livraison via la même file d'attente : chaque item du lot devient une entrée
    for (const it of r.lot.items) {
      ctx.store.enqueue({
        playername: r.deliverTo,
        item: it.item,
        amount: it.amount ?? 1,
        prizeText: `${r.lot.label} (${it.label ?? it.item})`,
        tierId: 'tresrare',
        voteDate: null,
        kind: 'monthly',
      });
    }
  }

  const monthLabel = previousMonthLabel(nowDate);
  await ctx.notify.podium(monthLabel, ranked);
  await deliverQueue(ctx); // tente une livraison immédiate pour ceux qui sont en ligne
}

export function previousMonthLabel(nowDate) {
  const d = new Date(nowDate.getFullYear(), nowDate.getMonth() - 1, 1);
  return d.toLocaleDateString('fr-FR', { month: 'long', year: 'numeric' });
}
