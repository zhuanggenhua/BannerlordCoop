import { isAbsolute, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { closeSync, existsSync, mkdirSync, openSync, readFileSync, renameSync, statSync, writeFileSync, writeSync } from 'node:fs';
import { StringDecoder } from 'node:string_decoder';

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

function schemaErrors(value, schema, path = '$') {
    const errors = [];
    const actualType = Array.isArray(value) ? 'array' : value === null ? 'null' : typeof value;
    if (schema.type === 'integer' ? !Number.isInteger(value) : schema.type && actualType !== schema.type) {
        return [`${path} must be ${schema.type}.`];
    }
    if (typeof value === 'string') {
        if (schema.minLength !== undefined && value.length < schema.minLength) errors.push(`${path} is too short.`);
        if (schema.pattern && !new RegExp(schema.pattern).test(value)) errors.push(`${path} does not match its schema pattern.`);
    }
    if (Array.isArray(value)) {
        if (schema.minItems !== undefined && value.length < schema.minItems) errors.push(`${path} has too few items.`);
        value.forEach((item, index) => errors.push(...schemaErrors(item, schema.items ?? {}, `${path}[${index}]`)));
    } else if (actualType === 'object') {
        for (const field of schema.required ?? []) {
            if (!Object.hasOwn(value, field)) errors.push(`${path}.${field} is required.`);
        }
        for (const [field, childSchema] of Object.entries(schema.properties ?? {})) {
            if (Object.hasOwn(value, field)) errors.push(...schemaErrors(value[field], childSchema, `${path}.${field}`));
        }
    }
    return errors;
}

function writeReceipt(path, receipt) {
    const temporary = `${path}.${process.pid}.tmp`;
    writeFileSync(temporary, `${JSON.stringify(receipt, null, 2)}\n`, { encoding: 'utf8', mode: 0o600 });
    renameSync(temporary, path);
}

async function killOwnedTree(child) {
    if (!child.pid || child.exitCode !== null || child.signalCode !== null) return;
    if (process.platform === 'win32') {
        const executable = join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'taskkill.exe');
        await new Promise((resolve) => {
            const killer = spawn(executable, ['/PID', String(child.pid), '/T', '/F'], {
                shell: false, windowsHide: true, stdio: 'ignore', timeout: 10_000,
            });
            killer.once('error', resolve);
            killer.once('close', resolve);
        });
        if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
    } else {
        try {
            process.kill(-child.pid, 'SIGKILL');
        } catch (error) {
            if (error.code !== 'ESRCH') throw error;
        }
    }
}

