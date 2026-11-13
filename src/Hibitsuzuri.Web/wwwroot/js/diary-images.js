// 本文を保存する経路とは分け、画像の保持期限だけを下書きに結び付ける。
export function createImageEditor({ form, content, draftId, post, changed, isBusy, setBusy, showNotice }) {
    const status = document.querySelector('#image-status');
    const list = document.querySelector('#image-list');
    const files = document.querySelector('#image-files');
    const url = `/api/images/${form.dataset.book}/${form.dataset.date}`;
    let queue = Promise.resolve();
    let uploading = false;

    function insert(id) {
        content.setRangeText(`![画像](attachment:${id})`, content.selectionStart, content.selectionEnd, 'end');
        content.focus();
        content.dispatchEvent(new Event('input'));
    }

    async function refresh() {
        try {
            const response = await fetch(url, { credentials: 'same-origin', redirect: 'error' });
            if (!response.ok) {
                throw new Error('images');
            }

            const images = await response.json();
            list.replaceChildren();
            let nearExpiry = false;
            for (const image of images) {
                const row = document.createElement('div');
                row.className = 'attachment-row';

                const thumbnail = document.createElement('button');
                thumbnail.type = 'button';
                thumbnail.className = 'image-trigger';
                thumbnail.dataset.imageSrc = `/images/${image.id}`;
                thumbnail.setAttribute('aria-label', `${image.name}を拡大`);
                const picture = document.createElement('img');
                picture.src = `/images/${image.id}`;
                picture.alt = image.name;
                picture.loading = 'lazy';
                thumbnail.append(picture);

                const label = document.createElement('span');
                label.textContent = `${image.name}（${image.width}×${image.height}）`;
                if (image.expiresAt !== null && image.expiresAt !== undefined) {
                    label.textContent += ` 画像期限 ${new Date(image.expiresAt).toLocaleDateString()}`;
                    nearExpiry ||= image.expiresAt - Date.now() < 7 * 86400000;
                }

                const add = document.createElement('button');
                add.type = 'button';
                add.textContent = '挿入';
                add.addEventListener('click', () => {
                    if (!isBusy()) {
                        insert(image.id);
                    }
                });

                const remove = document.createElement('button');
                remove.type = 'button';
                remove.textContent = '本文から外す';
                remove.addEventListener('click', () => {
                    if (isBusy()) {
                        return;
                    }

                    // 挿入操作で作ったMarkdownの画像参照だけを外す。日記と履歴の確定前に実体は消さない。
                    const reference = new RegExp(`!\\[(?:\\\\.|[^\\]\\\\])*\\]\\(attachment:${image.id}\\)`, 'g');
                    const updated = content.value.replace(reference, '');
                    if (updated === content.value) {
                        showNotice('本文中の画像参照は見つかりませんでした。履歴・別の下書きから使われる画像は保持されます。');
                        return;
                    }

                    content.value = updated;
                    content.dispatchEvent(new Event('input'));
                });

                row.append(thumbnail, label, add, remove);
                list.append(row);
            }

            status.textContent = nearExpiry ? '期限が近い下書き画像があります。本文を確認して下書き保存または日記保存してください。'
                : images.length === 0 ? '添付画像はありません。' : '追加した画像はカーソル位置へ挿入します。';
        } catch {
            status.textContent = '画像一覧を取得できません。接続とログイン状態を確認してください。本文は保持しています。';
        }
    }

    function renew(text, id = draftId) {
        if (uploading) {
            // 追加中の画像を直前の本文で参照解除しない。取り込み完了後に最新本文を送る。
            return Promise.resolve();
        }

        // 古い本文の更新応答が新しい保持期限を上書きしないよう、タブ内で順番を固定する。
        queue = queue.then(async () => {
            try {
                const response = await post('/api/images/draft', { draftId: id, content: text }, AbortSignal.timeout(10000));
                if (!response.ok) {
                    throw new Error('lease');
                }

                const result = await response.json();
                if (result.missing.length > 0) {
                    status.textContent = '期限切れまたは利用できない画像があります。画像を再選択してください。本文の下書きは保持しています。';
                }
            } catch {
                status.textContent = '画像の保持期限を延長できません。接続とログイン状態を確認してください。本文はブラウザに残っています。';
            }
        });

        return queue;
    }

    files.addEventListener('change', async () => {
        if (isBusy()) {
            return;
        }

        setBusy(true);
        uploading = true;
        try {
            await queue;
            for (const file of files.files) {
                status.textContent = `${file.name}を取り込み中…`;
                if (file.size > 20 * 1024 * 1024) {
                    showNotice('1枚20MiBを超える画像は追加できません。');
                    break;
                }

                const response = await post(url, { file, draftId });
                const result = await response.json();
                if (!response.ok) {
                    showNotice(result.message || '画像を追加できませんでした。');
                    break;
                }

                insert(result.id);
                changed();
            }
        } catch {
            showNotice('画像の取り込み結果を確認できませんでした。添付一覧を確認してから再度お試しください。');
        } finally {
            files.value = '';
            uploading = false;
            await renew(content.value);
            setBusy(false);
            await refresh();
        }
    });

    refresh();
    return { renew };
}
