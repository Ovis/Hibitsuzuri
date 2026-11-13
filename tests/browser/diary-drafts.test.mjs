import assert from 'node:assert/strict';
import test from 'node:test';
import { DiaryDrafts, saveIntent, validContent } from '../../src/Hibitsuzuri.Web/wwwroot/js/diary-drafts.js';

class MemoryStorage {
    values = new Map();

    get length() {
        return this.values.size;
    }

    key(index) {
        return [...this.values.keys()][index] ?? null;
    }

    getItem(key) {
        return this.values.get(key) ?? null;
    }

    setItem(key, value) {
        this.values.set(key, value);
    }

    removeItem(key) {
        this.values.delete(key);
    }
}

function draft(id, content = '本文') {
    return { id, content, baseContent: '', version: '', savedAt: 123456, operation: null };
}

test('同じ日付の複数タブ、日記帳、ユーザーの下書きを分離し、現在の一件だけ消す', () => {
    const storage = new MemoryStorage();
    const first = new DiaryDrafts(storage, 'owner', 'book', '2026-11-13');
    const second = new DiaryDrafts(storage, 'owner', 'book', '2026-11-13');
    const otherBook = new DiaryDrafts(storage, 'owner', 'other', '2026-11-13');
    const otherOwner = new DiaryDrafts(storage, 'member', 'book', '2026-11-13');
    first.save(draft('tab-a', 'タブA'));
    second.save(draft('tab-b', 'タブB'));
    otherBook.save(draft('tab-c'));
    otherOwner.save(draft('tab-d'));

    first.remove('tab-a');
    assert.deepEqual(second.list().map(item => item.content), ['タブB']);
    assert.equal(otherBook.list().length, 1);
    assert.equal(otherOwner.list().length, 1);
});

test('応答不明の保存IDを再読み込み後に再利用し、入力変更後は別の意図にする', () => {
    const storage = new MemoryStorage();
    const first = new DiaryDrafts(storage, 'owner', 'book', '2026-11-13');
    const intent = saveIntent(null, '本文', 'v1', () => 'operation-a');
    first.save({ ...draft('tab-a'), version: 'v1', operation: intent });
    const restored = new DiaryDrafts(storage, 'owner', 'book', '2026-11-13').list()[0];

    assert.equal(saveIntent(restored.operation, restored.content, restored.version).id, 'operation-a');
    assert.equal(saveIntent(restored.operation, '変更後', 'v1', () => 'operation-b').id, 'operation-b');
    assert.equal(saveIntent(restored.operation, '本文', 'v2', () => 'operation-c').id, 'operation-c');
});

test('容量不足・利用禁止・破損を空一覧や保存成功に置き換えない', () => {
    const storage = new MemoryStorage();
    const drafts = new DiaryDrafts(storage, 'owner', 'book', '2026-11-13');
    storage.setItem('hibitsuzuri.draft.owner.book.2026-11-13.broken', '{broken');
    assert.throws(() => drafts.list());
    assert.equal(storage.length, 1);
    storage.setItem = () => {
        throw new Error('QuotaExceededError');
    };
    assert.throws(() => drafts.save(draft('tab-a')), /QuotaExceededError/);
});

test('本文はUnicodeスカラー値で数え、空白・不正なUTF-16・NULを拒否する', () => {
    assert.equal(validContent('😀'.repeat(100000)), true);
    assert.equal(validContent('😀'.repeat(100001)), false);
    assert.equal(validContent(' \n\t'), false);
    assert.equal(validContent('\ud800'), false);
    assert.equal(validContent('本文\0'), false);
});
