import { readFileSync, writeFileSync, renameSync, existsSync } from 'node:fs';

/**
 * Persistance légère sur fichier JSON (état du bot) :
 *  - seen : votes déjà traités (clé -> timestamp), purgé après 3 h
 *  - queue : récompenses en attente de livraison (joueur hors ligne)
 *  - monthlyRun : dernier mois pour lequel le podium a été distribué ("2026-08")
 *  - pinnedMessageId : id du message épinglé "Tableau des gains"
 * Écriture atomique (tmp + rename) — suffisant pour ~15 joueurs.
 */
export class Store {
  constructor(path, now = () => Date.now()) {
    this.path = path;
    this.now = now;
    this.data = { seen: {}, queue: [], monthlyRun: null, pinnedMessageId: null, knownPlayers: {} };
    if (existsSync(path)) {
      try {
        this.data = { ...this.data, ...JSON.parse(readFileSync(path, 'utf8')) };
      } catch {
        // fichier corrompu : on repart proprement, l'anti-doublon reste garanti par claim-username
      }
    }
  }

  save() {
    const tmp = this.path + '.tmp';
    writeFileSync(tmp, JSON.stringify(this.data, null, 2));
    renameSync(tmp, this.path);
  }

  // --- votes vus (dédup locale, en complément du claim Top-Serveurs) ---
  hasSeen(key) { return key in this.data.seen; }

  markSeen(key) {
    this.data.seen[key] = this.now();
    this.pruneSeen();
    this.save();
  }

  pruneSeen(maxAgeMs = 3 * 3600 * 1000) {
    const cutoff = this.now() - maxAgeMs;
    for (const [k, ts] of Object.entries(this.data.seen)) {
      if (ts < cutoff) delete this.data.seen[k];
    }
  }

  // --- file d'attente des récompenses ---
  enqueue(entry) {
    this.data.queue.push({ ...entry, queuedAt: this.now() });
    this.save();
  }

  /** Entrées de la file pour un ensemble de joueurs en ligne (comparaison insensible à la casse). */
  takeDeliverable(onlineNames) {
    const online = new Set(onlineNames.map((n) => n.toLowerCase()));
    const deliverable = [];
    const remaining = [];
    for (const e of this.data.queue) {
      (online.has(e.playername.toLowerCase()) ? deliverable : remaining).push(e);
    }
    this.data.queue = remaining;
    if (deliverable.length) this.save();
    return deliverable;
  }

  /** Retire et retourne les entrées plus vieilles que maxAgeDays (jamais livrées). */
  expireQueue(maxAgeDays) {
    const cutoff = this.now() - maxAgeDays * 24 * 3600 * 1000;
    const expired = this.data.queue.filter((e) => e.queuedAt < cutoff);
    if (expired.length) {
      this.data.queue = this.data.queue.filter((e) => e.queuedAt >= cutoff);
      this.save();
    }
    return expired;
  }

  requeue(entry) {
    // remet en file SANS rafraîchir queuedAt (l'expiration reste basée sur la date initiale)
    this.data.queue.push(entry);
    this.save();
  }

  get queue() { return this.data.queue; }

  // --- joueurs connus du serveur (appris via /players à chaque cycle) ---
  /** Mémorise les joueurs vus en ligne (clé insensible à la casse, garde la casse d'origine). */
  learnPlayers(names) {
    let changed = false;
    for (const n of names) {
      const key = n.toLowerCase();
      if (!(key in this.data.knownPlayers)) changed = true;
      this.data.knownPlayers[key] = { name: n, lastSeen: this.now() };
    }
    if (changed) this.save();
  }

  isKnownPlayer(name) {
    return name.toLowerCase() in this.data.knownPlayers;
  }

  get knownPlayerCount() {
    return Object.keys(this.data.knownPlayers).length;
  }

  // --- podium mensuel ---
  monthlyAlreadyRun(monthKey) { return this.data.monthlyRun === monthKey; }
  markMonthlyRun(monthKey) { this.data.monthlyRun = monthKey; this.save(); }

  // --- message épinglé ---
  get pinnedMessageId() { return this.data.pinnedMessageId; }
  set pinnedMessageId(id) { this.data.pinnedMessageId = id; this.save(); }
}
