const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const { BRIDGE_LOG_FILE_NAME } = require('./constants');

import type { BridgeLogger, BridgeOptions, LogLevel } from './types';

const MAX_LOG_BYTES = 512 * 1024;

function writeLogLine(logFile: string, level: LogLevel, message: string, error?: unknown) {
    const method = level === 'error' ? 'error' : level === 'warn' ? 'warn' : 'info';
    const tag = `[wand-remote-bridge] ${message}`;

    // Logging runs inside Wand's own process; a failure here must never take the app down.
    try {
        console[method](tag, error || '');
    } catch {}

    try {
        rotateLogFile(logFile);
        const detail = error
            ? ` :: ${error && typeof error === 'object' && 'stack' in error ? String(error.stack) : String(error)}`
            : '';
        fs.appendFileSync(
            logFile,
            `[${new Date().toISOString()}] [${level}] ${message}${detail}\n`,
        );
    } catch {}
}

// One previous generation is kept; failures fall through to a plain append.
function rotateLogFile(logFile: string) {
    try {
        const stats = fs.statSync(logFile);
        if (!stats.isFile() || stats.size <= MAX_LOG_BYTES) {
            return;
        }

        const previous = `${logFile}.prev`;
        try {
            fs.unlinkSync(previous);
        } catch {}
        fs.renameSync(logFile, previous);
    } catch {}
}

function createBridgeLogger(options: BridgeOptions = {}): BridgeLogger {
    const logFile = options.logFile || path.join(os.tmpdir(), BRIDGE_LOG_FILE_NAME);
    const log = ((level: LogLevel, message: string, error?: unknown) =>
        writeLogLine(logFile, level, message, error)) as BridgeLogger;
    log.file = logFile;
    return log;
}

function writeInstallLog(level: LogLevel, message: string, error?: unknown) {
    writeLogLine(path.join(os.tmpdir(), BRIDGE_LOG_FILE_NAME), level, message, error);
}

module.exports = {
    createBridgeLogger,
    writeInstallLog,
};
