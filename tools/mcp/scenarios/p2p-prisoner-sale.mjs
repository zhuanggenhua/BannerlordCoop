import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { appendFile, mkdir, readFile, realpath, stat, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { assertActors, assertPlayers, assertProcessEvidence, assertWorld, assertWriter,
    epoch, field, jsonOutput, logDiagnostics, writer, paused as leasePaused } from './p2p-udp-reliability.mjs';

export const HELP = `Usage: node tools/mcp/scenarios/p2p-prisoner-sale.mjs
  --repo <coordinator checkout> --server <CoopMcpServer.exe> --config <profiles.json>
  --sdk-root <node_modules> --record <new evidence.jsonl>
  [--profile local] [--save P1B_DirectIP_Recruit]

Run only from the coordinator through tools/coordination/with-game-lock.mjs.
Starts two distinct direct-IP native processes, prepares regular NPC prisoners
only while the target party's confirmed writer owns the world, and submits four
real player sale-prefix requests across A-writer and B-writer epochs. Every sale
checks actor gold, exact prisoner count/wounds/XP, replica hashes and one native
application record. This is trusted QA preparation plus regular NPC ransom only;
it does not accept player-hero ransom, Mission/capture flows, natural cycles,
cold saves, or network fault injection. No deployment, private IPC, teleport,
direct processor call, replenishment, or retry of uncertain sale mutations.
--help/import are offline and do not import SDK, bind sockets or launch games.`;

const INSTANCES = ['server', 'client1'];
const TOWN = { id: 'town_ES1', location: 'Location_town_ES1_tavern' };
const FIXTURE = { id: 'imperial_infantryman', number: 2, wounded: 1, xp: 137 };
const STABLE_PLAYER = ['heroId', 'heroStringId', 'partyId', 'partyStringId', 'clanId', 'characterId',
    'registeredPlayers', 'heroRegistrations', 'partyRegistrations', 'clanRegistrations', 'troops', 'wounded',
    'settlementId', 'x', 'y', 'active'];
const STABLE_WORLD = ['campaign', 'partyIdentityHash', 'volunteerHash', 'nextHandle', 'allocationCounterHash'];

export function options(argv) {
    const config = { profile: 'local', save: 'P1B_DirectIP_Recruit' }, seen = new Set();
    const allowed = new Set(['repo', 'server', 'config', 'sdk-root', 'record', 'profile', 'save']);
    for (let i = 0; i < argv.length; i += 2) {
        const key = argv[i]?.replace(/^--/, '');
        assert.ok(argv[i]?.startsWith('--') && allowed.has(key) && !seen.has(key) && argv[i + 1] &&
            !argv[i + 1].startsWith('--'), 'Invalid or duplicate option: ' + argv[i]);
        seen.add(key); config[key] = argv[i + 1];
    }
    for (const key of ['repo', 'server', 'config', 'sdk-root', 'record']) {
        assert.ok(config[key], 'Missing --' + key); config[key] = resolve(config[key]);
    }
    return config;
}

export class OnceActions {
    #sent = new Set();
    async run(key, action) {
        assert.ok(!this.#sent.has(key), 'Mutation already dispatched: ' + key);
        this.#sent.add(key);
        return action();
    }
}

export async function pollReadOnly(label, readAndAssert, {
    timeoutMs = 30000, intervalMs = 500, now = Date.now, sleep = delay, onPending = async () => {},
} = {}) {
    const deadline = now() + timeoutMs; let lastError;
    do {
        try { return await readAndAssert(deadline); }
        catch (error) { lastError = error; await onPending(error); }
        if (now() < deadline) await sleep(Math.min(intervalMs, deadline - now()));
    } while (now() < deadline);
    throw new Error(`${label} did not converge: ${lastError?.message}`, { cause: lastError });
}

export function assertSaleRegistrations(pair, actors) {
    assertPlayers(pair);
    for (let viewpoint = 0; viewpoint < pair.length; viewpoint++) {
        for (let i = 0; i < pair[viewpoint].length; i++) {
            const row = pair[viewpoint][i];
            assert.equal(row.controllerId, actors[i].controller);
            assert.equal(row.active, true);
            assert.equal(row.registeredPlayers, 2); assert.equal(row.heroRegistrations, 1);
            assert.equal(row.partyRegistrations, 1); assert.equal(row.clanRegistrations, 1);
            assert.equal(row.heroStringId, actors[i].mainHero); assert.equal(row.partyStringId, actors[i].mainParty);
        }
    }
    return pair;
}

export function assertPausedSnapshot(timeModes, states, actors, worlds, previousWorlds) {
    assert.deepEqual(timeModes, ['Pause', 'Pause'], 'Both native peers must report campaign time Pause');
    assert.equal(states.length, 2);
    for (const value of states) {
        assert.equal(leasePaused(value), false, 'Native campaign Pause must retain an active authority lease');
        assert.equal(value.frozen, false, 'Native campaign Pause must not freeze the authority world');
    }
    assertWriter(states, writer(states[0]), actors);
    assertWorld(worlds);
    if (previousWorlds === undefined) return false;
    assert.deepEqual(worlds, previousWorlds, 'World changed while campaign time is paused');
    return true;
}

function readToken(value) { return value.startsWith('"') ? JSON.parse(value) : value; }

export function saleApplications(logTexts) {
    const copiedAcrossSources = new Set();
    const rows = [];
    const token = '("(?:[^"\\\\]|\\\\.)*"|\\S+)';
    const expression = new RegExp('Prisoner sale applied originController=' + token +
        ' writerController=' + token + ' epoch=(\\d+) authorityId=' + token +
        ' requestStream=' + token + ' requestSequence=(\\d+) sellingPartyId=(\\d+)');
    for (const text of logTexts) {
        const seenInSource = new Set();
        for (const rawLine of String(text).split(/\r?\n/)) {
            const line = rawLine.trim();
            if (!line) continue;
            if (seenInSource.has(line)) {
                // Repeated records within one log source are independent native log emissions.
            } else {
                seenInSource.add(line);
                if (copiedAcrossSources.has(line)) continue; // Two sources can expose the same archived file copy.
                copiedAcrossSources.add(line);
            }
            const match = expression.exec(line);
            if (!match) continue;
            rows.push({ originController: readToken(match[1]), writerController: readToken(match[2]),
                epoch: Number(match[3]), authorityId: readToken(match[4]), requestStream: readToken(match[5]),
                requestSequence: Number(match[6]), sellingPartyId: Number(match[7]), line });
        }
    }
    return rows;
}

export function assertSaleApplications(logTexts, intents) {
    const applied = saleApplications(logTexts);
    assert.equal(applied.length, intents.length, 'Native sale applications do not match submitted intent count');
    const requestIds = new Set();
    for (const row of applied) {
        assert.match(row.requestStream, /^[0-9a-f]{32}$/i, 'Application log has an invalid request stream');
        assert.ok(Number.isSafeInteger(row.requestSequence) && row.requestSequence > 0, 'Invalid request sequence');
        assert.ok(Number.isSafeInteger(row.sellingPartyId) && row.sellingPartyId > 0, 'Invalid selling party handle');
        const requestId = `${row.originController}\0${row.requestStream}\0${row.requestSequence}`;
        assert.ok(!requestIds.has(requestId), 'Duplicate applied request stream/sequence');
        requestIds.add(requestId);
    }
    for (const intent of intents) {
        const matching = applied.filter(row => row.originController === intent.originController &&
            row.writerController === intent.writerController && row.epoch === intent.epoch &&
            row.authorityId === intent.authorityId && row.sellingPartyId === intent.sellingPartyId);
        assert.equal(matching.length, 1, 'Submitted sale intent must have exactly one native application: ' + JSON.stringify(intent));
    }
    return applied;
}

function rosterRows(value) {
    return (value ?? []).map(row => ({
        character: field(row, 'character'),
        number: field(row, 'number'),
        wounded: field(row, 'woundedNumber'),
        xp: field(row, 'xp'),
        isHero: field(row, 'isHero'),
    })).sort((a, b) => String(a.character).localeCompare(String(b.character)));
}

export function assertSaleTransition(before, after, intent) {
    assertPlayers(before.players); assertPlayers(after.players);
    assertWorld(before.worlds); assertWorld(after.worlds);
    assertActors(after.states, before.states);
    assert.equal(intent.expectedRansomTotal > 0, true, 'Fixture must have positive native ransom value');
    const previousActors = before.actors;
    const currentActors = after.actors;
    for (let viewpoint = 0; viewpoint < 2; viewpoint++) {
        const oldRows = new Map(previousActors[viewpoint].map(row => [row.targetController, row]));
        for (const row of currentActors[viewpoint]) {
            const previous = oldRows.get(row.targetController); assert.ok(previous, 'Actor disappeared from inspection');
            for (const key of ['targetHeroId', 'targetPartyId', 'targetPartyHandle', 'settlement', 'ransomRecipientHeroId'])
                assert.equal(row[key], previous[key], `Actor identity changed: ${row.targetController}/${key}`);
            assert.deepEqual(rosterRows(row.memberRoster), rosterRows(previous.memberRoster),
                `Member roster changed for ${row.targetController}`);
            const isSeller = row.targetController === intent.originController;
            assert.equal(row.gold, previous.gold + (isSeller ? intent.expectedRansomTotal : 0),
                `Native ransom was credited to the wrong hero: ${row.targetController}`);
            assert.equal(row.ransomRecipientGold, row.gold, `Ransom recipient differs from actor ${row.targetController}`);
            if (isSeller) {
                assert.deepEqual(rosterRows(row.prisonRoster), [], 'Sale did not clear the selected NPC prisoner roster');
                assert.equal(row.expectedRansomTotal, 0, 'Sold prisoner stack still contributes a ransom value');
            }
            else assert.deepEqual(rosterRows(row.prisonRoster), rosterRows(previous.prisonRoster),
                `Unselected prisoner roster changed for ${row.targetController}`);
        }

        const oldPlayerRows = new Map(before.players[viewpoint].map(row => [row.controllerId, row]));
        for (const row of after.players[viewpoint]) {
            const previous = oldPlayerRows.get(row.controllerId); assert.ok(previous, 'Player registration disappeared');
            for (const key of STABLE_PLAYER) assert.equal(row[key], previous[key], `${row.controllerId}/${key}`);
            assert.equal(row.gold, previous.gold + (row.controllerId === intent.originController ? intent.expectedRansomTotal : 0),
                `Player-state gold changed on the wrong actor: ${row.controllerId}`);
        }

        const oldWorld = before.worlds[viewpoint], newWorld = after.worlds[viewpoint];
        for (const key of STABLE_WORLD) assert.equal(newWorld[key], oldWorld[key], `Sale changed ${key}`);
        assert.ok(newWorld.ticks >= oldWorld.ticks, 'Campaign ticks moved backwards');
        assert.notEqual(newWorld.rosterCountsHash, oldWorld.rosterCountsHash, 'Native sale did not change roster counts');
        assert.notEqual(newWorld.rosterXpHash, oldWorld.rosterXpHash, 'Native sale did not remove the staged roster XP');
        assert.notEqual(newWorld.heroGoldHash, oldWorld.heroGoldHash, 'Native sale did not change the world gold hash');
    }
    return { amount: intent.expectedRansomTotal, controller: intent.originController,
        writer: intent.writerController, epoch: intent.epoch, partyHandle: intent.sellingPartyId };
}

export function assertStopped(stopped, expectedCount) {
    assert.equal(stopped.state, 'stopped'); assert.ok(stopped.instances?.length > 0);
    if (expectedCount !== undefined) assert.equal(stopped.instances.length, expectedCount);
    assert.ok(stopped.instances.every(row => row.cleanupComplete === true && row.processAlive === false && row.processTreeAlive === false));
}

export async function run(config) {
    assert.ok((await stat(join(config.repo, '.git'))).isDirectory(), 'Live scenario requires coordinator main checkout');
    assert.equal(await realpath(execFileSync('git', ['rev-parse', '--show-toplevel'],
        { cwd: config.repo, encoding: 'utf8', windowsHide: true }).trim()), await realpath(config.repo));
    for (const path of [config.server, config.config]) assert.ok((await stat(path)).isFile(), `Missing file: ${path}`);
    const profile = JSON.parse(await readFile(config.config, 'utf8')).profiles?.[config.profile];
    assert.ok(profile?.listenAuthority && profile.serverPlatformId && profile.clientPlatformIds?.[0]);
    assert.notEqual(profile.serverPlatformId, profile.clientPlatformIds[0]);
    assert.ok(!profile.clientProxyPort, 'Use the original direct-IP profile without a proxy');
    const sdk = join(config['sdk-root'], '@modelcontextprotocol/sdk/dist/esm/client');
    const [{ Client }, { StdioClientTransport }] = await Promise.all([
        import(pathToFileURL(join(sdk, 'index.js')).href), import(pathToFileURL(join(sdk, 'stdio.js')).href),
    ]);
    await mkdir(dirname(config.record), { recursive: true });
    const summaryPath = config.record + '.summary.json';
    await writeFile(config.record, '', { flag: 'wx' }); await writeFile(summaryPath, '{}', { flag: 'wx' });
    const summary = { scenario: 'p2p-prisoner-sale', startedUtc: new Date().toISOString(),
        codeHead: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: config.repo, encoding: 'utf8', windowsHide: true }).trim(),
        config, runId: null, features: [], sales: [], screenshots: [], logs: {}, failed: null,
        cleanupConfirmed: false, passed: false,
        scope: 'Trusted writer-only Danustica preparation and real regular-NPC player-sale prefix; no player-hero ransom, captivity/Mission, natural-cycle, cold-save, or network-fault acceptance.' };
    let runId, callId = 0, initial = null, readDeadline;
    const once = new OnceActions();
    const client = new Client({ name: 'codex-p2p-prisoner-sale', version: '1.0.0' });
    const transport = new StdioClientTransport({ command: config.server, args: ['--config', config.config],
        cwd: config.repo, stderr: 'inherit', maxBufferSize: 16 * 1024 * 1024 });
    const logState = Object.fromEntries(INSTANCES.map(instance => [instance, { text: '', cursor: undefined, copiesRemoved: 0 }]));
    const emit = entry => appendFile(config.record, JSON.stringify({ utc: new Date().toISOString(), ...entry }) + '\n');
    const saveSummary = () => writeFile(summaryPath, JSON.stringify(summary, null, 2) + '\n');

    async function call(name, args, mutation = false) {
        assert.ok(!(readDeadline && mutation), 'Mutations cannot run inside a polling callback');
        const remaining = readDeadline ? readDeadline - Date.now() : 360000;
        assert.ok(remaining > 0, 'Read-only convergence deadline expired');
        const id = ++callId; await emit({ phase: 'request', callId: id, name, args, mutation });
        let raw;
        try { raw = await client.callTool({ name, arguments: args }, undefined, { timeout: remaining }); }
        catch (error) {
            await emit({ phase: 'transport_error', callId: id, name, error: error.stack,
                outcomeUncertain: mutation, noMutationRetry: mutation }); throw error;
        }
        let data;
        try { data = raw.structuredContent ?? JSON.parse(raw.content.find(item => item.type === 'text').text); }
        catch (error) { await emit({ phase: 'decode_error', callId: id, name, raw }); throw error; }
        if (name === 'start_run' && data.runId) { runId = data.runId; summary.runId = runId; }
        await emit({ phase: 'response', callId: id, name, args, data, raw });
        assert.ok(!raw.isError, `${name} MCP error: ${JSON.stringify(data)}`);
        console.log(JSON.stringify({ tool: name, instance: args.instance, callId: id, runId,
            status: data.state ?? data.ok ?? data.outcome ?? data.result?.reached }));
        return data;
    }
    async function commandResult(instance, name, args = [], mutation = false) {
        const response = await call('execute_command', { run_id: runId, instance, name, arguments: args }, mutation);
        assert.equal(response.ok, true, JSON.stringify(response)); assert.ok(response.result, 'Missing command result');
        return response.result;
    }
    async function command(instance, name, args = [], mutation = false) {
        const response = await commandResult(instance, name, args, mutation);
        assert.equal(response.succeeded, true, `${name}: ${response.output}`); return response.output;
    }
    const state = instance => command(instance, 'coop.debug.authority.state').then(jsonOutput);
    const world = instance => command(instance, 'coop.debug.authority.world_state').then(jsonOutput);
    const inspect = (instance, controller) => command(instance, 'coop.debug.prisoner_sale.inspect', [controller]).then(jsonOutput);
    const playerState = (instance, controller) => command(instance, 'coop.debug.players.party_state', [controller]).then(jsonOutput);
    const bothStates = () => Promise.all(INSTANCES.map(state));

    async function poll(label, readAndAssert, timeout = 30000) {
        return pollReadOnly(label, async deadline => {
            const previousDeadline = readDeadline;
            try { readDeadline = deadline; return await readAndAssert(); }
            finally { readDeadline = previousDeadline; }
        }, { timeoutMs: timeout, onPending: error => emit({ phase: 'poll_pending', label, reason: error.message }) });
    }
    async function awaitWriter(controller) {
        return poll('confirmed writer ' + controller, async () => {
            const states = await bothStates(); assertWriter(states, controller, initial);
            assert.equal(writer(states[0]), writer(states[1]));
            for (const value of states) { assert.equal(leasePaused(value), false); assert.equal(value.frozen, false); }
            return states;
        });
    }
    async function readRegistrations() {
        const pair = await Promise.all(INSTANCES.map(instance => Promise.all(initial.map(actor => playerState(instance, actor.controller)))));
        assertSaleRegistrations(pair, initial);
        assertActors(await bothStates(), initial); return pair;
    }
    async function registrations(waitForReady = true) {
        return waitForReady ? poll('registered player replicas', readRegistrations) : readRegistrations();
    }
    async function snapshot(label) {
        return poll(label, async () => {
            const states = await bothStates(); assertActors(states, initial);
            const worlds = await Promise.all(INSTANCES.map(world)); assertWorld(worlds);
            const players = await registrations(false);
            const actors = await Promise.all(INSTANCES.map(instance => Promise.all(initial.map(actor => inspect(instance, actor.controller)))));
            for (let viewpoint = 0; viewpoint < 2; viewpoint++) {
                for (const row of actors[viewpoint]) {
                    assert.ok(row.targetHeroId && row.targetPartyId && Number.isSafeInteger(row.targetPartyHandle));
                    assert.equal(row.targetHeroId, initial.find(actor => actor.controller === row.targetController)?.mainHero);
                    assert.equal(row.targetPartyId, initial.find(actor => actor.controller === row.targetController)?.mainParty);
                }
            }
            return { states, worlds, players, actors };
        });
    }
    async function pause() {
        await command('server', 'coop.debug.set_time_mode', ['Pause', 'force-live-test'], true);
        let previousWorlds;
        return poll('paused campaign clock', async () => {
            const states = await bothStates();
            const timeModes = await Promise.all(INSTANCES.map(instance => command(instance, 'coop.debug.get_time_mode')));
            const worlds = await Promise.all(INSTANCES.map(world)); assertWorld(worlds);
            const stable = assertPausedSnapshot(timeModes, states, initial, worlds, previousWorlds);
            previousWorlds = worlds;
            assert.equal(stable, true, 'Waiting for an unchanged world across paused reads');
            return worlds;
        });
    }
    async function play() {
        await command('server', 'coop.debug.set_time_mode', ['Play_1x', 'force-live-test'], true);
        return awaitWriter(writer(await state('server')));
    }
    async function waitState(instance, expected, seconds = 300) {
        const response = await call('wait_for_state', { run_id: runId, instance, state: expected, timeout_seconds: seconds });
        assert.equal((response.result ?? response).reached, true, JSON.stringify(response));
    }
    async function nativeMap(instance) {
        const response = await call('get_run', { run_id: runId });
        const status = response.instances?.find(row => row.name === instance)?.status;
        assert.ok(status); assert.equal(status.missionActive, false);
        assert.equal(status.activeState, 'TaleWorlds.CampaignSystem.GameState.MapState');
        return status;
    }
    async function screenshot(instance, label) {
        const requested = await call('screenshot', { run_id: runId, instance }, true);
        assert.equal(requested.ok, true, JSON.stringify(requested));
        const captureId = requested.result?.captureId; assert.ok(captureId, 'Missing screenshot capture id');
        const capture = await poll('stable BMP screenshot ' + captureId, async () => {
            const result = await call('screenshot_status', { run_id: runId, instance, capture_id: captureId });
            assert.equal(result.ok, true); assert.equal(result.result.complete, true);
            assert.ok(result.result.isBmp && result.result.headerValid && result.result.stable && result.result.sha256);
            return result.result;
        }, 60000);
        summary.screenshots.push({ instance, label, ...capture });
    }
    async function collectLogs(archived = false) {
        const errors = [];
        for (const instance of INSTANCES) {
            try {
                const log = logState[instance];
                for (let page = 0; page < 1024; page++) {
                    const response = await call('read_logs', { run_id: runId, instance, cursor: log.cursor, max_bytes: 65536 });
                    const chunk = response.result ?? response;
                    assert.equal(typeof chunk.text, 'string'); assert.equal(typeof chunk.cursor, 'string');
                    assert.equal(typeof chunk.reset, 'boolean'); assert.equal(typeof chunk.hasMore, 'boolean');
                    if (chunk.reset) {
                        assert.ok(archived, 'Live log reset; complete native application evidence is unavailable');
                        const existing = new Set(log.text.split(/\r?\n/));
                        const novel = chunk.text.split(/\r?\n/).filter(line => line && !existing.has(line));
                        log.copiesRemoved += chunk.text.split(/\r?\n/).filter(line => line && existing.has(line)).length;
                        if (novel.length) log.text += (log.text.endsWith('\n') ? '' : '\n') + novel.join('\n') + '\n';
                    } else log.text += chunk.text;
                    log.cursor = chunk.cursor;
                    if (!chunk.hasMore) break;
                    assert.ok(page < 1023, 'Log page budget exhausted');
                }
                const path = config.record + '.' + instance + '.log'; await writeFile(path, log.text);
                const critical = logDiagnostics(log.text);
                summary.logs[instance] = { path, sha256: createHash('sha256').update(log.text).digest('hex'),
                    critical, archivedDuplicateLinesRemoved: log.copiesRemoved };
                assert.equal(critical.realTick + critical.duplicateIds + critical.aiBatch + critical.errorLines.length, 0,
                    `${instance} strict log diagnostics: ${JSON.stringify(critical)}`);
            } catch (error) { errors.push(`${instance}: ${error.message}`); await emit({ phase: 'log_evidence_error', instance, error: error.stack }); }
        }
        assert.deepEqual(errors, [], 'Native log evidence or strict diagnostics failed');
    }
    async function feature(name, action) {
        const row = { name, startedUtc: new Date().toISOString(), status: 'RUNNING' };
        summary.features.push(row); await saveSummary();
        try {
            row.evidence = await action(); await collectLogs();
            if (row.evidence?.kind === 'sale') {
                const allLogs = INSTANCES.map(instance => logState[instance].text);
                row.evidence.appliedEntries = assertSaleApplications(allLogs, summary.sales);
            }
            for (const instance of INSTANCES) { await nativeMap(instance); await screenshot(instance, name); }
            row.status = 'PASS';
        } catch (error) {
            row.status = 'FAIL'; row.error = error.stack;
            try { await collectLogs(); } catch (diagnostic) { row.logError = diagnostic.stack; }
            for (const instance of INSTANCES) { try { await screenshot(instance, name + '-failed'); } catch (diagnostic) { row.captureError = diagnostic.stack; } }
            throw error;
        } finally {
            row.completedUtc = new Date().toISOString(); await emit({ phase: 'feature_result', feature: row }); await saveSummary();
        }
    }
    async function settlementReplicas(controller, requireBackstreetInstance) {
        return poll('Danustica/backstreet replicas for ' + controller, async () => {
            const values = await Promise.all(INSTANCES.map(instance => inspect(instance, controller)));
            for (const value of values) assert.equal(value.settlement, TOWN.id);
            if (requireBackstreetInstance) assert.equal(values[INSTANCES.indexOf(requireBackstreetInstance)].currentMenu, 'town_backstreet');
            await registrations(false); return values;
        });
    }
    async function ensureSettlement(instance, controller) {
        const before = await inspect(instance, controller);
        if (before.settlement === TOWN.id && before.currentMenu === 'town_backstreet')
            return { alreadyInsideBackstreet: true, values: await settlementReplicas(controller, instance) };
        const first = await command(instance, 'coop.debug.location.enter', [TOWN.location], true);
        let view = await call('get_run', { run_id: runId });
        let mission = first.startsWith('Opening location') ||
            view.instances.find(value => value.name === instance)?.status?.missionActive;
        let current = await inspect(instance, controller);
        if (!mission && (current.settlement !== TOWN.id || current.currentMenu !== 'town_backstreet')) {
            // Danustica's first native entry can initialize PlayerEncounter without opening the tavern.
            const second = await command(instance, 'coop.debug.location.enter', [TOWN.location], true);
            view = await call('get_run', { run_id: runId });
            mission = second.startsWith('Opening location') ||
                view.instances.find(value => value.name === instance)?.status?.missionActive;
        }
        if (mission) {
            await waitState(instance, 'readyForMissionTests');
            await command(instance, 'coop.debug.location.leave', [], true);
            await poll('native map after requested Mission exit', () => nativeMap(instance), 60000);
        }
        current = await inspect(instance, controller);
        if (current.settlement === TOWN.id && current.currentMenu === 'town')
            await command(instance, 'coop.debug.prisoner_sale.open_backstreet', [controller], true);
        const values = await settlementReplicas(controller, instance);
        current = values[INSTANCES.indexOf(instance)];
        return { first, mission, current, values };
    }
    async function prepareSale(writerInstance, targetController) {
        const writerController = initial[INSTANCES.indexOf(writerInstance)].controller;
        await assertWriter(await bothStates(), writerController, initial);
        const output = jsonOutput(await once.run(`prepare-${writerController}-${targetController}-${summary.sales.length + 1}`,
            () => command(writerInstance, 'coop.debug.prisoner_sale.prepare', [targetController], true)));
        assert.equal(output.status, 'prepared'); assert.equal(output.writerController, writerController);
        assert.equal(output.targetController, targetController); assert.equal(output.settlementId, TOWN.id);
        assert.deepEqual(rosterRows(output.prisonRoster), [{ character: FIXTURE.id, number: FIXTURE.number,
            wounded: FIXTURE.wounded, xp: FIXTURE.xp, isHero: false }]);
        await pause();
        const before = await snapshot('prisoner-sale-before-' + (summary.sales.length + 1));
        for (const viewpoint of before.actors) {
            const row = viewpoint.find(value => value.targetController === targetController);
            assert.equal(row.settlement, TOWN.id);
            assert.equal(row.ransomRecipientHeroId, row.targetHeroId, 'Native ransom recipient must be the selected player');
            assert.deepEqual(rosterRows(row.prisonRoster), [{ character: FIXTURE.id, number: FIXTURE.number,
                wounded: FIXTURE.wounded, xp: FIXTURE.xp, isHero: false }]);
            assert.ok(Number.isSafeInteger(row.expectedRansomTotal) && row.expectedRansomTotal > 0);
            assert.equal(row.ransomRecipientGold, row.gold);
        }
        assert.equal(output.partyHandle, before.actors[0].find(row => row.targetController === targetController).targetPartyHandle);
        return { before, expectedRansomTotal: before.actors[0].find(row => row.targetController === targetController).expectedRansomTotal,
            partyHandle: output.partyHandle, output };
    }
    async function sell(originInstance, writerInstance) {
        const originController = initial[INSTANCES.indexOf(originInstance)].controller;
        const writerController = initial[INSTANCES.indexOf(writerInstance)].controller;
        await play();
        await assertWriter(await bothStates(), writerController, initial);
        await settlementReplicas(originController, originInstance);
        const fixture = await prepareSale(writerInstance, originController);
        const originBefore = fixture.before.actors[INSTANCES.indexOf(originInstance)]
            .find(row => row.targetController === originController);
        // Native time Pause retains the confirmed writer lease. Keep unrelated NPC ticks
        // outside this one-action comparison; active simulation has its own stress scenario.
        const output = jsonOutput(await once.run(`submit-sale-${summary.sales.length + 1}`,
            () => command(originInstance, 'coop.debug.prisoner_sale.submit', [originController], true)));
        assert.equal(output.status, 'submitted'); assert.equal(output.controller, originController);
        assert.equal(output.localAuthority, originController === writerController);
        assert.equal(field(output.lease, 'authorityControllerId'), writerController);
        assert.equal(output.partyHandle, fixture.partyHandle);
        assert.equal(output.expectedRansomTotal, fixture.expectedRansomTotal);
        assert.equal(output.goldBefore, originBefore.gold);
        if (!output.localAuthority) {
            assert.equal(output.goldAfterImmediateLocal, output.goldBefore,
                'Observer changed local actor gold before the current writer applied the sale');
            assert.deepEqual(rosterRows(output.prisonRosterAfterImmediateLocal), [{ character: FIXTURE.id,
                number: FIXTURE.number, wounded: FIXTURE.wounded, xp: FIXTURE.xp, isHero: false }],
            'Observer changed its local prisoner roster before writer application');
        }
        const intent = { originController, writerController, epoch: epoch(fixture.before.states[0]),
            authorityId: field(fixture.before.states[0].lease, 'authorityId'),
            sellingPartyId: fixture.partyHandle, expectedRansomTotal: fixture.expectedRansomTotal,
            originInstance, writerInstance };
        summary.sales.push(intent);
        await poll(`one native apply log for ${originController}`, async () => {
            await collectLogs();
            const matches = saleApplications(INSTANCES.map(instance => logState[instance].text))
                .filter(row => row.originController === intent.originController &&
                    row.writerController === intent.writerController && row.epoch === intent.epoch &&
                    row.authorityId === intent.authorityId && row.sellingPartyId === intent.sellingPartyId);
            assert.ok(matches.length <= 1, 'Native writer applied one submitted sale more than once');
            assert.equal(matches.length, 1, 'Native writer has not yet logged the applied sale');
            return matches[0];
        }, 60000);
        await pause();
        const after = await poll(`one authoritative sale for ${originController}`, async () => {
            const receipt = await snapshot('prisoner-sale-convergence');
            assertSaleTransition(fixture.before, receipt, intent);
            for (const state of receipt.states) {
                assert.equal(field(state.lease, 'authorityControllerId'), writerController);
                assert.equal(epoch(state), intent.epoch, 'Campaign authority epoch changed during the sale');
                assert.equal(field(state.lease, 'authorityId'), intent.authorityId, 'World authority changed during the sale');
            }
            return receipt;
        }, 60000);
        return { kind: 'sale', intent, request: output, before: fixture.before, after,
            authoritativeRecipient: originBefore.ransomRecipientHeroId,
            writerHeroId: fixture.before.states[INSTANCES.indexOf(writerInstance)].mainHero };
    }

    try {
        await client.connect(transport);
        const started = await call('start_run', { profile: config.profile, client_count: 0, save_name: config.save }, true);
        assert.ok(runId); assert.equal(started.error, null);
        await waitState('server', 'readyForCampaignTests'); await delay(1000);
        const launched = await call('start_client', { run_id: runId, client_index: 1 }, true);
        assert.equal(launched.outcome, 'launched'); assert.equal(launched.instance, 'client1'); assert.equal(launched.run.error, null);
        await waitState('client1', 'readyToJoin', 180);
        const joined = await call('join_client', { run_id: runId, instance: 'client1' }, true);
        assert.equal(joined.ok, true); await waitState('client1', 'readyForCampaignTests');
        initial = await bothStates(); assert.notEqual(initial[0].controller, initial[1].controller);
        assert.deepEqual(initial.map(value => value.controller), [profile.serverPlatformId, profile.clientPlatformIds[0]]);
        for (const value of initial) assert.ok(value.mainHero && value.mainParty);
        await play();
        for (const instance of INSTANCES) {
            const catalog = JSON.stringify(await call('list_commands', { run_id: runId, instance }));
            for (const name of ['coop.debug.authority.state', 'coop.debug.authority.world_state',
                'coop.debug.authority.availability', 'coop.debug.players.party_state', 'coop.debug.set_time_mode',
                'coop.debug.get_time_mode',
                'coop.debug.location.list', 'coop.debug.location.enter', 'coop.debug.location.leave',
                'coop.debug.prisoner_sale.prepare', 'coop.debug.prisoner_sale.inspect',
                'coop.debug.prisoner_sale.open_backstreet', 'coop.debug.prisoner_sale.submit'])
                assert.ok(catalog.includes(name), `${instance} lacks ${name}`);
        }

        await feature('join-two-native-actors', async () => {
            const states = await awaitWriter(initial[0].controller); const actors = await registrations();
            const view = await call('get_run', { run_id: runId });
            assert.equal(view.instances.length, 2);
            const [a, b] = INSTANCES.map(name => view.instances.find(value => value.name === name));
            assert.ok(a?.processAlive && b?.processAlive && a.identity.pid !== b.identity.pid);
            assert.equal(a.status.buildVersion, b.status.buildVersion);
            const assemblies = value => value.status.loadedAssemblies.map(item => [item.name, item.mvid])
                .sort((left, right) => left[0].localeCompare(right[0]));
            assert.deepEqual(assemblies(a), assemblies(b), 'Native game processes loaded different mod assemblies');
            const osProcesses = JSON.parse(execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-Command',
                "@(Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^Bannerlord.*\\.exe$|DedicatedServer' } | Select-Object Name,ProcessId,CreationDate,CommandLine) | ConvertTo-Json -Compress -Depth 4"],
                { encoding: 'utf8', windowsHide: true }));
            assertProcessEvidence(osProcesses, [a.identity.pid, b.identity.pid]);
            await writeFile(config.record + '.processes.json', JSON.stringify(osProcesses, null, 2));
            return { states, actors, processes: view.instances, osProcesses };
        });

        await feature('native-Danustica-writer-only-preparation', async () => {
            await play();
            const locations = await command('server', 'coop.debug.location.list');
            assert.ok([...locations.matchAll(/Id: '([^']+)'/g)].some(match => match[1] === TOWN.location),
                'Native Danustica tavern was not observed');
            await awaitWriter(initial[0].controller); const a = await ensureSettlement('server', initial[0].controller);
            await command('server', 'coop.debug.authority.availability', ['yield'], true);
            await awaitWriter(initial[1].controller); await pause();
            const b = await ensureSettlement('client1', initial[1].controller);
            await command('server', 'coop.debug.authority.availability', ['auto'], true);
            await awaitWriter(initial[0].controller); await pause();
            const prepared = await Promise.all(initial.map(actor => settlementReplicas(actor.controller,
                INSTANCES[initial.indexOf(actor)])));
            return { a, b, prepared, trustedWriterOnlyNativeTownPreparation: true };
        });

        await feature('A-local-writer-sells-once', () => sell('server', 'server'));
        await feature('B-observer-routes-through-A-writer-once', () => sell('client1', 'server'));

        await feature('A-to-B-writer-handoff', async () => {
            await play();
            await command('server', 'coop.debug.authority.availability', ['yield'], true);
            const states = await awaitWriter(initial[1].controller); await pause();
            return { states, worlds: await Promise.all(INSTANCES.map(world)) };
        });
        await feature('A-observer-routes-through-B-writer-once', () => sell('server', 'client1'));
        await feature('B-local-writer-sells-once', () => sell('client1', 'client1'));

        await feature('B-to-A-writer-handoff', async () => {
            await play();
            await command('server', 'coop.debug.authority.availability', ['auto'], true);
            const states = await awaitWriter(initial[0].controller); await pause();
            return { states, worlds: await Promise.all(INSTANCES.map(world)) };
        });
        await feature('final-prisoner-sale-state', async () => {
            const receipt = await snapshot('final actors/world');
            assert.equal(summary.sales.length, 4);
            return receipt;
        });
    } catch (error) {
        summary.failed = { message: error.message, stack: error.stack };
        await emit({ phase: 'scenario_failure', ...summary.failed }); process.exitCode = 1;
        if (runId) {
            try { await call('get_run', { run_id: runId }); } catch {}
            for (const instance of INSTANCES) { try { await state(instance); } catch {} }
            try { await collectLogs(); } catch (diagnostic) { await emit({ phase: 'diagnostic_failure', error: diagnostic.stack }); }
        }
    } finally {
        if (runId) {
            try {
                const stopped = await call('stop_run', { run_id: runId }, true); summary.stopped = stopped;
                assertStopped(stopped, initial ? 2 : undefined); summary.cleanupConfirmed = true;
            } catch (error) { summary.cleanupError = { message: error.message, stack: error.stack }; process.exitCode = 1; }
            if (summary.cleanupConfirmed) {
                try { await collectLogs(true); }
                catch (error) { summary.logVerificationError = error.message; process.exitCode = 1; }
            }
        }
        try { await client.close(); }
        catch (error) { summary.transportCloseError = error.message; process.exitCode = 1; }
        summary.completedUtc = new Date().toISOString();
        summary.passed = !summary.failed && !summary.cleanupError && !summary.logVerificationError &&
            !summary.transportCloseError && summary.cleanupConfirmed;
        if (!summary.passed) process.exitCode = 1;
        await saveSummary();
        console.log(JSON.stringify({ scenario: summary.scenario, runId, passed: summary.passed,
            cleanupConfirmed: summary.cleanupConfirmed, sales: summary.sales.length, summaryPath }));
    }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    if (process.argv.includes('--help')) console.log(HELP);
    else await run(options(process.argv.slice(2)));
}
