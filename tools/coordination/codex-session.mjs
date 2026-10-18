import { isAbsolute, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const handoffSchemaPath = fileURLToPath(new URL('./handoff.schema.json', import.meta.url));

function validateOptions(options) {
    if (!options || typeof options.verifiedParentModel !== 'string' || !options.verifiedParentModel.trim()) {
        throw new TypeError('An explicitly verified parent model is required; config defaults are not evidence.');
    }
    if (options.model !== options.verifiedParentModel || options.model !== options.model.trim()) {
        throw new TypeError('model must exactly match verifiedParentModel.');
    }
    for (const field of ['cwd', 'outputDir']) {
        if (typeof options[field] !== 'string' || !isAbsolute(options[field])) {
            throw new TypeError(`${field} must be an absolute path.`);
        }
    }
    if (options.resumeSessionId !== undefined && !/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(options.resumeSessionId)) {
        throw new TypeError('resumeSessionId must be an exact session UUID; --last is prohibited.');
    }
}

export function buildExecArgs(options) {
    validateOptions(options);
    const args = [
        'exec', '--json', '--cd', options.cwd, '--model', options.model,
        '--output-last-message', join(options.outputDir, 'final.json'),
        '--output-schema', handoffSchemaPath,
    ];
    if (options.resumeSessionId) args.push('resume', options.resumeSessionId);
    return [...args, '-'];
}

function recordEvent(result, event, line) {
    if (!event || typeof event !== 'object' || Array.isArray(event) || typeof event.type !== 'string') {
        result.errors.push(`Invalid event at line ${line}.`);
        return;
    }
    result.events.push(event);
    if (event.type === 'thread.started') {
        if (typeof event.thread_id !== 'string' || !event.thread_id) {
            result.errors.push(`Missing thread_id at line ${line}.`);
        } else if (result.threadId && result.threadId !== event.thread_id) {
            result.errors.push(`Conflicting thread IDs at line ${line}.`);
        } else {
            result.threadId = event.thread_id;
        }
    }
    if (event.type === 'turn.completed') result.completed = true;
    if (event.type === 'turn.failed' || event.type === 'error') {
        result.failed = true;
        result.errors.push(`${event.type}: ${event.error?.message ?? event.message ?? 'unspecified error'}`);
    }
}

function parseLine(result, text, line) {
    if (!text.trim()) return;
    try {
        recordEvent(result, JSON.parse(text), line);
    } catch {
        result.errors.push(`Invalid JSON at line ${line}.`);
    }
}

export function parseCodexEvents(text) {
    const result = { events: [], threadId: null, completed: false, failed: false, errors: [] };
    String(text).split(/\r?\n/).forEach((line, index) => parseLine(result, line, index + 1));
    return result;
}