// A completed receipt is a schema-valid worker handoff. Review, integration and
// gameplay acceptance remain coordinator gates, even when the CLI exits cleanly.
export async function runCodexSession(options) {
    const args = buildExecArgs(options);
    if (!Number.isInteger(options.timeoutMs ?? 1_800_000) || (options.timeoutMs ?? 1_800_000) <= 0) {
        throw new TypeError('timeoutMs must be a positive integer.');
    }
    if ((options.prompt === undefined) === (options.promptFile === undefined)) {
        throw new TypeError('Provide exactly one prompt or promptFile.');
    }
    if (options.promptFile !== undefined && (typeof options.promptFile !== 'string' || !isAbsolute(options.promptFile))) {
        throw new TypeError('promptFile must be an absolute path.');
    }
    const prompt = options.promptFile === undefined ? options.prompt : readFileSync(options.promptFile, 'utf8');
    if (typeof prompt !== 'string' || !prompt.trim()) throw new TypeError('The prompt must be a nonempty string.');
    if (!statSync(options.cwd).isDirectory()) throw new TypeError('cwd must be an existing directory.');
    const executable = options.executable ?? 'codex';
    const executableArgs = options.executableArgs ?? [];
    if (typeof executable !== 'string' || !executable || !Array.isArray(executableArgs) || executableArgs.some(arg => typeof arg !== 'string')) {
        throw new TypeError('The executable and its arguments must be strings.');
    }
    const schema = JSON.parse(readFileSync(handoffSchemaPath, 'utf8'));
    mkdirSync(options.outputDir, { recursive: true });
    const paths = {
        receipt: join(options.outputDir, 'receipt.json'), events: join(options.outputDir, 'events.jsonl'),
        stderr: join(options.outputDir, 'stderr.log'), final: join(options.outputDir, 'final.json'),
    };
    // Never overwrite a previous attempt. A retry must use its own output directory.
    closeSync(openSync(paths.receipt, 'wx', 0o600));
    const receipt = {
        schemaVersion: 1, state: 'starting', cwd: options.cwd, model: options.model,
        verifiedParentModel: options.verifiedParentModel, resumeSessionId: options.resumeSessionId ?? null,
        executable, args: [...executableArgs, ...args], pid: null, threadId: null,
        startedAt: new Date().toISOString(), finishedAt: null, exitCode: null, signal: null,
        paths, errors: [], handoff: null, requiresCoordinatorReview: true,
    };
    writeReceipt(paths.receipt, receipt);
    const protocol = { events: [], threadId: null, completed: false, failed: false, errors: [] };
    const decoder = new StringDecoder('utf8');
    let eventFile;
    let stderrFile;
    let pending = '';
    let lineNumber = 0;
    let eventBytes = 0;
    let closed = false;
    let termination;
    let killPromise = Promise.resolve();
    let timer;
    let child;
    const stop = (state, message) => {
        if (closed || termination) return;
        termination = { state, message };
        receipt.state = 'stopping';
        receipt.errors.push(message);
        writeReceipt(paths.receipt, receipt);
        killPromise = killOwnedTree(child).catch(error => receipt.errors.push(`Process cleanup failed: ${error.message}`));
    };
    const consume = (text) => {
        pending += text;
        const lines = pending.split('\n');
        pending = lines.pop();
        for (const line of lines) {
            const previousId = protocol.threadId;
            parseLine(protocol, line, ++lineNumber);
            protocol.events.length = 0;
            if (protocol.threadId && protocol.threadId !== previousId) {
                receipt.threadId = protocol.threadId;
                writeReceipt(paths.receipt, receipt);
            }
        }
    };
    const abort = () => stop('aborted', `Session aborted: ${String(options.signal?.reason ?? 'caller cancellation')}`);
    try {
        eventFile = openSync(paths.events, 'wx', 0o600);
        stderrFile = openSync(paths.stderr, 'wx', 0o600);
        if (existsSync(paths.final)) throw new Error('The final output file already exists.');
        if (options.signal?.aborted) {
            termination = { state: 'aborted', message: 'Session was cancelled before spawn.' };
            receipt.errors.push(termination.message);
        } else {
            child = spawn(executable, [...executableArgs, ...args], {
                cwd: options.cwd, shell: false, windowsHide: true,
                detached: process.platform !== 'win32', stdio: ['pipe', 'pipe', 'pipe'],
            });
            receipt.pid = child.pid ?? null;
            receipt.state = 'running';
            writeReceipt(paths.receipt, receipt);
            const completion = new Promise(resolve => {
                child.once('error', error => receipt.errors.push(`Spawn failed: ${error.message}`));
                child.once('close', (code, signal) => {
                    closed = true;
                    receipt.exitCode = code;
                    receipt.signal = signal;
                    resolve();
                });
            });
            child.stdout.on('data', chunk => {
                try {
                    writeSync(eventFile, chunk);
                    eventBytes += chunk.length;
                    if (eventBytes > 64 * 1024 * 1024) stop('failed', 'Event output exceeded 64 MiB.');
                    else consume(decoder.write(chunk));
                } catch (error) {
                    stop('failed', `Event logging failed: ${error.message}`);
                }
            });
            child.stderr.on('data', chunk => {
                try { writeSync(stderrFile, chunk); }
                catch (error) { stop('failed', `Stderr logging failed: ${error.message}`); }
            });
            child.stdin.on('error', error => { if (!closed) receipt.errors.push(`Stdin failed: ${error.message}`); });
            options.signal?.addEventListener('abort', abort, { once: true });
            timer = setTimeout(() => stop('timeout', 'Session exceeded timeoutMs.'), options.timeoutMs ?? 1_800_000);
            child.stdin.end(prompt);
            await completion;
            await killPromise;
            consume(decoder.end());
            if (pending.trim()) parseLine(protocol, pending, ++lineNumber);
        }
    } catch (error) {
        receipt.errors.push(error.message);
        if (child && !closed) {
            stop('failed', 'Session setup or transport failed.');
            await killPromise;
        }
    } finally {
        clearTimeout(timer);
        options.signal?.removeEventListener('abort', abort);
        if (eventFile !== undefined) closeSync(eventFile);
        if (stderrFile !== undefined) closeSync(stderrFile);
    }
    receipt.threadId = protocol.threadId;
    receipt.errors.push(...protocol.errors);
    if (!termination) {
        if (receipt.exitCode !== 0) receipt.errors.push(`CLI exit code was ${receipt.exitCode}.`);
        if (!protocol.threadId) receipt.errors.push('No thread.started identity was received.');
        if (options.resumeSessionId && protocol.threadId !== options.resumeSessionId) receipt.errors.push('Resumed thread identity did not match the requested session.');
        if (!protocol.completed) receipt.errors.push('No turn.completed event was received.');
        if (protocol.failed) receipt.errors.push('The turn reported failure.');
        try {
            if (statSync(paths.final).size > 16 * 1024 * 1024) throw new Error('Final JSON exceeded 16 MiB.');
            const handoff = JSON.parse(readFileSync(paths.final, 'utf8'));
            const errors = schemaErrors(handoff, schema);
            receipt.errors.push(...errors);
            if (errors.length === 0) receipt.handoff = handoff;
        } catch (error) {
            receipt.errors.push(`Invalid final handoff JSON: ${error.message}`);
        }
    }
    receipt.state = termination?.state ?? (receipt.errors.length === 0 ? 'completed' : 'failed');
    receipt.finishedAt = new Date().toISOString();
    receipt.retryable = receipt.state !== 'completed';
    receipt.turnCompleted = protocol.completed;
    writeReceipt(paths.receipt, receipt);
    return receipt;
}
