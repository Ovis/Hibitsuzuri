import { appendFile, mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parseArgs } from 'node:util';
import { randomUUID } from 'node:crypto';

const sourceUrl = 'https://c-mds.fidoalliance.org/';
const root = fileURLToPath(new URL('../', import.meta.url));
const fallbackFile = 'tools/passkey-providers-fallback.json';
const dayMilliseconds = 24 * 60 * 60 * 1000;
const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const zeroGuid = '00000000-0000-0000-0000-000000000000';

/** 配列やnullを対応表として誤って受け入れない。 */
function requireObject(value) {
    if (value === null || typeof value !== 'object' || Array.isArray(value)) {
        throw new Error('名前対応表はJSONオブジェクトである必要があります。');
    }
}

/** GUIDと表示名を検証し、差分が取得順に左右されない対応表へ正規化する。 */
function validateProviders(value) {
    requireObject(value);

    const result = new Map();
    for (const [id, name] of Object.entries(value)) {
        if (!guidPattern.test(id) || id.toLowerCase() === zeroGuid || typeof name !== 'string'
            || name.trim().length === 0 || [...name].length > 100 || !name.isWellFormed() || /[\u0000-\u001f\u007f]/u.test(name)) {
            throw new Error(`名前対応表に不正な項目があります: ${id}`);
        }

        const normalizedId = id.toLowerCase();
        if (result.has(normalizedId)) {
            throw new Error(`名前対応表に重複したGUIDがあります: ${normalizedId}`);
        }

        result.set(normalizedId, name);
    }

    if (result.size === 0) {
        throw new Error('空の名前対応表で既存データを上書きできません。');
    }

    return Object.fromEntries([...result].sort(([left], [right]) => left < right ? -1 : left > right ? 1 : 0));
}

/** C-MDSの名前だけを取り込み、アイコンや未知の構造をアプリへ持ち込まない。 */
function readMetadata(value) {
    requireObject(value);
    if (!Number.isSafeInteger(value.no) || value.no < 0) {
        throw new Error('C-MDSのシリアル番号が不正です。');
    }

    const providers = Object.create(null);
    for (const [id, entry] of Object.entries(value)) {
        if (id === 'no') {
            continue;
        }

        // 英語名は仕様上必須。取得元の異常を欠落扱いで隠さず、更新全体を停止する。
        providers[id] = entry?.friendlyNames?.['en-US'];
    }

    return { serial: value.no, providers: validateProviders(providers) };
}

/** 欠損だけを未取得として扱い、読み取り失敗や破損は更新失敗として伝える。 */
async function readCache(path) {
    let text;
    try {
        text = await readFile(path, 'utf8');
    } catch (error) {
        if (error.code === 'ENOENT') {
            return null;
        }

        throw error;
    }

    const cache = JSON.parse(text);
    if (cache.source !== sourceUrl || !Number.isSafeInteger(cache.serial) || cache.serial < 0
        || !Number.isFinite(Date.parse(cache.checkedAt))) {
        throw new Error('C-MDSのキャッシュが不正です。');
    }

    return { ...cache, providers: validateProviders(cache.providers) };
}

/** 完成したJSONを一時ファイルから置換し、途中までの内容を残さない。 */
async function writeJson(path, value) {
    await mkdir(dirname(path), { recursive: true });

    const temporary = `${path}.${randomUUID()}.tmp`;
    try {
        await writeFile(temporary, JSON.stringify(value, null, 2) + '\n', { encoding: 'utf8', flag: 'wx' });
        await rename(temporary, path);
    } finally {
        await rm(temporary, { force: true });
    }
}

/** C-MDSを取得・検証して名前の実差分だけを書き出す。失敗時は同梱JSONを変更しない。 */
export async function updateProviders({
    targetPath = resolve(root, 'src/Hibitsuzuri.Web/Auth/passkey-providers.json'),
    fallbackPath = resolve(root, fallbackFile),
    cachePath = resolve(root, 'artifacts/passkey-provider-update/cache.json'),
    fetchImpl = fetch,
    now = new Date()
} = {}) {
    const current = JSON.parse(await readFile(targetPath, 'utf8'));
    const fallback = JSON.parse(await readFile(fallbackPath, 'utf8'));
    const currentProviders = validateProviders(current.providers);
    const fallbackProviders = validateProviders(fallback.providers);

    let cache = await readCache(cachePath);
    const age = cache === null ? Infinity : now.getTime() - Date.parse(cache.checkedAt);
    if (age < 0) {
        throw new Error('C-MDSのキャッシュ時刻が未来になっています。');
    }

    if (age >= dayMilliseconds) {
        const url = new URL(sourceUrl);
        if (cache !== null) {
            url.searchParams.set('localCopySerial', cache.serial);
        }

        // 429や通信失敗をその場で繰り返さず、次の実行で確認する。
        const response = await fetchImpl(url, { signal: AbortSignal.timeout(30000), redirect: 'error' });
        if (response.status === 304 && cache !== null) {
            cache = { ...cache, checkedAt: now.toISOString() };
        } else {
            if (response.status !== 200) {
                throw new Error(`C-MDSの取得に失敗しました (HTTP ${response.status})。既存データを維持します。`);
            }

            const metadata = readMetadata(await response.json());
            if (cache !== null && metadata.serial < cache.serial) {
                throw new Error('C-MDSのシリアル番号が後退しています。');
            }

            cache = { source: sourceUrl, checkedAt: now.toISOString(), ...metadata };
        }

        await writeJson(cachePath, cache);
    }

    const providers = validateProviders({ ...fallbackProviders, ...cache.providers });
    if (JSON.stringify(currentProviders) === JSON.stringify(providers)) {
        return { changed: false, count: Object.keys(providers).length };
    }

    await writeJson(targetPath, {
        retrievedOn: cache.checkedAt.slice(0, 10),
        source: sourceUrl,
        sourceSerial: cache.serial,
        fallbackSource: fallback.source,
        fallbackSnapshot: fallback.retrievedOn,
        providers
    });

    return { changed: true, count: Object.keys(providers).length };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    const { values } = parseArgs({ options: { cache: { type: 'string' } } });
    const result = await updateProviders({ cachePath: values.cache });
    console.log(`Passkeyの名前対応表: ${result.count}件、${result.changed ? '更新あり' : '変更なし'}`);
    if (process.env.GITHUB_OUTPUT) {
        await appendFile(process.env.GITHUB_OUTPUT, `changed=${result.changed}\n`);
    }
}
