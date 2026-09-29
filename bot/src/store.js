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
    this.data = { seen: {}, queue: [], monthlyRun: null, pinnedMessageId: null, knownPlayers: {}, history: [], milestones: {} };
    if (existsSync(path)) {
      try {
        this.data = { ...this.data, ...JSON.parse(readFileSync(path, 'utf8')) };
        if (!Array.isArray(this.data.history)) this.data.history = [];
        if (!this.data.milestones || typeof this.data.milestones !== 'object') this.data.milestones = {};
        // v1.1.1 : id unique par entrée de file (rétro-remplissage des entrées existantes)
        for (const e of this.data.queue) {
          if (!e.id) e.id = this.newQueueId();
        }
      } catch {
        // fichier corrompu : on repart proprement, l'anti-doublon reste garanti par claim-username
      }
    }
  }

  newQueueId() {
    return `q-${this.now()}-${Math.random().toString(36).slice(2, 8)}`;
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
    this.data.queue.push({ id: this.newQueueId(), ...entry, queuedAt: this.now() });
    this.save();
  }

  /**
   * v1.1.1 — Lecture NON destructive des entrées livrables (le retrait ne se fait
   * qu'après un give confirmé, via removeEntry). Compare le pseudo brut et le
   * pseudo résolu par alias. Retourne [{ entry, deliverTo }].
   */
  peekDeliverable(onlineNames, resolveAliasFn = (n) => n) {
    // clé insensible à la casse -> casse réelle du joueur en ligne (celle du serveur)
    const online = new Map(onlineNames.map((n) => [n.toLowerCase().trim(), n]));
    const out = [];
    for (const e of this.data.queue) {
      const raw = (e.playername || '').toLowerCase().trim();
      const resolved = (resolveAliasFn(e.playername) || '').toLowerCase().trim();
      const match = online.get(raw) ?? online.get(resolved);
      if (match) out.push({ entry: e, deliverTo: match });
    }
    return out;
  }

  /** Retire une entrée par id — appelé UNIQUEMENT après un give réussi. */
  removeEntry(id) {
    const before = this.data.queue.length;
    this.data.queue = this.data.queue.filter((e) => e.id !== id);
    if (this.data.queue.length !== before) this.save();
  }

  /** Incrémente le compteur d'échecs d'une entrée et retourne sa valeur. */
  bumpAttempts(id) {
    const e = this.data.queue.find((x) => x.id === id);
    if (!e) return 0;
    e.attempts = (e.attempts ?? 0) + 1;
    this.save();
    return e.attempts;
  }

  /**
   * Entrées de la file pour un ensemble de joueurs en ligne.
   * Compare le pseudo brut et le pseudo résolu par alias pour ne jamais bloquer une récompense en file.
   */
  takeDeliverable(onlineNames, resolveAliasFn = (n) => n) {
    const online = new Set(onlineNames.map((n) => n.toLowerCase().trim()));
    const deliverable = [];
    const remaining = [];
    for (const e of this.data.queue) {
      const raw = (e.playername || '').toLowerCase().trim();
      const resolved = (resolveAliasFn(e.playername) || '').toLowerCase().trim();
      if (online.has(raw) || online.has(resolved)) {
        const effectiveName = online.has(resolved) ? resolveAliasFn(e.playername) : e.playername;
        deliverable.push({ ...e, playername: effectiveName });
      } else {
        remaining.push(e);
      }
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

  // --- traçabilité des tirages (audit trail - Règle Studio n°3) ---
  addHistory(entry, maxEntries = 50) {
    if (!Array.isArray(this.data.history)) this.data.history = [];
    const id = `${this.now()}-${Math.random().toString(36).slice(2, 7)}`;
    this.data.history.push({
      id,
      timestamp: this.now(),
      date: new Date(this.now()).toISOString(),
      ...entry,
    });
    if (this.data.history.length > maxEntries) {
      this.data.history = this.data.history.slice(-maxEntries);
    }
    this.save();
    return id;
  }

  getHistory(limit = 50) {
    const arr = this.data.history || [];
    return arr.slice(-Math.max(1, limit)).reverse();
  }

  get history() {
    return this.data.history || [];
  }

  // --- paliers collectifs communautaires (milestones) ---
  recordMonthlyVoter(monthKey, playerName) {
    if (!this.data.milestones) this.data.milestones = {};
    if (!this.data.milestones[monthKey]) {
      this.data.milestones[monthKey] = { voters: [], reached: [] };
    }
    const m = this.data.milestones[monthKey];
    if (!m.voters.some((v) => v.toLowerCase() === playerName.toLowerCase())) {
      m.voters.push(playerName);
      this.save();
    }
  }

  getMonthlyVoters(monthKey) {
    return this.data.milestones?.[monthKey]?.voters || [];
  }

  isMilestoneReached(monthKey, threshold) {
    return this.data.milestones?.[monthKey]?.reached?.includes(threshold) || false;
  }

  markMilestoneReached(monthKey, threshold) {
    if (!this.data.milestones) this.data.milestones = {};
    if (!this.data.milestones[monthKey]) {
      this.data.milestones[monthKey] = { voters: [], reached: [] };
    }
    const m = this.data.milestones[monthKey];
    if (!m.reached.includes(threshold)) {
      m.reached.push(threshold);
      this.save();
    }
  }
}
