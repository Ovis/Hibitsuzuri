import assert from 'node:assert/strict';
import test from 'node:test';
import { createImageEditor } from '../../src/Hibitsuzuri.Web/wwwroot/js/diary-images.js';

/** 添付一覧の操作を実行するための最小限のDOM境界。 */
class Element {
    children = [];
    dataset = {};
    handlers = new Map();
    textContent = '';
    value = '';
    events = [];

    append(...nodes) {
        this.children.push(...nodes);
    }

    replaceChildren(...nodes) {
        this.children = nodes;
    }

    addEventListener(type, listener) {
        this.handlers.set(type, listener);
    }

    setAttribute() {}

    dispatchEvent(event) {
        this.events.push(event.type);
    }

    async click() {
        await this.handlers.get('click')?.();
    }
}

const imageId = '9501f94db9d045979a3b565c56f8d116';
const draftId = 'f855fe00-1b0b-4cc8-b5af-d9abcf9db9cc';

/** 本番の画像エディターを読み込み、通信と確認ダイアログだけを置き換える。 */
async function editor(t, options = {}) {
    const originalDocument = globalThis.document;
    const originalWindow = globalThis.window;
    const status = new Element();
    const list = new Element();
    const files = new Element();
    const content = new Element();
    const requests = [];
    const notices = [];
    const confirmations = [];
    let busy = false;
    let images = [{ id: imageId, name: '写真.png', width: 60, height: 40, expiresAt: options.saved ? null : Date.now() + 86400000 }];

    globalThis.document = {
        querySelector: selector => ({ '#image-status': status, '#image-list': list, '#image-files': files })[selector],
        createElement: () => new Element()
    };
    globalThis.window = {
        confirm: message => {
            confirmations.push(message);
            return options.confirm !== false;
        }
    };
    t.after(() => {
        globalThis.document = originalDocument;
        globalThis.window = originalWindow;
    });
    t.mock.method(globalThis, 'fetch', async () => ({ ok: true, json: async () => images }));

    const api = createImageEditor({
        form: { dataset: { book: 'book', date: '2020-05-10' } }, content, draftId,
        isBusy: () => busy,
        setBusy: value => {
            busy = value;
        },
        changed() {},
        showNotice: message => notices.push(message),
        post: async (path, fields) => {
            requests.push({ path, fields });
            if (path.endsWith('/delete')) {
                if (options.reject) {
                    return { ok: false, json: async () => ({ message: '別の下書きで保持している画像です。' }) };
                }

                images = [];
                return { ok: true, json: async () => ({ deleted: true }) };
            }

            return { ok: true, json: async () => ({ missing: [] }) };
        }
    });
    await new Promise(setImmediate);

    return {
        api, content, list, requests, notices, confirmations, isBusy: () => busy,
        button: label => list.children.flatMap(row => row.children).find(item => item.textContent === label)
    };
}

test('本文を空にした更新は明示破棄と区別し、下書きの保持要求を順番に送る', async t => {
    const view = await editor(t);

    await Promise.all([view.api.renew('移動前'), view.api.renew(''), view.api.discard()]);

    assert.deepEqual(view.requests.map(request => request.path), ['/api/images/draft', '/api/images/draft', '/api/images/draft/discard']);
    assert.deepEqual(view.requests.map(request => request.fields.content), ['移動前', '', '']);
});

test('本文から外す操作は画像実体を消さず、削除確認をキャンセルしても保持する', async t => {
    const view = await editor(t, { confirm: false });
    view.content.value = `前文\n![画像](attachment:${imageId})\n後文`;

    await view.button('画像を削除').click();
    assert.equal(view.requests.length, 0);
    assert.equal(view.confirmations.length, 0);
    assert.match(view.notices.at(-1), /本文から外す/);

    await view.button('本文から外す').click();
    assert.equal(view.content.value, '前文\n\n後文');
    assert.deepEqual(view.content.events, ['input']);
    await view.button('画像を削除').click();
    assert.equal(view.confirmations.length, 1);
    assert.equal(view.requests.length, 0);
    assert.equal(view.list.children.length, 1);
});

test('未使用画像の削除は先行する保持更新後に実行し、一覧から取り除く', async t => {
    const view = await editor(t);
    view.content.value = '画像を外した本文';
    const renewal = view.api.renew(view.content.value);

    await view.button('画像を削除').click();
    await renewal;

    assert.deepEqual(view.requests.map(request => request.path), ['/api/images/draft', `/api/images/${imageId}/delete`]);
    assert.deepEqual(view.requests[1].fields, { draftId, content: view.content.value, confirmed: 'true' });
    assert.equal(view.list.children.length, 0);
    assert.equal(view.isBusy(), false);
    assert.match(view.notices.at(-1), /削除しました/);
});

test('サーバーが別の下書きの保持を検出した場合は理由を表示し、画像を残す', async t => {
    const view = await editor(t, { reject: true });

    await view.button('画像を削除').click();

    assert.equal(view.list.children.length, 1);
    assert.equal(view.isBusy(), false);
    assert.match(view.notices.at(-1), /別の下書き/);
});

test('保存済み本文や履歴等で保持する画像には実体の削除操作を表示しない', async t => {
    const view = await editor(t, { saved: true });

    assert.equal(view.button('画像を削除'), undefined);
    assert.ok(view.button('本文から外す'));
});
