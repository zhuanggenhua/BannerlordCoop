import { isAbsolute, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFile, execFileSync, spawn } from 'node:child_process';
import { closeSync, existsSync, mkdirSync, openSync, readFileSync, realpathSync, renameSync, statSync, writeFileSync, writeSync } from 'node:fs';
import { StringDecoder } from 'node:string_decoder';
import { setTimeout as delay } from 'node:timers/promises';

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
    if (options.writeTask !== undefined && typeof options.writeTask !== 'boolean') {
        throw new TypeError('writeTask must be an explicit boolean.');
    }
    if (options.writeTask) {
        if (typeof options.gitCommonDir !== 'string' || !isAbsolute(options.gitCommonDir)) {
            throw new TypeError('A writeTask requires an explicit absolute gitCommonDir from the coordinator.');
        }
        const actual = execFileSync('git', ['rev-parse', '--path-format=absolute', '--git-common-dir'], {
            cwd: options.cwd, encoding: 'utf8', windowsHide: true, shell: false, timeout: 10_000,
        }).trim();
        const canonical = path => process.platform === 'win32' ? realpathSync(path).toLowerCase() : realpathSync(path);
        if (canonical(actual) !== canonical(options.gitCommonDir)) {
            throw new TypeError('gitCommonDir must match the assigned cwd Git common directory.');
        }
    } else if (options.gitCommonDir !== undefined) {
        throw new TypeError('gitCommonDir is only permitted for an explicit writeTask.');
    }
}

