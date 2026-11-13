import { DiaryDrafts, saveIntent, validContent } from './diary-drafts.js';
import { createImageEditor } from './diary-images.js';

const form = document.querySelector('#diary-editor');
const content = document.querySelector('#diary-content');
const notice = document.querySelector('#editor-notice');
const draftStatus = document.querySelector('#draft-status');
const preview = document.querySelector('#diary-preview');
const conflict = document.querySelector('#conflict');
const candidatePanel = document.querySelector('#draft-candidates');
const csrf = form.querySelector('[name="__RequestVerificationToken"]').value;
const draftId = crypto.randomUUID();

let drafts;
let baseContent = content.value;
let version = form.dataset.version || '';
let operation = null;
let latest = null;
let busy = false;
let leaving = false;
let draftTimer;
let previewTimer;
let previewRequest;

const images = createImageEditor({
    form, content, draftId, post, changed, showNotice,
    isBusy: () => busy,
    setBusy: value => {
        busy = value;
        content.readOnly = value;
        document.querySelector('#save-entry').disabled = value;
    }
});

function showNotice(message) {
    notice.textContent = message;
    notice.hidden = false;
}

function dirty() {
    return content.value !== baseContent || operation !== null;
}

function persistDraft() {
    clearTimeout(draftTimer);
    if (!drafts) {
        draftStatus.textContent = 'ブラウザに下書きを保存できません。離れる前に本文をコピーしてください。';
        return;
    }

    try {
        drafts.save({ id: draftId, content: content.value, baseContent, version, operation, savedAt: Date.now() });
        draftStatus.textContent = `下書き保存 ${new Date().toLocaleTimeString()}（日記は未保存）`;
        images.renew(content.value);
    } catch {
        draftStatus.textContent = '下書きを保存できません。容量・保存の許可を確認し、本文をコピーしてください。';
    }
}

function removeCurrentDraft() {
    clearTimeout(draftTimer);
    if (drafts) {
        drafts.remove(draftId);
    }

    return images.renew('');
}

async function post(url, fields, signal) {
    const body = new FormData();
    body.append('__RequestVerificationToken', csrf);
    for (const [key, value] of Object.entries(fields)) {
        body.append(key, value);
    }

    return fetch(url, { method: 'POST', body, signal, credentials: 'same-origin', redirect: 'error' });
}

async function updatePreview() {
    previewRequest?.abort();
    if ([...content.value].length > 100000) {
        preview.textContent = '100,000文字を超えているためプレビューを停止しています。';
        return;
    }

    const request = new AbortController();
    previewRequest = request;

    try {
        const response = await post('/api/diary/preview', { content: content.value }, request.signal);
        if (!response.ok) {
            throw new Error('preview');
        }

        const result = await response.json();
        if (!request.signal.aborted && typeof result.html === 'string') {
            // HTMLは同一オリジンの共通Markdownレンダラーで生成した応答だけを使う。
            preview.innerHTML = result.html;
        }
    } catch (error) {
        if (error.name !== 'AbortError') {
            preview.textContent = 'プレビューを更新できません。接続とログイン状態を確認してください。本文は編集欄に残っています。';
        }
    }
}

function changed() {
    document.querySelector('#word-count').textContent = `${[...content.value].length.toLocaleString()} / 100,000文字`;

    clearTimeout(draftTimer);
    draftTimer = setTimeout(persistDraft, 250);

    clearTimeout(previewTimer);
    previewTimer = setTimeout(updatePreview, 400);
}

