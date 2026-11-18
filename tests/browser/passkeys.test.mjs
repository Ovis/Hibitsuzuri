import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';

const source = await readFile(new URL('../../src/Hibitsuzuri.Web/wwwroot/js/passkeys.js', import.meta.url), 'utf8');
const buffer = (...bytes) => Uint8Array.from(bytes).buffer;

/** 実際のフォーム処理を実行し、認証器との境界と送信済みリクエストを記録する。 */
async function submit({ purpose = 'register', extensions = {}, serialization = 'omitted', cancelled = false } = {}) {
    const requests = [];
    const redirects = [];
    const calls = [];
    const button = { disabled: false };
    const status = { textContent: '' };
    let onSubmit;

    class Credential {
        id = '__79';
        rawId = buffer(255, 254, 253);
        type = 'public-key';
        authenticatorAttachment = 'platform';
        response = purpose === 'register' ? {
            clientDataJSON: buffer(1, 2, 3),
            attestationObject: buffer(255, 255),
            getAuthenticatorData: () => buffer(4, 5),
            getPublicKey: () => buffer(6),
            getPublicKeyAlgorithm: () => -7,
            getTransports: () => ['internal', 'hybrid']
        } : {
            clientDataJSON: buffer(1, 2, 3),
            authenticatorData: buffer(4, 5),
            signature: buffer(252, 253),
            userHandle: purpose === 'login' ? null : buffer(8, 9)
        };

        getClientExtensionResults() {
            return extensions;
        }

        toJSON() {
            if (serialization === 'throws') {
                throw new TypeError('Illegal invocation');
            }

            // 利用者の送信結果と同じく、空の拡張結果が省略される認証器を再現する。
            return { id: this.id, rawId: this.id, type: this.type, response: { clientDataJSON: 'AQID' } };
        }

        static parseCreationOptionsFromJSON(options) {
            return options;
        }

        static parseRequestOptionsFromJSON(options) {
            return options;
        }
    }

    if (serialization === 'unavailable') {
        delete Credential.prototype.toJSON;
    }

    const form = {
        dataset: { purpose },
        querySelector: selector => selector.startsWith('button') ? button : status,
        addEventListener: (type, listener) => {
            assert.equal(type, 'submit');
            onSubmit = listener;
        }
    };
    class Fields extends FormData {
        constructor() {
            super();
            this.set('Name', '1Password');
            this.set('__RequestVerificationToken', 'test-csrf-token');
        }
    }

    const context = vm.createContext({
        window: { isSecureContext: true, PublicKeyCredential: Credential, location: { assign: value => redirects.push(value) } },
        PublicKeyCredential: Credential,
        document: { querySelectorAll: () => [form], querySelector: () => ({ checked: true }) },
        navigator: {
            credentials: Object.fromEntries(['create', 'get'].map(method => [method, async () => {
                calls.push(method);
                return cancelled ? null : new Credential();
            }]))
        },
        FormData: Fields,
        DOMException,
        btoa,
        fetch: async (path, options) => {
            requests.push({ path, fields: Object.fromEntries(options.body), credentials: options.credentials, redirect: options.redirect });
            return { ok: true, json: async () => path.endsWith('/options')
                ? { challengeId: 'test-challenge', publicKeyOptions: {} } : { redirect: '/account/passkeys' } };
        }
    });

    vm.runInContext(source, context);
    assert.equal(button.disabled, false, status.textContent);
    assert.equal(typeof onSubmit, 'function');
    await onSubmit({ preventDefault() {} });

    return { requests, redirects, calls, button, status };
}

for (const serialization of ['omitted', 'throws', 'unavailable']) {
    test(`toJSONが${serialization}でも、登録時に空の拡張結果と認証器のバイト列を送る`, async () => {
        const result = await submit({ serialization });

        assert.equal(result.requests.length, 2, result.status.textContent);
        const request = result.requests[1];
        const credential = JSON.parse(request.fields.Credential);
        assert.equal(request.path, '/api/passkeys/register/verify');
        assert.equal(request.fields.ChallengeId, 'test-challenge');
        assert.equal(request.fields.Name, '1Password');
        assert.equal(request.fields.__RequestVerificationToken, 'test-csrf-token');
        assert.equal(request.credentials, 'same-origin');
        assert.equal(request.redirect, 'error');
        assert.deepEqual(credential.clientExtensionResults, {});
        assert.equal(credential.rawId, '__79');
        assert.deepEqual(credential.response, {
            clientDataJSON: 'AQID', attestationObject: '__8', authenticatorData: 'BAU',
            publicKey: 'Bg', publicKeyAlgorithm: -7, transports: ['internal', 'hybrid']
        });
        assert.deepEqual(result.calls, ['create']);
        assert.deepEqual(result.redirects, ['/account/passkeys']);
        assert.equal(result.button.disabled, false);
    });
}

for (const purpose of ['login', 'reauthenticate']) {
    test(`${purpose}では拡張結果・署名・ユーザーハンドルを保持する`, async () => {
        const extensions = { credProps: { rk: true } };
        const result = await submit({ purpose, extensions, serialization: 'throws' });

        assert.equal(result.requests.length, 2, result.status.textContent);
        const request = result.requests[1];
        const credential = JSON.parse(request.fields.Credential);
        assert.equal(request.path, `/api/passkeys/${purpose}/verify`);
        assert.deepEqual(credential.clientExtensionResults, extensions);
        assert.deepEqual(credential.response, {
            clientDataJSON: 'AQID', authenticatorData: 'BAU', signature: '_P0',
            userHandle: purpose === 'login' ? null : 'CAk'
        });
        assert.equal(request.fields.rememberMe, purpose === 'login' ? 'true' : undefined);
        assert.deepEqual(result.calls, ['get']);
        assert.deepEqual(result.redirects, ['/account/passkeys']);
    });
}

test('認証器でキャンセルした場合は検証APIを呼ばず、操作をやり直せる', async () => {
    const result = await submit({ cancelled: true });

    assert.equal(result.requests.length, 1);
    assert.deepEqual(result.redirects, []);
    assert.equal(result.status.textContent, '操作がキャンセルされました。');
    assert.equal(result.button.disabled, false);
});
