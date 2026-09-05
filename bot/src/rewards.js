/**
 * Tirage de la Roue de la Fortune — table de loot pondérée par rareté (rewards.json).
 */

/**
 * Tire une récompense.
 * @param {object} rewards contenu de rewards.json
 * @param {() => number} [rng] injectable pour les tests (retourne [0,1))
 * @returns {{tier: object, reward: object, amount: number}}
 */
export function drawReward(rewards, rng = Math.random) {
  const tier = pickWeighted(rewards.tiers, (t) => t.weight, rng);
  const reward = tier.rewards[Math.floor(rng() * tier.rewards.length)];
  const amount = drawAmount(reward.amount, rng);
  return { tier, reward, amount };
}

function pickWeighted(items, weightOf, rng) {
  const total = items.reduce((s, i) => s + weightOf(i), 0);
  let roll = rng() * total;
  for (const item of items) {
    roll -= weightOf(item);
    if (roll < 0) return item;
  }
  return items[items.length - 1];
}

/** amount peut être un nombre fixe ou [min, max] inclusif. */
export function drawAmount(amount, rng = Math.random) {
  if (Array.isArray(amount)) {
    const [min, max] = amount;
    return min + Math.floor(rng() * (max - min + 1));
  }
  return amount ?? 1;
}

/** Texte affichable d'une récompense tirée, ex. "💰 20 × Piastres". */
export function prizeLabel(prize) {
  const { reward, amount } = prize;
  const emoji = reward.emoji ? `${reward.emoji} ` : '';
  const qty = amount > 1 ? `${amount} × ` : '';
  return `${emoji}${qty}${reward.label}`;
}

/**
 * Récompenses mensuelles par rang (1-indexé).
 * rewards.monthly = { "top3": {...}, "rank4to5": {...} } — chacun : { label, items: [{item, amount, label, emoji}] }
 * @returns {object|null} le lot pour ce rang, ou null si aucun
 */
export function monthlyPrizeForRank(rewards, rank) {
  const m = rewards.monthly;
  if (!m) return null;
  if (rank >= 1 && rank <= 3) return m.top3 ?? null;
  if (rank >= 4 && rank <= 5) return m.rank4to5 ?? null;
  return null;
}