function renderCandidates(candidates) {
    candidatePanel.hidden = candidates.length === 0;
    const list = document.querySelector('#draft-list');
    list.replaceChildren();

    for (const draft of candidates) {
        const row = document.createElement('div');
        row.className = 'book-row';

        const label = document.createElement('span');
        label.textContent = `${new Date(draft.savedAt).toLocaleString()} ／ ${[...draft.content].length.toLocaleString()}文字`;

        const restore = document.createElement('button');
        restore.type = 'button';
        restore.textContent = '復元';
        restore.addEventListener('click', () => {
            if (busy || (dirty() && !window.confirm('編集中の内容をこの下書きに置き換えますか？'))) {
                return;
            }

            content.value = draft.content;
            baseContent = draft.baseContent;
            version = draft.version;
            operation = draft.operation;
            conflict.hidden = true;

            changed();
            persistDraft();

            showNotice('下書きを復元しました。保存日時と本文を確認してから保存してください。元の候補は明示的に破棄するまで残ります。');
        });

        const discard = document.createElement('button');
        discard.type = 'button';
        discard.textContent = '破棄';
        discard.addEventListener('click', () => {
            if (busy || !window.confirm('この下書きを破棄しますか？ 別の画面で編集中の候補は残してください。')) {
                return;
            }

            try {
                drafts.remove(draft.id);
                images.renew('', draft.id);
                renderCandidates(drafts.list().filter(item => item.id !== draftId));
            } catch {
                showNotice('下書きを破棄できませんでした。保存領域の許可を確認してください。');
            }
        });

        row.append(label, restore, discard);
        list.append(row);
    }
}

content.addEventListener('input', () => {
    // 本文が変わった時点で、応答不明の保存と新しい保存意図を分ける。
    operation = null;
    changed();
});

form.addEventListener('submit', async event => {
    event.preventDefault();
    if (busy) {
        return;
    }

    if (!validContent(content.value)) {
        showNotice('本文は空白以外の1～100,000文字で入力してください。');
        return;
    }

    operation = saveIntent(operation, content.value, version);
    persistDraft();

    busy = true;
    content.readOnly = true;
    document.querySelector('#save-entry').disabled = true;

    try {
        const response = await post(`/api/diary/${form.dataset.book}/${form.dataset.date}/save`, {
            content: operation.content, version: operation.version, operationId: operation.id
        });
        if (response.status === 401) {
            showNotice('ログインの有効期限が切れました。本文を残したまま別のタブでログインし、この画面を再読み込みして下書きを復元してください。');
            const login = document.createElement('a');
            login.href = '/auth/login';
            login.target = '_blank';
            login.rel = 'noopener noreferrer';
            login.textContent = ' ログイン画面を開く';
            notice.append(login);
            return;
        }

        const result = await response.json();
        if (response.status === 409 && result.latest) {
            latest = result.latest;
            document.querySelector('#latest-content').value = latest.content;
            conflict.hidden = false;
            document.querySelector('#keep-mine').disabled = latest.deleted;
            document.querySelector('#use-latest').disabled = latest.deleted;
            showNotice(latest.deleted ? '日記がゴミ箱に移されています。本文を控え、日記を復元してから編集してください。' : result.message);
            return;
        }

        if (!response.ok || result.saved !== true) {
            showNotice(result.message || '保存できませんでした。本文は保持しています。');
            return;
        }

        baseContent = content.value;
        version = result.version;
        operation = null;

        try {
            await removeCurrentDraft();
        } catch {
            showNotice('日記は保存できましたが、ブラウザの下書きを削除できませんでした。下書きの保存日時を確認して整理してください。');
            const back = document.createElement('a');
            back.href = form.dataset.return;
            back.textContent = ' 保存した日記を見る';
            notice.append(back);
            return;
        }

        leaving = true;
        window.location.assign(form.dataset.return);
    } catch {
        showNotice('保存結果を確認できませんでした。本文は保持しています。同じ内容で再度保存すると、重複せずに結果を確認できます。');
    } finally {
        busy = false;
        content.readOnly = false;
        document.querySelector('#save-entry').disabled = false;
    }
});

document.querySelector('#keep-mine').addEventListener('click', () => {
    if (!latest || latest.deleted || busy) {
        return;
    }

    version = latest.version;
    baseContent = latest.content;
    operation = null;
    conflict.hidden = true;

    persistDraft();

    showNotice('入力中の本文を採用しました。「保存する」で反映します。');
});

