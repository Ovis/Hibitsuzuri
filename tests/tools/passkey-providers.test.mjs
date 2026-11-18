import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { updateProviders } from '../../tools/update-passkey-providers.mjs';

const firstId = 'bada5566-a7aa-401f-bd96-45619a55120d';
const secondId = 'ea9b8d66-4d01-1d21-3ce4-b6b48cb575d4';
const thirdId = 'd548826e-79b4-db40-a3d8-11116f7e8349';
const fourthId = '11111111-2222-3333-4444-555555555555';
const now = new Date('2026-11-16T00:17:00.000Z');

/** 各試験が所有する一時領域へ保存先と固定一覧を用意する。 */
async function fixture(t, current = { [firstId]: 'Old name', [secondId]: 'Fallback only' }) {
    const directory = await mkdtemp(join(tmpdir(), 'hibitsuzuri-provider-test-'));
    t.after(() => rm(directory, { recursive: true, force: true }));
    const targetPath = join(directory, 'providers.json');
    const fallbackPath = join(directory, 'fallback.json');
    const cachePath = join(directory, 'cache.json');
    const fallback = {
        source: 'fixed-community-snapshot',
        retrievedOn: '2026-11-16',
        providers: { [firstId]: 'Old name', [secondId]: 'Fallback only' }
    };

    await writeFile(targetPath, JSON.stringify({ retrievedOn: '2026-11-01', providers: current }));
    await writeFile(fallbackPath, JSON.stringify(fallback));

    return { targetPath, fallbackPath, cachePath, now };
}

/** アイコン付きのC-MDS応答を作り、表示名だけを抽出できるか確認する。 */
function metadata(serial = 10, providers = { [firstId]: '1Password', [thirdId]: 'Bitwarden' }) {
    return {
        no: serial,
        ...Object.fromEntries(Object.entries(providers).map(([id, name]) => [id, {
            friendlyNames: { 'en-US': name },
            providerLogoLight: 'data:ignored-icon'
        }]))
    };
}

/** 通信条件付き取得と24時間の境界を検証するため、成功済みキャッシュを用意する。 */
async function seedCache(options, checkedAt = '2026-11-15T00:17:00.000Z', serial = 10) {
    const cache = {
        source: 'https://c-mds.fidoalliance.org/',
        checkedAt,
        serial,
        providers: { [firstId]: 'Old name' }
    };
    await writeFile(options.cachePath, JSON.stringify(cache));
}

test('C-MDSを優先し、旧一覧のみの名前を補完して、新規追加と廃止を反映する', async t => {
    const options = await fixture(t, { [firstId]: 'Old name', [secondId]: 'Fallback only', [fourthId]: 'Removed C-MDS entry' });

    const result = await updateProviders({
        ...options,
        fetchImpl: async url => {
            assert.equal(url.href, 'https://c-mds.fidoalliance.org/');

            return Response.json(metadata());
        }
    });

    assert.equal(result.changed, true);
    const text = await readFile(options.targetPath, 'utf8');
    const updated = JSON.parse(text);
    assert.deepEqual(updated.providers, { [firstId]: '1Password', [thirdId]: 'Bitwarden', [secondId]: 'Fallback only' });
    assert.deepEqual(Object.keys(updated.providers), Object.keys(updated.providers).sort());
    assert.equal(updated.sourceSerial, 10);
    assert.equal(updated.fallbackSnapshot, '2026-11-16');
    assert.equal(text.includes('ignored-icon'), false);
    assert.equal(text.includes('\r'), false);
    assert.equal(text.endsWith('\n'), true);
});

test('名前が同じなら、取得日・シリアル・アイコン・順序が変わっても出力を変更しない', async t => {
    const options = await fixture(t);
    const original = await readFile(options.targetPath, 'utf8');
    const response = metadata(11, { [secondId]: 'Fallback only', [firstId]: 'Old name' });
    const result = await updateProviders({ ...options, fetchImpl: async () => Response.json(response) });

    assert.equal(result.changed, false);
    assert.equal(await readFile(options.targetPath, 'utf8'), original);
    assert.equal(JSON.parse(await readFile(options.cachePath, 'utf8')).serial, 11);
});

