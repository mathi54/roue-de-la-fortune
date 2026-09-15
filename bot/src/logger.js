import { appendFileSync, mkdirSync, existsSync } from 'node:fs';
import { dirname } from 'node:path';

/**
 * Gestionnaire de journalisation (diagnostic embarqué - Règle Studio n°1) :
 * - Écrit dans la console standard
 * - Écrit dans un fichier persistant local (ex: logs/roue.log)
 * - Maintient un buffer glissant des N derniers logs en mémoire (pour consultation HTTP GET /logs)
 */
export class Logger {
  constructor(logFilePath = null, maxBufferSize = 100) {
    this.logFilePath = logFilePath;
    this.maxBufferSize = maxBufferSize;
    this.buffer = [];

    if (this.logFilePath) {
      const dir = dirname(this.logFilePath);
      if (!existsSync(dir)) {
        try {
          mkdirSync(dir, { recursive: true });
        } catch {
          // ignore si déjà créé
        }
      }
    }
  }

  log(msg, level = 'INFO') {
    const timestamp = new Date().toISOString();
    const line = `[${timestamp}] [${level}] ${msg}`;

    // 1. Sortie console
    console.log(line);

    // 2. Buffer mémoire glissant
    this.buffer.push(line);
    if (this.buffer.length > this.maxBufferSize) {
      this.buffer.shift();
    }

    // 3. Fichier persistant
    if (this.logFilePath) {
      try {
        appendFileSync(this.logFilePath, line + '\n', 'utf8');
      } catch (err) {
        console.error(`Impossible d'écrire dans le fichier de log : ${err.message}`);
      }
    }
  }

  getLogs(limit = 100) {
    const lim = Math.max(1, Math.min(limit, this.maxBufferSize));
    return this.buffer.slice(-lim);
  }

  clearBuffer() {
    this.buffer = [];
  }
}