document.querySelector('#use-latest').addEventListener('click', () => {
    if (!latest || latest.deleted || busy || !window.confirm('編集中の本文を最新版に置き換えますか？')) {
        return;
    }

    content.value = latest.content;
    baseContent = latest.content;
    version = latest.version;
    operation = null;
    conflict.hidden = true;

    changed();
});

document.querySelector('#cancel-edit').addEventListener('click', async () => {
    if (busy || (dirty() && !window.confirm('編集中の変更を破棄しますか？ この画面の下書きも削除します。'))) {
        return;
    }

    busy = true;
    content.readOnly = true;
    document.querySelector('#save-entry').disabled = true;

    try {
        await removeCurrentDraft();
        leaving = true;
        window.location.assign(form.dataset.return);
    } catch {
        showNotice('下書きを削除できませんでした。本文はこの画面に残しています。');
    } finally {
        busy = false;
        content.readOnly = false;
        document.querySelector('#save-entry').disabled = false;
    }
});

document.querySelectorAll('[data-revision]').forEach(button => {
    button.addEventListener('click', () => {
        if (busy || (dirty() && !window.confirm('編集中の本文を選択した履歴で置き換えますか？'))) {
            return;
        }

        content.value = document.getElementById(`revision-${button.dataset.revision}`).value;
        operation = null;

        changed();
        showNotice('過去の本文を読み込みました。「保存する」を押すまで日記は変わりません。');
    });
});

document.querySelectorAll('[data-format]').forEach(button => {
    button.addEventListener('click', () => {
        if (busy) {
            return;
        }

        const selection = content.value.slice(content.selectionStart, content.selectionEnd);
        const formats = {
            bold: `**${selection || '太字'}**`, italic: `*${selection || '斜体'}*`,
            heading: `\n## ${selection || '見出し'}\n`, list: `\n- ${selection || '項目'}\n`,
            link: `[${selection || 'リンク名'}](https://example.com)`
        };

        content.setRangeText(formats[button.dataset.format], content.selectionStart, content.selectionEnd, 'select');
        content.focus();
        operation = null;
        changed();
    });
});

document.querySelectorAll('[data-editor-tab]').forEach(button => {
    button.addEventListener('click', () => {
        document.querySelector('.editor-grid').dataset.activePane = button.dataset.editorTab;
        document.querySelectorAll('[data-editor-tab]').forEach(tab => {
            tab.setAttribute('aria-pressed', String(tab === button));
        });
    });
});

document.querySelector('#skip-drafts').addEventListener('click', () => {
    candidatePanel.hidden = true;
});

window.addEventListener('beforeunload', event => {
    if (!leaving && (dirty() || busy)) {
        persistDraft();
        event.preventDefault();
        event.returnValue = '';
    }
});

window.addEventListener('pagehide', () => {
    if (!leaving && dirty()) {
        persistDraft();
    }
});

try {
    drafts = new DiaryDrafts(localStorage, form.dataset.owner, form.dataset.book, form.dataset.date);
    const candidates = drafts.list();
    renderCandidates(candidates);

    if (candidates.length === 0 && form.dataset.new === 'true') {
        content.value = document.querySelector('#book-template').value;
        if (content.value) {
            changed();
            showNotice('日記帳のテンプレートを挿入しました。保存すると日記に反映されます。');
        }
    }
} catch {
    showNotice('ブラウザの下書きを読み取れません。保存領域の内容は消していません。編集内容は離れる前にコピーしてください。');
}

document.querySelector('#word-count').textContent = `${[...content.value].length.toLocaleString()} / 100,000文字`;

// 初期化に失敗した画面から通常のフォーム送信で入力を失わないよう、準備後だけ有効にする。
document.querySelector('#save-entry').disabled = false;
content.disabled = false;
