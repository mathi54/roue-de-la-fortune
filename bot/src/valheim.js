/**
 * Client vers ValheimRestApi (mod BepInEx de mathi, HttpListener + MainThreadDispatcher).
 * Endpoints utilisés :
 *   GET  /players -> { success, count, players: [...] } (voir extractPlayerName pour les formats)
 *   POST /give    -> body { playername, item, amount, message } -> { success, mode, error? }
 * Auth du mod : header "X-Auth-Token" (config BepInEx Security.AuthToken).
 * config.json : valheim.baseUrl (ex. http://127.0.0.1:52858), valheim.apiToken.
 */
export class ValheimClient {
  constructor({ baseUrl, apiToken = '' }, fetchImpl = fetch) {
    this.baseUrl = baseUrl.replace(/\/$/, '');
    this.apiToken = apiToken;
    this.fetch = fetchImpl;
  }

  #headers() {
    const h = { 'Content-Type': 'application/json' };
    if (this.apiToken) h['X-Auth-Token'] = this.apiToken;
    return h;
  }

  /** Liste des pseudos des joueurs connectés. Lance une erreur si l'API est injoignable. */
  async onlinePlayers() {
    const res = await this.fetch(`${this.baseUrl}/players`, {
      headers: this.#headers(),
      signal: AbortSignal.timeout(10000),
    });
    if (!res.ok) throw new Error(`ValheimRestApi /players HTTP ${res.status}`);
    const data = await res.json();
    const list = Array.isArray(data) ? data : data.players ?? [];
    return list.map(extractPlayerName).filter(Boolean);
  }

  async isOnline(playername) {
    const players = await this.onlinePlayers();
    const target = playername.toLowerCase();
    return players.some((p) => p.toLowerCase() === target);
  }

  /**
   * Donne un objet à un joueur connecté (insertion inventaire via RPC EventController).
   * @returns {Promise<boolean>} true si la livraison a réussi
   */
  async give(playername, item, amount, message = '') {
    const res = await this.fetch(`${this.baseUrl}/give`, {
      method: 'POST',
      headers: this.#headers(),
      body: JSON.stringify({ playername, item, amount, message }),
      signal: AbortSignal.timeout(10000),
    });
    if (!res.ok) return false;
    const data = await res.json().catch(() => null);
    return data?.success === true;
  }

  /** Livre toutes les lignes d'un lot (prize simple ou lot mensuel multi-items). */
  async giveAll(playername, items) {
    for (const it of items) {
      const ok = await this.give(playername, it.item, it.amount);
      if (!ok) return false;
    }
    return true;
  }
}

/**
 * Extrait le pseudo d'une entrée de /players, quel que soit son format :
 *  - objet { name: "Grudu", ... }                 (format idéal)
 *  - chaîne "Grudu"                                (format simple)
 *  - chaîne JSON "{\"name\":\"Grudu\",...}"        (format réel du JsonBuilder du mod :
 *    son `case string` ré-échappe les objets déjà sérialisés — on décode donc ici)
 */
export function extractPlayerName(p) {
  if (p && typeof p === 'object') return p.name ?? null;
  if (typeof p !== 'string') return null;
  const s = p.trim();
  if (s.startsWith('{')) {
    try { return JSON.parse(s).name ?? null; } catch { return null; }
  }
  return s || null;
}
