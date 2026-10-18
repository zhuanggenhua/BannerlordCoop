import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { appendFile, mkdir, readFile, realpath, stat, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const HELP = `Usage: node tools/mcp/scenarios/p2p-recruit-training.mjs
  --repo <coordinator checkout> --server <CoopMcpServer.exe> --config <profiles.json>
  --sdk-root <node_modules> --record <new evidence.jsonl>
  [--profile local] [--save P1B_DirectIP_Recruit]

Run only from the coordinator through tools/coordination/with-game-lock.mjs.
Starts A, then B; submits four native recruitment carts once each; exercises
A -> B -> A, native NPC daily training, critical world hashes and unique Tick.
Selects an observed tavern town with existing shared stock and a four-purchase
budget; never replenishes volunteers or gold.
Town preparation uses trusted QA native lifecycle commands while that player is
the confirmed writer; it does not accept ordinary observer travel/entry.
Writes JSONL, per-feature summary JSON and BMP screenshot references. Always
requests stop_run and requires both owned process trees to be gone.
No deployment, private IPC, XP setters, or retry of uncertain mutations.
--help and --self-test [recorded.jsonl] do not import the SDK, connect, deploy or
launch games. The optional file replays recorded public MCP response wrappers.
Self-test verifies offline parsing/assertion fixtures only, not live acceptance.`;

const INSTANCES = ['server', 'client1'];
const CRITICAL_KEYS = ['partyIdentityHash', 'rosterCountsHash', 'rosterXpHash',
    'volunteerHash', 'heroGoldHash', 'nextHandle'];
const REQUIRED_COMMANDS = ['coop.debug.authority.state', 'coop.debug.authority.world_state',
    'coop.debug.authority.availability', 'coop.debug.authority.allocation_probe',
    'coop.debug.authority.training_daily', 'coop.debug.recruitment.inspect',
    'coop.debug.recruitment.submit', 'coop.debug.players.party_state',
    'coop.debug.mobile_party.character_ids', 'coop.debug.set_time_mode',
    'coop.debug.location.list', 'coop.debug.location.enter', 'coop.debug.location.leave'];

function options(argv) {
    const result = { profile: 'local', save: 'P1B_DirectIP_Recruit' };
    const allowed = new Set(['repo', 'server', 'config', 'sdk-root', 'record', 'profile', 'save']);
    for (let i = 0; i < argv.length; i += 2) {
        const key = argv[i]?.replace(/^--/, '');
        assert.ok(argv[i]?.startsWith('--') && allowed.has(key) && argv[i + 1] &&
            !argv[i + 1].startsWith('--'), `Invalid option: ${argv[i]}`);
        assert.ok(!Object.hasOwn(result, key) || key === 'profile' || key === 'save', `Duplicate option: ${key}`);
        result[key] = argv[i + 1];
    }
    for (const key of ['repo', 'server', 'config', 'sdk-root', 'record'])
        assert.ok(result[key], `Missing --${key}; use --help`);
    for (const key of ['repo', 'server', 'config', 'sdk-root', 'record']) result[key] = resolve(result[key]);
    return result;
}

function jsonOutput(text) {
    const marker = text.indexOf('LIVE_TEST_JSON=');
    return JSON.parse(marker < 0 ? text : text.slice(marker + 'LIVE_TEST_JSON='.length));
}
function field(object, camel, pascal = camel[0].toUpperCase() + camel.slice(1)) {
    return object[camel] ?? object[pascal];
}
function writer(state) { return field(state.lease, 'authorityControllerId'); }
function epoch(state) { return field(state.lease, 'epoch'); }
function leasePaused(state) { return field(state.lease, 'isPaused'); }
function counts(player) { return player.troops.reduce((sum, row) => sum + field(row, 'number'), 0); }
function memberStacks(text) {
    const member = text.split('Member roster:')[1]?.split('Prisoner roster:')[0];
    assert.ok(member !== undefined, 'character_ids did not return a member roster');
    return [...member.matchAll(/^([^:\r\n]+): number=(\d+) wounded=(\d+) xp=(\d+) hero=False\r?$/gm)]
        .map(match => ({ id: match[1], number: Number(match[2]), wounded: Number(match[3]), xp: Number(match[4]) }));
}
function normalizedStacks(rows) {
    return rows.map(row => ({ id: row.id, number: row.number, xp: row.xp }))
        .sort((a, b) => a.id.localeCompare(b.id));
}
function assertTraining(before, after, requireIncrease) {
    const left = normalizedStacks(before), right = normalizedStacks(after);
    assert.ok(left.length > 0, 'NPC has no regular troop stacks');
    assert.deepEqual(right.map(row => [row.id, row.number]), left.map(row => [row.id, row.number]),
        'Native daily training changed NPC stack membership/counts');
    let increased = false;
    for (let i = 0; i < left.length; i++) {
        assert.ok(right[i].xp >= left[i].xp, `NPC XP decreased: ${left[i].id}`);
        increased ||= right[i].xp > left[i].xp;
    }
    if (requireIncrease) assert.ok(increased, 'No regular NPC stack gained XP');
    return increased;
}
function recruitSlot(inspected, controller, sharedSlots) {
    const actor = inspected.players.find(player => player.controller === controller);
    assert.ok(actor && Number.isFinite(actor.gold), 'Recruit actor or gold missing');
    const candidates = [];
    for (const notable of inspected.volunteers ?? []) {
        for (const slot of notable.slots ?? []) {
            if (notable.id && slot.character && slot.index < notable.maximumRecruitableIndex &&
                Number.isInteger(slot.cost) && slot.cost >= 0 && slot.cost <= actor.gold &&
                (!sharedSlots || sharedSlots.some(shared => shared.nativeNotable === notable.nativeId &&
                    shared.index === slot.index && shared.character === slot.character)))
                candidates.push({ notable: notable.id, nativeNotable: notable.nativeId, ...slot });
        }
    }
    candidates.sort((a, b) => a.cost - b.cost || a.nativeNotable.localeCompare(b.nativeNotable) || a.index - b.index);
    if (candidates.length) return candidates[0];
    throw new Error(`No affordable native recruitable slot for ${controller}; no stock/gold fixture will be invented`);
}
function selectRecruitmentTown(catalog, locations, controllers) {
    assert.equal(catalog.query, 'town_catalog');
    assert.deepEqual(catalog.players.map(player => player.controller).sort(), [...controllers].sort());
    for (const player of catalog.players) assert.ok(Number.isFinite(player.gold) && player.gold >= 0);
    // A buys three troops, B buys one. This cap leaves at least one quarter of the smaller wallet.
    const maximumCost = Math.floor(Math.min(...catalog.players.map(player => player.gold)) / 4);
    assert.equal(catalog.maximumCost, maximumCost);
    assert.equal(catalog.requiredSharedSlots, 4);
    assert.equal(catalog.returnedTownCount, catalog.towns.length);
    assert.ok(catalog.towns.length <= 16 && catalog.eligibleTownCount >= catalog.towns.length &&
        catalog.scannedTownCount >= catalog.eligibleTownCount, 'Invalid bounded catalog scan metadata');
    const observedTaverns = [...locations.matchAll(/Id: '([^']+)'/g)].map(match => match[1]);
    const candidates = [];
    for (const town of catalog.towns) {
        const location = observedTaverns.find(id => id.endsWith(`_${town.id}_tavern`));
        if (!location) continue;
        const sharedSlots = [];
        for (const notable of town.notables) {
            if (!notable.id) continue;
            for (const slot of notable.slots) {
                if (!slot.character) continue;
                const eligibility = catalog.players.map(player => ({ controller: player.controller, gold: player.gold,
                    maximumRecruitableIndex: notable.players.find(value => value.controller === player.controller)?.maximumRecruitableIndex,
                    cost: slot.players.find(value => value.controller === player.controller)?.cost }));
                if (eligibility.every(value => Number.isInteger(value.maximumRecruitableIndex) &&
                    slot.index < value.maximumRecruitableIndex && Number.isInteger(value.cost) &&
                    value.cost >= 0 && value.cost <= maximumCost && value.cost <= value.gold))
                    sharedSlots.push({ nativeNotable: notable.nativeId, index: slot.index, character: slot.character, eligibility });
            }
        }
        if (sharedSlots.length >= 4)
            candidates.push({ id: town.id, location, maximumCost, availableSharedSlots: sharedSlots.length,
                headroom: sharedSlots.length - 4, sharedSlots });
    }
    candidates.sort((a, b) => b.availableSharedSlots - a.availableSharedSlots || a.id.localeCompare(b.id));
    assert.ok(candidates.length, 'No observed tavern town has four existing shared recruitable slots within the four-purchase budget');
    return candidates[0];
}
function assertRecruitCounts(before, after, controller, character) {
    assert.deepEqual(after.map(player => player.controller).sort(), before.map(player => player.controller).sort());
    for (const previous of before) {
        const current = after.find(player => player.controller === previous.controller);
        const left = new Map(previous.troops.map(row => [row.character, field(row, 'number')]));
        const right = new Map(current.troops.map(row => [row.character, field(row, 'number')]));
        const ids = new Set([...left.keys(), ...right.keys()]);
        for (const id of ids) assert.equal(right.get(id) ?? 0,
            (left.get(id) ?? 0) + (previous.controller === controller && id === character ? 1 : 0),
            `Wrong recruited troop delta for ${previous.controller}/${id}`);
        if (previous.controller === controller) assert.equal(right.get(character), (left.get(character) ?? 0) + 1);
    }
}
function criticalErrors(text) {
    return {
        realTick: (text.match(/Failed to run ["']?Campaign\.RealTick/g) ?? []).length,
        duplicateIds: (text.match(/Duplicate id:|Generated duplicate id |Duplicate object handle:/gi) ?? []).length,
        aiBatch: (text.match(/(?:AI batch.*fail|Failed.*AI batch)/gi) ?? []).length,
    };
}
function assertClientLaunched(response) {
    assert.equal(response.outcome, 'launched', JSON.stringify(response));
    assert.equal(response.instance, 'client1');
    assert.ok(response.run, 'start_client is missing its RunView');
    assert.equal(response.run.error, null, JSON.stringify(response.run));
}
function logChunk(response) {
    const chunk = response.result ?? response;
    assert.equal(typeof chunk.text, 'string', 'Missing log chunk text');
    assert.equal(typeof chunk.cursor, 'string', 'Missing log continuation cursor');
    assert.equal(typeof chunk.reset, 'boolean'); assert.equal(typeof chunk.hasMore, 'boolean');
    assert.ok(Number.isFinite(chunk.length) && chunk.length >= 0);
    return chunk;
}
function assertStopped(response, expectedCount) {
    assert.equal(response.state, 'stopped');
    assert.ok(response.instances?.length > 0, 'Missing owned game instance cleanup receipt');
    if (expectedCount !== undefined)
        assert.equal(response.instances.length, expectedCount, 'Missing game instance cleanup receipt');
    assert.ok(response.instances.every(value => value.cleanupComplete === true &&
        value.processAlive === false && value.processTreeAlive === false));
}
function assertNativeMap(view, instance) {
    const status = view.instances?.find(value => value.name === instance)?.status;
    assert.ok(status, `Missing native state for ${instance}`);
    assert.equal(status.missionActive, false, `${instance} still has a native Mission`);
    assert.equal(status.activeState, 'TaleWorlds.CampaignSystem.GameState.MapState',
        `${instance} has not returned to native MapState`);
    return status;
}
function assertSettlementReplicas(inspections, controller, settlement) {
    return inspections.map((value, index) => {
        const player = value.players.find(player => player.controller === controller);
        assert.ok(player, `${INSTANCES[index]} is missing player ${controller}`);
        assert.equal(player.currentSettlement, settlement,
            `${INSTANCES[index]} has not applied ${controller} settlement ${settlement}`);
        return player;
    });
}
function assertActors(states, initial) {
    for (let i = 0; i < states.length; i++) {
        assert.equal(states[i].mainHero, initial[i].mainHero, `${INSTANCES[i]} native MainHero changed`);
        assert.equal(states[i].mainParty, initial[i].mainParty, `${INSTANCES[i]} native MainParty changed`);
        assert.equal(states[i].controller, initial[i].controller);
        assert.equal(states[i].roomHost, initial[0].controller);
    }
}
function assertWorld(worlds) {
    assert.equal(worlds[0].unregisteredParties, 0);
    assert.equal(worlds[1].unregisteredParties, 0);
    assert.equal(worlds[0].campaign, worlds[1].campaign);
    for (const key of CRITICAL_KEYS) {
        assert.notEqual(worlds[0][key], undefined, `Missing critical field ${key}`);
        assert.equal(worlds[0][key], worlds[1][key], `Critical world mismatch: ${key}`);
    }
    assert.equal(worlds[0].ticks, worlds[1].ticks, 'Stopped native clock differs');
}

async function run(config) {
    assert.ok((await stat(join(config.repo, '.git'))).isDirectory(), 'Live scenario requires coordinator main checkout');
    assert.equal(await realpath(execFileSync('git', ['rev-parse', '--show-toplevel'],
        { cwd: config.repo, encoding: 'utf8', windowsHide: true }).trim()), await realpath(config.repo));
    for (const path of [config.server, config.config]) assert.ok((await stat(path)).isFile(), `Missing file: ${path}`);
    const sdk = join(config['sdk-root'], '@modelcontextprotocol/sdk/dist/esm/client');
    const [{ Client }, { StdioClientTransport }] = await Promise.all([
        import(pathToFileURL(join(sdk, 'index.js')).href), import(pathToFileURL(join(sdk, 'stdio.js')).href),
    ]);
    await mkdir(dirname(config.record), { recursive: true });
    const summaryPath = config.record + '.summary.json';
    await writeFile(config.record, '', { flag: 'wx' });
    await writeFile(summaryPath, '{}', { flag: 'wx' });
    const summary = { scenario: 'p2p-recruit-training', startedUtc: new Date().toISOString(),
        codeHead: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: config.repo, encoding: 'utf8', windowsHide: true }).trim(),
        config, runId: null, features: [], screenshots: [], probes: [], npcAttempts: [],
        failed: null, cleanupConfirmed: false, passed: false,
        scope: 'Trusted QA native town preparation under the confirmed writer, native RecruitmentVM and one native NPC daily callback; not ordinary observer travel/entry, a full natural daily cycle, battle, or cold-save acceptance.' };
    let runId, callId = 0, initial, readDeadline, recruitmentTown;
    const client = new Client({ name: 'codex-p2p-recruit-training', version: '1.0.0' });
    const transport = new StdioClientTransport({ command: config.server, args: ['--config', config.config],
        cwd: config.repo, stderr: 'inherit', maxBufferSize: 16 * 1024 * 1024 });
    const logState = Object.fromEntries(INSTANCES.map(instance => [instance, { text: '', cursor: undefined }]));
    const emit = entry => appendFile(config.record, JSON.stringify({ utc: new Date().toISOString(), ...entry }) + '\n');
    const saveSummary = () => writeFile(summaryPath, JSON.stringify(summary, null, 2) + '\n');

    async function call(name, args, mutation = false) {
        assert.ok(!(readDeadline && mutation), 'Mutations cannot run in a polling callback');
        const remaining = readDeadline ? readDeadline - Date.now() : 360000;
        assert.ok(remaining > 0, 'Read-only convergence deadline expired');
        const id = ++callId;
        await emit({ phase: 'request', callId: id, name, args, mutation });
        let raw;
        try { raw = await client.callTool({ name, arguments: args }, undefined, { timeout: remaining }); }
        catch (error) {
            await emit({ phase: 'transport_error', callId: id, name, error: error.stack,
                outcomeUncertain: mutation, noMutationRetry: mutation });
            throw error;
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
    async function result(instance, name, args = [], mutation = false) {
        const response = await call('execute_command', { run_id: runId, instance, name, arguments: args }, mutation);
        assert.equal(response.ok, true, JSON.stringify(response));
        assert.ok(response.result, 'Missing command result');
        return response.result;
    }
    async function command(instance, name, args = [], mutation = false) {
        const response = await result(instance, name, args, mutation);
        assert.equal(response.succeeded, true, JSON.stringify(response));
        return response.output;
    }
    const state = instance => command(instance, 'coop.debug.authority.state').then(jsonOutput);
    const world = instance => command(instance, 'coop.debug.authority.world_state').then(jsonOutput);
    const inspect = instance => command(instance, 'coop.debug.recruitment.inspect').then(jsonOutput);
    const playerState = (instance, controller) => command(instance, 'coop.debug.players.party_state', [controller]).then(jsonOutput);
    const bothStates = () => Promise.all(INSTANCES.map(state));
    async function poll(label, readAndAssert, timeout = 30000) {
        const deadline = Date.now() + timeout;
        let lastError;
        do {
            const previousDeadline = readDeadline;
            try { readDeadline = deadline; return await readAndAssert(); }
            catch (error) {
                // Poll only read/assert callbacks. Mutating commands never run in this retry loop.
                lastError = error;
                await emit({ phase: 'poll_pending', label, reason: error.message });
            }
            finally { readDeadline = previousDeadline; }
            await delay(500);
        } while (Date.now() < deadline);
        throw new Error(`${label} did not converge: ${lastError?.message}`, { cause: lastError });
    }
    async function awaitWriter(controller) {
        return poll('confirmed writer ' + controller, async () => {
            const states = await bothStates();
            for (const value of states) {
                assert.equal(writer(value), controller); assert.equal(leasePaused(value), false);
                assert.equal(value.frozen, false); assert.equal(value.localAuthority, value.controller === controller);
            }
            assert.equal(epoch(states[0]), epoch(states[1]));
            if (initial) assertActors(states, initial);
            return states;
        });
    }
    async function registrations() {
        const pair = [];
        for (const instance of INSTANCES) {
            const entries = [];
            for (const actor of initial) {
                const entry = await playerState(instance, actor.controller);
                assert.equal(entry.active, true); assert.equal(entry.partyRegistrations, 1);
                assert.equal(entry.heroRegistrations, 1); assert.equal(entry.clanRegistrations, 1);
                assert.equal(entry.registeredPlayers, 2);
                if (instance === 'server') assert.equal(entry.mode, 'ListenAuthority');
                assert.equal(entry.heroStringId, actor.mainHero); assert.equal(entry.partyStringId, actor.mainParty);
                entries.push(entry);
            }
            pair.push(entries);
        }
        for (let i = 0; i < 2; i++) {
            assert.equal(pair[0][i].heroId, pair[1][i].heroId); assert.equal(pair[0][i].partyId, pair[1][i].partyId);
            assert.equal(pair[0][i].troops, pair[1][i].troops); assert.equal(pair[0][i].gold, pair[1][i].gold);
        }
        assertActors(await bothStates(), initial);
        return pair;
    }
    async function stableWorld(label) {
        return poll(label, async () => {
            const worlds = await Promise.all(INSTANCES.map(world));
            assertWorld(worlds); await registrations();
            return worlds;
        });
    }
    async function pause() {
        await command('server', 'coop.debug.set_time_mode', ['Pause', 'force-live-test'], true);
        return stableWorld('stopped critical world');
    }
    async function waitState(instance, value, seconds = 300) {
        const response = await call('wait_for_state', { run_id: runId, instance, state: value, timeout_seconds: seconds });
        assert.equal((response.result ?? response).reached, true, JSON.stringify(response));
    }
    async function screenshot(instance, label) {
        const requested = await call('screenshot', { run_id: runId, instance }, true);
        assert.equal(requested.ok, true, JSON.stringify(requested));
        const id = requested.result?.captureId;
        assert.ok(id, 'Missing screenshot captureId; request will not be retried');
        const capture = await poll('BMP capture ' + id, async () => {
            const response = await call('screenshot_status', { run_id: runId, instance, capture_id: id });
            assert.equal(response.ok, true); assert.equal(response.result.complete, true);
            assert.ok(response.result.isBmp && response.result.headerValid && response.result.stable);
            assert.ok(response.result.path && response.result.sha256);
            return response.result;
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
                const chunk = logChunk(response);
                if (chunk.reset) {
                    assert.ok(archived, 'Live log replaced or truncated; whole-run evidence is incomplete');
                    log.text = '';
                }
                log.text += chunk.text; log.cursor = chunk.cursor;
                if (!chunk.hasMore) break;
                assert.ok(page < 1023, 'Log page budget exhausted; evidence incomplete');
            }
            const path = config.record + '.' + instance + '.log';
            await writeFile(path, log.text);
            const critical = criticalErrors(log.text);
            summary.logs ??= {};
            summary.logs[instance] = { path, sha256: createHash('sha256').update(log.text).digest('hex'), critical };
            assert.deepEqual(Object.values(critical), [0, 0, 0], `${instance} critical errors: ${JSON.stringify(critical)}`);
            } catch (error) {
                errors.push(`${instance}: ${error.message}`);
                await emit({ phase: 'log_evidence_error', instance, error: error.stack });
            }
        }
        assert.deepEqual(errors, [], 'Log capture/critical error verification failed');
    }
    async function feature(name, action) {
        const startedUtc = new Date().toISOString();
        const evidence = await action();
        await collectLogs();
        for (const instance of INSTANCES) await screenshot(instance, name);
        summary.features.push({ name, status: 'PASS', startedUtc, completedUtc: new Date().toISOString(), evidence });
        await emit({ phase: 'feature_pass', name, evidence }); await saveSummary();
    }
    async function tickForFiveSeconds(instance) {
        const other = instance === 'server' ? 'client1' : 'server';
        await command('server', 'coop.debug.set_time_mode', ['Play_1x', 'force-live-test'], true);
        await awaitWriter(initial[INSTANCES.indexOf(instance)].controller);
        const before = { writer: await state(instance), observer: await state(other) };
        await delay(5000);
        const after = { writer: await state(instance), observer: await state(other) };
        assert.equal(epoch(after.writer), epoch(before.writer));
        assert.ok(after.writer.worldTicks > before.writer.worldTicks, 'Writer native Campaign.Tick did not advance');
        assert.ok(after.writer.hours > before.writer.hours, 'Writer world time did not advance');
        assert.equal(after.observer.worldTicks, before.observer.worldTicks, 'Observer advanced Campaign.Tick');
        const worlds = await pause();
        return { before, after, worlds };
    }
    async function settlementReplicas(instance, settlement, label) {
        const controller = initial[INSTANCES.indexOf(instance)].controller;
        return poll(label, async () => {
            const values = await Promise.all(INSTANCES.map(inspect));
            assertSettlementReplicas(values, controller, settlement);
            assert.equal(values[INSTANCES.indexOf(instance)].settlement, settlement);
            await registrations();
            return values;
        });
    }
    async function leavePreviousSettlement(selected) {
        await awaitWriter(initial[0].controller);
        const current = await inspect('client1');
        if (current.settlement === selected.id) {
            return { alreadyInside: true, replicas: await settlementReplicas('client1', selected.id,
                'B selected native settlement on both replicas') };
        }
        if (current.settlement)
            await command('client1', 'coop.debug.siege.leave_settlement', [], true);
        return { previousSettlement: current.settlement, replicas: await settlementReplicas('client1', null,
            'B leaves its old native settlement on both replicas') };
    }
    async function ensureSettlement(instance, selected) {
        const { id: townId, location } = selected;
        await awaitWriter(initial[INSTANCES.indexOf(instance)].controller);
        const current = await inspect(instance);
        if (current.settlement === townId) return { alreadyInside: true, townId,
            replicas: await settlementReplicas(instance, townId, 'existing native settlement ' + instance) };
        assert.equal(current.settlement, null, 'Previous native settlement must be left before town preparation');
        const firstEntry = await command(instance, 'coop.debug.location.enter', [location], true);
        let view = await call('get_run', { run_id: runId });
        let mission = firstEntry.startsWith('Opening location') ||
            view.instances.find(value => value.name === instance)?.status?.missionActive;
        if (!mission && (await inspect(instance)).settlement !== townId) {
            // First call may initialize only PlayerEncounter; a second call opens the observed location.
            const secondEntry = await command(instance, 'coop.debug.location.enter', [location], true);
            view = await call('get_run', { run_id: runId });
            mission = secondEntry.startsWith('Opening location') ||
                view.instances.find(value => value.name === instance)?.status?.missionActive;
        }
        if (mission) {
            await waitState(instance, 'readyForMissionTests');
            await command(instance, 'coop.debug.location.leave', [], true);
            // Campaign readiness also remains true inside MissionState; observe native return explicitly.
            await poll('native MapState after location leave ' + instance, async () =>
                assertNativeMap(await call('get_run', { run_id: runId }), instance));
        }
        return { townId, replicas: await settlementReplicas(instance, townId, 'native settlement ' + instance) };
    }
    async function recruit(instance, label, localAuthority) {
        const before = await Promise.all(INSTANCES.map(inspect));
        const controller = initial[INSTANCES.indexOf(instance)].controller;
        assert.equal(before[INSTANCES.indexOf(instance)].localAuthority, localAuthority);
        const slot = recruitSlot(before[INSTANCES.indexOf(instance)], controller, recruitmentTown.sharedSlots);
        const baseline = new Map(before[0].players.map(player => [player.controller, { gold: player.gold, troops: counts(player) }]));
        const output = jsonOutput(await command(instance, 'coop.debug.recruitment.submit', [slot.notable, String(slot.index)], true));
        assert.equal(output.status, 'submitted'); assert.equal(output.localAuthority, localAuthority);
        assert.equal(output.cart.length, 1);
        const cost = field(output.cart[0], 'cost');
        assert.equal(cost, slot.cost); assert.equal(output.cart[0].character, slot.character);
        assert.equal(field(output.cart[0], 'index'), slot.index);
        if (!localAuthority) {
            assert.equal(output.goldAfterLocal, output.goldBefore, 'Observer changed gold before authoritative result');
            assert.equal(output.troopsAfterLocal, output.troopsBefore, 'Observer applied native recruitment locally');
        } else {
            assert.equal(output.goldAfterLocal, output.goldBefore - cost);
            assert.equal(output.troopsAfterLocal, output.troopsBefore + 1);
        }
        const after = await poll(label + ' single authoritative application', async () => {
            const values = await Promise.all(INSTANCES.map(inspect));
            for (const value of values) {
                assertRecruitCounts(before[0].players, value.players, controller, slot.character);
                for (const player of value.players) {
                    const old = baseline.get(player.controller); assert.ok(old);
                    const target = player.controller === controller;
                    assert.equal(player.gold, old.gold - (target ? cost : 0));
                    assert.equal(counts(player), old.troops + (target ? 1 : 0));
                }
                const notable = value.volunteers.find(item => item.nativeId === slot.nativeNotable);
                assert.ok(notable, 'Selected native notable missing on replica');
                assert.equal(notable.slots.find(item => item.index === slot.index)?.character, null,
                    'Volunteer stock slot was not consumed on replica');
            }
            await registrations(); return values;
        });
        return { instance, controller, slot, cart: output, before, after, worlds: await stableWorld(label + ' world') };
    }
    async function stacks(instance, hero) {
        return memberStacks(await command(instance, 'coop.debug.mobile_party.character_ids', [hero]));
    }
    async function syncNpc(npc, expected) {
        return poll('NPC XP ' + npc.id, async () => {
            const values = await Promise.all(INSTANCES.map(instance => stacks(instance, npc.hero)));
            for (const rows of values) assert.deepEqual(normalizedStacks(rows), normalizedStacks(expected));
            return values;
        });
    }
    async function allocationProbe(instance) {
        const other = instance === 'server' ? 'client1' : 'server';
        const created = jsonOutput(await command(instance, 'coop.debug.authority.allocation_probe', ['create'], true));
        const observed = await poll('probe registered ' + created.id, async () => {
            const value = jsonOutput(await command(other, 'coop.debug.authority.allocation_probe', ['inspect', created.id]));
            assert.equal(value.handle, created.handle); return value;
        });
        const prior = summary.probes.at(-1);
        const ordinal = Number(created.id.match(/(\d+)$/)?.[1]);
        assert.ok(Number.isInteger(ordinal));
        if (prior) { assert.ok(created.handle > prior.handle); assert.ok(ordinal > prior.ordinal); }
        await command(instance, 'coop.debug.authority.allocation_probe', ['retire', created.id], true);
        await poll('retired probe absent', async () => {
            for (const endpoint of INSTANCES) {
                const value = await result(endpoint, 'coop.debug.authority.allocation_probe', ['inspect', created.id]);
                assert.equal(value.succeeded, false); assert.equal(value.errorCode, 'probe_absent');
            }
        });
        summary.probes.push({ instance, ...created, ordinal, observed });
        return created;
    }

    try {
        await client.connect(transport);
        const started = await call('start_run', { profile: config.profile, client_count: 0, save_name: config.save }, true);
        assert.ok(runId); assert.equal(started.error, null);
        await waitState('server', 'readyForCampaignTests');
        await delay(1000); // A is loaded before a second game process is launched.
        const launched = await call('start_client', { run_id: runId, client_index: 1 }, true);
        assertClientLaunched(launched);
        await waitState('client1', 'readyToJoin', 180);
        const joined = await call('join_client', { run_id: runId, instance: 'client1' }, true);
        assert.equal(joined.ok, true); await waitState('client1', 'readyForCampaignTests');
        initial = await bothStates(); assert.notEqual(initial[0].controller, initial[1].controller);
        for (const value of initial) assert.ok(value.mainHero && value.mainParty);
        for (const instance of INSTANCES) {
            const catalog = await call('list_commands', { run_id: runId, instance });
            const text = JSON.stringify(catalog);
            const required = instance === 'server' ? REQUIRED_COMMANDS :
                [...REQUIRED_COMMANDS.filter(name => name !== 'coop.debug.set_time_mode'), 'coop.debug.siege.leave_settlement'];
            for (const name of required) assert.ok(text.includes(name), `${instance} lacks ${name}`);
        }
        await feature('join-single-native-actors', async () => {
            await awaitWriter(initial[0].controller);
            const actors = await poll('active unique player registrations', registrations);
            const view = await call('get_run', { run_id: runId });
            assert.equal(view.instances.length, 2, 'Expected exactly two owned player processes');
            const [a, b] = INSTANCES.map(name => view.instances.find(value => value.name === name));
            assert.ok(a?.processAlive && b?.processAlive);
            assert.ok(a.identity.pid > 0 && b.identity.pid > 0 && a.identity.pid !== b.identity.pid);
            assert.equal(a.status.buildVersion, b.status.buildVersion);
            const assemblies = instance => instance.status.loadedAssemblies.map(value => [value.name, value.mvid])
                .sort((left, right) => left[0].localeCompare(right[0]));
            assert.deepEqual(assemblies(a), assemblies(b), 'Player game processes loaded different mod assemblies');
            return { actors, processes: view.instances };
        });
        await feature('A-only-world-tick', async () => {
            assert.ok(!(await inspect('server')).settlement,
                'Control save must start A outside settlement for the native advancing-clock test');
            const tick = await tickForFiveSeconds('server');
            return { tick, probe: await allocationProbe('server'), worlds: await stableWorld('A first allocation') };
        });
        const locations = await command('server', 'coop.debug.location.list');
        await feature('native-settlement-preparation', async () => {
            await pause();
            const catalog = jsonOutput(await command('server', 'coop.debug.recruitment.inspect', ['town_catalog']));
            recruitmentTown = selectRecruitmentTown(catalog, locations, initial.map(value => value.controller));
            summary.recruitmentTown = recruitmentTown;
            const coverage = { scannedTownCount: catalog.scannedTownCount, eligibleTownCount: catalog.eligibleTownCount,
                returnedTownCount: catalog.returnedTownCount, limited: catalog.limited };
            await emit({ phase: 'existing-stock-preflight', coverage, players: catalog.players, selected: recruitmentTown });
            const previousB = await leavePreviousSettlement(recruitmentTown);
            const prepared = [await ensureSettlement('server', recruitmentTown)];
            await command('server', 'coop.debug.authority.availability', ['yield'], true);
            const bWriter = await awaitWriter(initial[1].controller);
            await pause();
            prepared.push(await ensureSettlement('client1', recruitmentTown));
            await command('server', 'coop.debug.authority.availability', ['auto'], true);
            const aWriter = await awaitWriter(initial[0].controller);
            const worlds = await pause();
            return { selected: recruitmentTown, coverage, players: catalog.players, previousB,
                prepared, bWriter, aWriter, worlds, trustedQaLifecyclePreparation: true };
        });
        await feature('A-writer-native-recruit-1', () => recruit('server', 'A writer recruit', true));
        await feature('A-to-B-final-checkpoint', async () => {
            await command('server', 'coop.debug.authority.availability', ['yield'], true);
            const states = await awaitWriter(initial[1].controller);
            return { states, worlds: await pause(), probe: await allocationProbe('client1') };
        });
        await feature('A-observer-native-recruit-2', () => recruit('server', 'A observer recruit', false));
        await feature('B-writer-native-recruit-3', () => recruit('client1', 'B writer recruit', true));
        let trainedNpc;
        await feature('B-native-NPC-daily-training', async () => {
            const worlds = await stableWorld('before NPC training');
            const playerParties = new Set(initial.map(value => value.mainParty));
            const candidates = worlds[1].npcSample.filter(npc => npc.hero && !playerParties.has(npc.id) &&
                (/^lord_/i.test(npc.id) || /^lord_/i.test(npc.hero)));
            assert.ok(candidates.length, 'No regular lord NPC in observed sample; no NPC fixture will be invented');
            for (const npc of candidates) {
                const before = await stacks('client1', npc.hero);
                if (!before.length) continue;
                const rejected = await result('server', 'coop.debug.authority.training_daily', [npc.id], true);
                assert.equal(rejected.succeeded, false); assert.equal(rejected.errorCode, 'not_world_writer');
                const callback = jsonOutput(await command('client1', 'coop.debug.authority.training_daily', [npc.id], true));
                assert.equal(callback.nativeCallback, 'OnDailyTickParty');
                assert.deepEqual(normalizedStacks(callback.before), normalizedStacks(before));
                const increased = assertTraining(callback.before, callback.after, false);
                await syncNpc(npc, callback.after);
                summary.npcAttempts.push({ npc, increased, callback }); await saveSummary();
                if (increased) { trainedNpc = npc; return { npc, rejected, callback, worlds: await stableWorld('NPC training world') }; }
                // A completed zero-gain callback permits trying a different NPC, never resubmitting this callback.
            }
            throw new Error('Every completed native NPC callback produced zero XP; positive training acceptance is unverified');
        });
        await feature('B-only-world-tick', async () => {
            // B must leave its native town menu; A may remain inside as an observer for recruit 4.
            if ((await inspect('client1')).settlement) {
                await command('client1', 'coop.debug.siege.leave_settlement', [], true);
                await poll('B leaves native settlement', async () => assert.equal((await inspect('client1')).settlement, null));
            }
            return tickForFiveSeconds('client1');
        });
        await feature('B-to-A-XP-and-number-continuity', async () => {
            const before = await stacks('client1', trainedNpc.hero); await syncNpc(trainedNpc, before);
            await command('server', 'coop.debug.authority.availability', ['auto'], true);
            const states = await awaitWriter(initial[0].controller);
            await syncNpc(trainedNpc, before);
            return { states, npc: trainedNpc, xpBeforeHandoff: before, probe: await allocationProbe('server'),
                worlds: await stableWorld('A returned checkpoint') };
        });
        await feature('A-returned-native-recruit-4', () => recruit('server', 'A returned recruit', true));
        await feature('A-native-NPC-daily-training', async () => {
            const before = await stacks('server', trainedNpc.hero);
            const callback = jsonOutput(await command('server', 'coop.debug.authority.training_daily', [trainedNpc.id], true));
            assert.deepEqual(normalizedStacks(callback.before), normalizedStacks(before));
            assertTraining(callback.before, callback.after, true); await syncNpc(trainedNpc, callback.after);
            return { npc: trainedNpc, callback, worlds: await stableWorld('A daily training world') };
        });
        await feature('final-critical-world-and-actors', async () => ({
            states: await awaitWriter(initial[0].controller), worlds: await pause(), registrations: await registrations(),
        }));
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
                assertStopped(stopped, initial ? 2 : undefined);
                summary.cleanupConfirmed = true;
            } catch (error) {
                summary.cleanupError = { message: error.message, stack: error.stack }; process.exitCode = 1;
            }
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
            cleanupConfirmed: summary.cleanupConfirmed, features: summary.features.length, summaryPath }));
    }
}