test('24時間以内は再取得せず、24時間ちょうどから条件付き取得して304を処理する', async t => {
    const options = await fixture(t);
    await seedCache(options, '2026-11-15T00:17:00.001Z');

    const result = await updateProviders({
        ...options,
        fetchImpl: async () => {
            assert.fail('24時間以内に外部へアクセスしました。');
        }
    });

    assert.equal(result.changed, false);

    let calls = 0;
    const original = await readFile(options.targetPath, 'utf8');
    const exactBoundary = new Date(now.getTime() + 1);
    const unchanged = await updateProviders({
        ...options,
        now: exactBoundary,
        fetchImpl: async url => {
            calls++;
            assert.equal(url.searchParams.get('localCopySerial'), '10');

            return new Response(null, { status: 304 });
        }
    });

    assert.equal(calls, 1);
    assert.equal(unchanged.changed, false);
    assert.equal(await readFile(options.targetPath, 'utf8'), original);
    assert.equal(JSON.parse(await readFile(options.cachePath, 'utf8')).checkedAt, exactBoundary.toISOString());
});

test('空データ・不正項目・通信失敗では既存JSONと成功済みキャッシュを維持する', async t => {
    const failures = [
        ['空オブジェクト', async () => Response.json({})],
        ['名前なし', async () => Response.json({ no: 11 })],
        ['不正GUID', async () => Response.json(metadata(11, { bad: 'Invalid' }))],
        ['ゼロGUID', async () => Response.json(metadata(11, { '00000000-0000-0000-0000-000000000000': 'Unknown' }))],
        ['名前の欠損', async () => Response.json({ no: 11, [firstId]: { icon: 'ignored' } })],
        ['空の名前', async () => Response.json(metadata(11, { [firstId]: '　 ' }))],
        ['長すぎる名前', async () => Response.json(metadata(11, { [firstId]: 'x'.repeat(101) }))],
        ['制御文字', async () => Response.json(metadata(11, { [firstId]: 'name\n' }))],
        ['不正Unicode', async () => Response.json(metadata(11, { [firstId]: '\ud800' }))],
        ['大小文字だけ異なるGUID', async () => Response.json(metadata(11, { [firstId]: 'One', [firstId.toUpperCase()]: 'Two' }))],
        ['シリアル後退', async () => Response.json(metadata(9))],
        ['不正シリアル', async () => Response.json(metadata('11'))],
        ['不正JSON', async () => new Response('{broken')],
        ['取得制限', async () => new Response(null, { status: 429 })],
        ['サービス停止', async () => new Response(null, { status: 503 })],
        ['通信例外', async () => {
            throw new Error('Network unavailable');
        }]
    ];

    for (const [name, fetchImpl] of failures) {
        await t.test(name, async scenario => {
            const options = await fixture(scenario);
            await seedCache(options);
            const original = await readFile(options.targetPath, 'utf8');
            const cache = await readFile(options.cachePath, 'utf8');

            await assert.rejects(updateProviders({ ...options, fetchImpl }));

            assert.equal(await readFile(options.targetPath, 'utf8'), original);
            assert.equal(await readFile(options.cachePath, 'utf8'), cache);
        });
    }
});

test('キャッシュがない304と、破損・未来時刻のキャッシュを成功として扱わない', async t => {
    const options = await fixture(t);
    const original = await readFile(options.targetPath, 'utf8');
    await assert.rejects(updateProviders({ ...options, fetchImpl: async () => new Response(null, { status: 304 }) }), /HTTP 304/);

    await writeFile(options.cachePath, '{broken');
    await assert.rejects(updateProviders(options), SyntaxError);

    await seedCache(options, '2026-11-17T00:17:00.000Z');
    await assert.rejects(updateProviders(options), /未来/);
    assert.equal(await readFile(options.targetPath, 'utf8'), original);
});

test('旧一覧の固定データを読み込み、C-MDS未収録の1Passwordを保持する', async t => {
    const options = await fixture(t);
    options.fallbackPath = new URL('../../tools/passkey-providers-fallback.json', import.meta.url);
    await updateProviders({ ...options, fetchImpl: async () => Response.json(metadata(10, { [fourthId]: 'New provider' })) });

    const updated = JSON.parse(await readFile(options.targetPath, 'utf8'));
    assert.equal(updated.providers[firstId], '1Password');
    assert.equal(updated.providers[fourthId], 'New provider');
    assert.equal(Object.keys(updated.providers).length, 68);
});
