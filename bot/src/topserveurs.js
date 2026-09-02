/**
 * Client API Top-Serveurs (https://api.top-serveurs.net)
 * Doc : sections "Vote" et "Claim" de la documentation officielle.
 */
const API = 'https://api.top-serveurs.net/v1';

export class TopServeursClient {
  /**
   * @param {string} serverToken La "Token de Votes" du panel Top-Serveurs.
   * @param {typeof fetch} [fetchImpl] Injectable pour les tests.
   */
  constructor(serverToken, fetchImpl = fetch) {
    this.token = serverToken;
    this.fetch = fetchImpl;
  }

  async #get(path, params = {}) {
    const url = new URL(API + path);
    for (const [k, v] of Object.entries(params)) url.searchParams.set(k, v);
    const res = await this.fetch(url, { signal: AbortSignal.timeout(15000) });
    let data = null;
    try { data = await res.json(); } catch { /* réponse non-JSON */ }
    return { status: res.status, data };
  }

  /** Liste des votes de l'heure courante. Retourne un tableau (possiblement vide). */
  async lastVotes() {
    const { status, data } = await this.#get('/votes/last', { server_token: this.token });
    if (status === 200 && data?.success && Array.isArray(data.votes)) return data.votes;
    if (status === 404) return []; // aucun vote / pas encore de votes cette heure
    throw new Error(`Top-Serveurs /votes/last HTTP ${status}: ${data?.message || 'réponse inattendue'}`);
  }

  /**
   * Réclame le vote d'un joueur (fenêtre : 2 dernières heures).
   * @returns {Promise<0|1|2>} 0 = introuvable, 1 = réclamé avec succès, 2 = déjà réclamé
   */
  async claimUsername(playername) {
    const { status, data } = await this.#get('/votes/claim-username', {
      server_token: this.token,
      playername,
    });
    if (data && typeof data.claimed === 'number') return data.claimed;
    if (status === 404) return 0;
    throw new Error(`Top-Serveurs /votes/claim-username HTTP ${status}: ${data?.message || 'réponse inattendue'}`);
  }

  /**
   * Classement des meilleurs voteurs.
   * @param {'current'|'lastMonth'} type
   * @returns {Promise<Array>} tableau players (mis en cache 15 min côté Top-Serveurs)
   */
  async playersRanking(type = 'current') {
    const { status, data } = await this.#get(`/servers/${this.token}/players-ranking`, { type });
    if (status === 200 && data?.success && Array.isArray(data.players)) return data.players;
    throw new Error(`Top-Serveurs players-ranking HTTP ${status}: ${data?.message || 'réponse inattendue'}`);
  }
}

/** Extrait le pseudo d'un objet vote de /votes/last, quel que soit le nom du champ. */
export function voteName(vote) {
  const name = vote?.playername ?? vote?.pseudo ?? vote?.username ?? vote?.name ?? null;
  return typeof name === 'string' && name.trim() ? name.trim() : null;
}

/** Clé de déduplication locale d'un vote (évite de re-claim en boucle pendant l'heure). */
export function voteKey(vote) {
  const name = voteName(vote) ?? 'inconnu';
  const when = vote?.datetime ?? vote?.date ?? vote?.created_at ?? '';
  return `${name.toLowerCase()}|${when}`;
}