async function selfTest(recordedPath) {
    assert.deepEqual(jsonOutput('ready\nLIVE_TEST_JSON={"active":true}'), { active: true });
    assert.equal(field({ Cost: 12 }, 'cost'), 12);
    const parsed = memberStacks('## Lord\nMember roster:\nunit: number=4 wounded=1 xp=7 hero=False\nhero: number=1 wounded=0 xp=0 hero=True\nPrisoner roster:\nprisoner: number=3 wounded=0 xp=2 hero=False\n');
    assert.deepEqual(parsed, [{ id: 'unit', number: 4, wounded: 1, xp: 7 }]);
    assertTraining(parsed, [{ ...parsed[0], xp: 8 }], true);
    assert.equal(assertTraining(parsed, parsed, false), false);
    assert.throws(() => assertTraining(parsed, [{ ...parsed[0], number: 5 }], true));
    assert.throws(() => assertTraining(parsed, [{ ...parsed[0], xp: 6 }], false));
    assert.throws(() => assertTraining(parsed, parsed, true));
    const inspected = { players: [{ controller: 'A', gold: 10 }], volunteers: [{ id: 'Hero_n',
        nativeId: 'n', maximumRecruitableIndex: 2, slots: [
            { index: 0, character: 'expensive', cost: 11 }, { index: 1, character: 'unit', cost: 10 },
            { index: 2, character: 'locked', cost: 1 }] }] };
    assert.equal(recruitSlot(inspected, 'A').index, 1);
    assert.throws(() => recruitSlot({ ...inspected, volunteers: [] }, 'A'));
    const affordable = { ...inspected, volunteers: [{ ...inspected.volunteers[0], maximumRecruitableIndex: 3,
        slots: [{ index: 0, character: 'expensive', cost: 9 }, { index: 1, character: 'cheap', cost: 2 }] }] };
    assert.equal(recruitSlot(affordable, 'A').character, 'cheap');
    assert.equal(recruitSlot(affordable, 'A', [{ nativeNotable: 'n', index: 0, character: 'expensive' }]).character, 'expensive');
    const town = (id, count, cost = 20, maximum = 6) => ({ id, notables: [{ id: 'Hero_' + id,
        nativeId: id + '_notable', players: ['A', 'B'].map(controller => ({ controller, maximumRecruitableIndex: maximum })),
        slots: Array.from({ length: count }, (_, index) => ({ index, character: 'unit',
            players: ['A', 'B'].map(controller => ({ controller, cost })) })) }] });
    const catalog = { query: 'town_catalog', players: [{ controller: 'A', gold: 1000 }, { controller: 'B', gold: 920 }],
        maximumCost: 230, requiredSharedSlots: 4, scannedTownCount: 50, eligibleTownCount: 5, returnedTownCount: 5,
        towns: [town('locked', 6, 20, 3), town('expensive', 6, 240), town('unobserved', 6), town('just_four', 4), town('stocked', 6)] };
    const locations = ['locked', 'expensive', 'just_four', 'stocked'].map(id => `Id: 'Location_${id}_tavern'`).join('\n');
    const selected = selectRecruitmentTown(catalog, locations, ['A', 'B']);
    assert.equal(selected.id, 'stocked'); assert.equal(selected.availableSharedSlots, 6); assert.equal(selected.headroom, 2);
    assert.throws(() => selectRecruitmentTown({ ...catalog, towns: [town('locked', 6, 20, 3)], returnedTownCount: 1 },
        locations, ['A', 'B']));
    const world = { campaign: 'c', unregisteredParties: 0, ticks: 1,
        ...Object.fromEntries(CRITICAL_KEYS.map(key => [key, key === 'nextHandle' ? 10 : 'hash'])) };
    assertWorld([world, { ...world }]);
    assert.throws(() => assertWorld([world, { ...world, rosterXpHash: 'diverged' }]));
    const players = [{ controller: 'A', troops: [{ character: 'unit', Number: 4 }] },
        { controller: 'B', troops: [{ character: 'unit', Number: 2 }] }];
    assertRecruitCounts(players, [{ ...players[0], troops: [{ character: 'unit', Number: 5 }] }, players[1]], 'A', 'unit');
    assert.throws(() => assertRecruitCounts(players, players, 'A', 'unit'));
    assert.deepEqual(criticalErrors('Failed to run "Campaign.RealTick"\nDuplicate id: x\nFailed to tick mobile-party AI batch'),
        { realTick: 1, duplicateIds: 1, aiBatch: 1 });
    assert.throws(() => options(['--repo', 'r', '--unknown', 'x']));
    // Recorded API shape: StartClientResult wraps RunView, LogChunk and stopped RunView are flat.
    const launched = { outcome: 'launched', instance: 'client1', run: { error: null } };
    assertClientLaunched(launched);
    assertClientLaunched({ ...launched, error: null }); // Some server serializers retain this optional outer field.
    assert.throws(() => assertClientLaunched({ ...launched, outcome: 'not-launched' }));
    assert.throws(() => assertClientLaunched({ ...launched, run: { error: 'launch failed' } }));
    const stopped = { state: 'stopped', instances: INSTANCES.map(name => ({ name,
        processAlive: false, processTreeAlive: false, cleanupComplete: true })) };
    assertStopped(stopped, 2);
    assert.throws(() => assertStopped({ ...stopped, instances: [{ processAlive: true }] }));
    const mapView = { instances: [{ name: 'client1', status: { readyForCampaignTests: true,
        missionActive: false, activeState: 'TaleWorlds.CampaignSystem.GameState.MapState' } }] };
    assertNativeMap(mapView, 'client1');
    const missionView = { instances: [{ name: 'client1', status: { readyForCampaignTests: true,
        missionActive: true, activeState: 'TaleWorlds.MountAndBlade.MissionState' } }] };
    assert.throws(() => assertNativeMap(missionView, 'client1'));
    assert.throws(() => assertNativeMap({ instances: [] }, 'client1'));
    const leftTown = { players: [{ controller: 'B', currentSettlement: null }] };
    assertSettlementReplicas([leftTown, leftTown], 'B', null);
    assert.throws(() => assertSettlementReplicas([
        { players: [{ controller: 'B', currentSettlement: 'old_town' }] }, leftTown], 'B', null));
    assert.equal(logChunk({ text: 'log', cursor: 'opaque', reset: false, hasMore: false, length: 3 }).text, 'log');
    assert.throws(() => logChunk({ text: 'log', hasMore: false }));
    if (recordedPath) {
        const replayed = { start_client: 0, stop_run: 0, read_logs: 0, recruitmentInspect: 0,
            nativeRecruitmentCart: 0, nativeMap: 0, campaignReadyWhileMission: 0 };
        for (const line of (await readFile(resolve(recordedPath), 'utf8')).split(/\r?\n/).filter(Boolean)) {
            const entry = JSON.parse(line);
            if (!entry.data) continue;
            if (entry.name === 'start_client') { assertClientLaunched(entry.data); replayed.start_client++; }
            if (entry.name === 'stop_run') { assertStopped(entry.data); replayed.stop_run++; }
            if (entry.name === 'read_logs') { logChunk(entry.data); replayed.read_logs++; }
            if (entry.name === 'get_run') for (const instance of entry.data.instances ?? []) {
                if (instance.status?.activeState === 'TaleWorlds.CampaignSystem.GameState.MapState' &&
                    instance.status.missionActive === false) {
                    assertNativeMap(entry.data, instance.name); replayed.nativeMap++;
                }
                if (instance.status?.readyForCampaignTests === true && instance.status.missionActive === true) {
                    assert.throws(() => assertNativeMap(entry.data, instance.name)); replayed.campaignReadyWhileMission++;
                }
            }
            if (entry.name === 'execute_command' && entry.data.result?.succeeded &&
                entry.args?.name === 'coop.debug.recruitment.inspect') {
                const inspected = jsonOutput(entry.data.result.output);
                assert.ok(Array.isArray(inspected.players));
                if (!inspected.query) for (const player of inspected.players) assert.ok(Number.isInteger(counts(player)));
                replayed.recruitmentInspect++;
            }
            if (entry.name === 'execute_command' && entry.data.result?.succeeded &&
                entry.args?.name === 'coop.debug.recruitment.submit') {
                const submitted = jsonOutput(entry.data.result.output);
                assert.equal(submitted.status, 'submitted'); assert.equal(submitted.cart.length, 1);
                assert.ok(Number.isInteger(field(submitted.cart[0], 'cost')));
                assert.ok(Number.isInteger(field(submitted.cart[0], 'index')));
                replayed.nativeRecruitmentCart++;
            }
        }
        assert.ok(replayed.start_client > 0 && replayed.stop_run > 0, 'Recorded response wrappers are incomplete');
        console.log(JSON.stringify({ offlineRecordedWrapperReplay: replayed, record: resolve(recordedPath) }));
    }
    console.log('PASS offline parser/assertion self-test; no SDK, transport, game, or live scenario executed.');
}

try {
    const argv = process.argv.slice(2);
    if (argv.length === 1 && argv[0] === '--help') console.log(HELP);
    else if (argv[0] === '--self-test' && argv.length <= 2) await selfTest(argv[1]);
    else await run(options(argv));
} catch (error) {
    console.error(error.stack); process.exitCode = 1;
}