export function buildExecArgs(options) {
    validateOptions(options);
    const args = [
        '--ask-for-approval', 'never', 'exec', '--json',
        '--sandbox', options.writeTask ? 'workspace-write' : 'read-only',
        '--cd', options.cwd, '--model', options.model,
        '--output-last-message', join(options.outputDir, 'final.json'),
        '--output-schema', handoffSchemaPath,
    ];
    if (options.writeTask) args.push('--add-dir', options.gitCommonDir);
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

async function cleanupDescendants(rootPid, earliest, rootExitedAt) {
    if (process.platform !== 'win32') {
        try {
            process.kill(-rootPid, 'SIGKILL');
            return { complete: true, method: 'owned-process-group', identities: [] };
        } catch (error) {
            if (error.code === 'ESRCH') return { complete: true, method: 'owned-process-group', identities: [] };
            throw error;
        }
    }
    // An exited PID is never killed. A direct child must have been created while
    // our root was alive; every kill rechecks PID, parent and creation time.
    const script = `
$ErrorActionPreference = 'Stop'
$identities = [System.Collections.Generic.List[object]]::new()
$seen = [System.Collections.Generic.HashSet[string]]::new()
$errors = [System.Collections.Generic.List[string]]::new()
function CreationMs($value) { ([DateTimeOffset]$value.CreationDate.ToUniversalTime()).ToUnixTimeMilliseconds() }
function Collect([int]$ownerPid, [long]$earliestMs, [long]$latestMs) {
    if ($identities.Count -ge 128) { throw 'Descendant identity limit exceeded.' }
    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $ownerPid" -Property ProcessId,ParentProcessId,CreationDate)
    foreach ($candidate in $children) {
        $created = CreationMs $candidate
        if ($created -lt $earliestMs -or $created -gt $latestMs) { continue }
        $key = "$($candidate.ProcessId):$created"
        if (!$seen.Add($key)) { continue }
        $entry = [pscustomobject]@{ pid = [int]$candidate.ProcessId; parentPid = $ownerPid; createdAtMs = $created; status = 'observed' }
        $identities.Add($entry)
        $current = @(Get-CimInstance Win32_Process -Filter "ProcessId = $($entry.pid)" -Property ProcessId,ParentProcessId,CreationDate)
        if ($current.Count -eq 1 -and (CreationMs $current[0]) -eq $created -and [int]$current[0].ParentProcessId -eq $ownerPid) {
            Collect $entry.pid $created ([long]::MaxValue)
        } else {
            $errors.Add("Descendant $($entry.pid) changed or exited before its tree could be verified.")
        }
    }
}
Collect ${rootPid} ${earliest} ${rootExitedAt}
for ($index = $identities.Count - 1; $index -ge 0; $index--) {
    $entry = $identities[$index]
    $current = @(Get-CimInstance Win32_Process -Filter "ProcessId = $($entry.pid)" -Property ProcessId,ParentProcessId,CreationDate)
    if ($current.Count -eq 0) { $entry.status = 'exited'; continue }
    if ((CreationMs $current[0]) -ne $entry.createdAtMs -or [int]$current[0].ParentProcessId -ne $entry.parentPid) {
        $entry.status = 'identity_changed'
        $errors.Add("Descendant $($entry.pid) identity changed; it was not killed.")
        continue
    }
    & (Join-Path $env:SystemRoot 'System32/taskkill.exe') /PID $entry.pid /T /F 2>$null | Out-Null
    $entry.status = 'termination_requested'
}
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(2)
do {
    $remaining = @()
    foreach ($entry in $identities) {
        $current = @(Get-CimInstance Win32_Process -Filter "ProcessId = $($entry.pid)" -Property ProcessId,ParentProcessId,CreationDate)
        if ($current.Count -eq 1 -and (CreationMs $current[0]) -eq $entry.createdAtMs) { $remaining += $entry.pid }
        elseif ($entry.status -eq 'termination_requested') { $entry.status = 'terminated' }
    }
    if ($remaining.Count -gt 0) { Start-Sleep -Milliseconds 25 }
} while ($remaining.Count -gt 0 -and [DateTimeOffset]::UtcNow -lt $deadline)
[pscustomobject]@{ complete = ($remaining.Count -eq 0 -and $errors.Count -eq 0); method = 'verified-parent-and-creation-time'; identities = $identities.ToArray(); remaining = @($remaining); errors = $errors.ToArray() } | ConvertTo-Json -Depth 8 -Compress
`;
    const executable = join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
    return new Promise((resolve, reject) => {
        execFile(executable, ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', script], {
            windowsHide: true, shell: false, encoding: 'utf8', timeout: 10_000, maxBuffer: 1024 * 1024,
        }, (error, stdout) => {
            if (error) reject(new Error(`Descendant verification failed: ${error.message}`));
            else {
                try { resolve(JSON.parse(stdout)); }
                catch { reject(new Error('Descendant verification did not return valid JSON.')); }
            }
        });
    });
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
        writeTask: options.writeTask ?? false, sandbox: options.writeTask ? 'workspace-write' : 'read-only',
        gitCommonDir: options.gitCommonDir ?? null, approvalPolicy: 'never',
        executable, args: [...executableArgs, ...args], pid: null, threadId: null,
        startedAt: new Date().toISOString(), finishedAt: null, exitCode: null, signal: null,
        paths, errors: [], handoff: null, requiresCoordinatorReview: true,
        cleanup: { complete: false, method: 'not-checked', identities: [] },
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
    let rootExited = false;
    let rootExitedAt;
    let termination;
    let killPromise = Promise.resolve();
    let timer;
    let child;
    const stop = (state, message) => {
        if (closed || rootExited || termination) return;
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
            receipt.cleanup = { complete: true, method: 'not-spawned', identities: [] };
        } else {
            receipt.spawnedAt = new Date().toISOString();
            child = spawn(executable, [...executableArgs, ...args], {
                cwd: options.cwd, shell: false, windowsHide: true,
                detached: process.platform !== 'win32', stdio: ['pipe', 'pipe', 'pipe'],
            });
            receipt.pid = child.pid ?? null;
            receipt.state = 'running';
            writeReceipt(paths.receipt, receipt);
            const completion = new Promise(resolve => {
                child.once('error', error => {
                    receipt.errors.push(`Spawn failed: ${error.message}`);
                    rootExited = true;
                    rootExitedAt = Date.now();
                    resolve();
                });
                child.once('exit', (code, signal) => {
                    rootExited = true;
                    rootExitedAt = Date.now();
                    receipt.rootExitedAt = new Date(rootExitedAt).toISOString();
                    receipt.exitCode = code;
                    receipt.signal = signal;
                    resolve();
                });
                child.once('close', (code, signal) => {
                    closed = true;
                    receipt.exitCode = code;
                    receipt.signal = signal;
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
            clearTimeout(timer);
            await killPromise;
            if (receipt.pid) {
                try {
                    receipt.cleanup = await cleanupDescendants(receipt.pid, Date.parse(receipt.spawnedAt), rootExitedAt);
                } catch (error) {
                    receipt.cleanup = { complete: false, method: 'verification-failed', identities: [], errors: [error.message] };
                }
            } else {
                receipt.cleanup = { complete: true, method: 'not-spawned', identities: [] };
            }
            for (let retry = 0; !closed && retry < 50; retry++) await delay(10);
            if (!closed) {
                receipt.cleanup.complete = false;
                receipt.errors.push('CLI streams remained open after root exit; detached descendants may still own them.');
                child.stdin.destroy();
                child.stdout.destroy();
                child.stderr.destroy();
                for (let retry = 0; !closed && retry < 50; retry++) await delay(10);
            }
            consume(decoder.end());
            if (pending.trim()) parseLine(protocol, pending, ++lineNumber);
        }
    } catch (error) {
        receipt.errors.push(error.message);
        if (!child) {
            receipt.cleanup = { complete: true, method: 'not-spawned', identities: [] };
        } else if (!closed) {
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
    if (!receipt.cleanup.complete) receipt.errors.push('Owned process cleanup requires coordinator recovery.');
    receipt.state = !receipt.cleanup.complete ? 'cleanup_required' : termination?.state ?? (receipt.errors.length === 0 ? 'completed' : 'failed');
    receipt.finishedAt = new Date().toISOString();
    receipt.retryable = receipt.state !== 'completed' && receipt.state !== 'cleanup_required';
    receipt.turnCompleted = protocol.completed;
    writeReceipt(paths.receipt, receipt);
    return receipt;
}
