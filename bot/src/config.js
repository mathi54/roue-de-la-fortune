import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');

function loadJson(name) {
  return JSON.parse(readFileSync(resolve(root, name), 'utf8'));
}

export function loadConfig() {
  const config = loadJson('config.json');
  const rewards = loadJson('rewards.json');
  validate(config, rewards);
  return { config, rewards, root };
}

function validate(config, rewards) {
  const miss = [];
  if (!config.discord?.token) miss.push('discord.token');
  if (!config.discord?.channels?.public) miss.push('discord.channels.public');
  if (!config.discord?.channels?.admin) miss.push('discord.channels.admin');
  if (!config.topServeurs?.serverToken) miss.push('topServeurs.serverToken');
  if (!config.valheim?.baseUrl) miss.push('valheim.baseUrl');
  if (miss.length) {
    throw new Error(`config.json incomplet, champs manquants : ${miss.join(', ')}`);
  }
  if (!Array.isArray(rewards.tiers) || rewards.tiers.length === 0) {
    throw new Error('rewards.json : il faut au moins un tier dans "tiers"');
  }
  for (const t of rewards.tiers) {
    if (!t.id || !(t.weight > 0) || !Array.isArray(t.rewards) || t.rewards.length === 0) {
      throw new Error(`rewards.json : tier invalide (${t.id || '?'}) — id, weight > 0 et rewards[] requis`);
    }
  }
}
