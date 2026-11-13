// ブラウザの保存領域だけを扱う。本文のDB保存とは独立させる。
export class DiaryDrafts {
    constructor(storage, owner, book, date) {
        this.storage = storage;
        this.prefix = `hibitsuzuri.draft.${owner}.${book}.${date}.`;
    }

    list() {
        const drafts = [];
        for (let index = 0; index < this.storage.length; index++) {
            const key = this.storage.key(index);
            if (!key?.startsWith(this.prefix)) {
                continue;
            }

            const draft = JSON.parse(this.storage.getItem(key));
            if (!draft || typeof draft.id !== 'string' || key !== this.prefix + draft.id
                || typeof draft.content !== 'string' || typeof draft.baseContent !== 'string'
                || typeof draft.version !== 'string' || !Number.isFinite(draft.savedAt)
                || (draft.operation !== null && (typeof draft.operation?.id !== 'string'
                    || typeof draft.operation?.content !== 'string' || typeof draft.operation?.version !== 'string'))) {
                throw new Error('下書きの保存形式を読み取れません。保存領域の内容は変更していません。');
            }

            drafts.push(draft);
        }

        return drafts.sort((left, right) => right.savedAt - left.savedAt);
    }

    save(draft) {
        this.storage.setItem(this.prefix + draft.id, JSON.stringify(draft));
    }

    remove(id) {
        this.storage.removeItem(this.prefix + id);
    }
}

// 変更のない再送には同じIDを使い、本文または期待版を変えたら新しい意図とする。
export function saveIntent(previous, content, version, makeId = () => crypto.randomUUID()) {
    if (previous?.content === content && previous?.version === version) {
        return previous;
    }

    return { id: makeId(), content, version };
}

export function validContent(text) {
    return text.trim().length > 0 && !text.includes('\0') && text.isWellFormed()
        && [...text].length <= 100000;
}
