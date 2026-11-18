// 日記帳・記念日の既存フォームを使い、認可・版照合・入力検証を通常画面と共有する。
(() => {
    const dialog = document.querySelector('#page-dialog');
    const body = dialog.querySelector('[data-page-dialog-body]');
    const title = dialog.querySelector('h2');
    const error = dialog.querySelector('[role="alert"]');
    let changed = false;
    let saving = false;
    let refreshed = false;
    let opener;
    let requestId = 0;

    function canLeave() {
        return !saving && (!changed || window.confirm('入力中の変更を破棄しますか？'));
    }

    function close() {
        if (!canLeave()) {
            return;
        }

        requestId++;
        changed = false;
        dialog.close();
        if (refreshed) {
            location.reload();
        } else {
            opener?.focus();
        }
    }

    function updateBookChoices(root = body) {
        root.querySelectorAll('[data-anniversary-books]').forEach(group => {
            group.hidden = group.closest('form').querySelector('[name="allNotebooks"]:checked')?.value !== 'false';
        });
    }

    async function open(link) {
        if (!canLeave()) {
            return;
        }

        const url = new URL(link.href, location.href);
        if (url.origin !== location.origin) {
            return;
        }
        if (!dialog.open) {
            opener = document.activeElement;
            dialog.showModal();
        }

        const version = ++requestId;
        changed = false;
        error.hidden = true;
        title.textContent = link.textContent.trim();
        body.replaceChildren(document.createTextNode('読み込み中…'));
        try {
            const response = await fetch(url, { credentials: 'same-origin' });
            const document = new DOMParser().parseFromString(await response.text(), 'text/html');
            const content = document.querySelector(link.dataset.dialogTarget || '[data-dialog-content]');
            if (!response.ok || !content) {
                throw new Error('画面を読み込めません。接続とログイン状態を確認して、開き直してください。');
            }
            if (version !== requestId) {
                return;
            }

            content.querySelectorAll('script').forEach(script => script.remove());
            // 元ページとダイアログのラベルが同じIDを指さないようにする。
            content.querySelectorAll('[id]').forEach(element => {
                const id = element.id;
                element.id = 'modal-' + id;
                content.querySelectorAll('label[for]').forEach(label => {
                    if (label.htmlFor === id) {
                        label.htmlFor = element.id;
                    }
                });
            });
            const heading = content.querySelector('h2');
            if (heading) {
                title.textContent = heading.textContent;
                heading.remove();
            }
            if (content instanceof HTMLDetailsElement) {
                content.open = true;
                content.querySelector('summary')?.remove();
            }
            content.classList.remove('paper', 'spacer', 'settings-panel');
            body.replaceChildren(content);
            updateBookChoices();
        } catch (failure) {
            if (version === requestId) {
                body.replaceChildren();
                error.textContent = failure.message;
                error.hidden = false;
            }
        }
    }

    document.addEventListener('click', event => {
        const link = event.target.closest('a[data-page-dialog]');
        if (link && !event.ctrlKey && !event.metaKey && !event.shiftKey && event.button === 0) {
            event.preventDefault();
            open(link);
        }
    });

    body.addEventListener('input', () => {
        changed = true;
    });
    updateBookChoices(document);
    document.querySelector('main')?.addEventListener('change', () => updateBookChoices(document));
    body.addEventListener('change', () => {
        changed = true;
        updateBookChoices();
    });
    body.addEventListener('submit', async event => {
        event.preventDefault();
        const form = event.target;
        if (saving || (form.dataset.confirm && !window.confirm(form.dataset.confirm))) {
            return;
        }

        saving = true;
        error.hidden = true;
        const buttons = [...body.querySelectorAll('button')];
        const states = buttons.map(button => button.disabled);
        const values = new FormData(form, event.submitter);
        buttons.forEach(button => {
            button.disabled = true;
        });
        try {
            const response = await fetch(form.action, { method: 'POST', body: values, credentials: 'same-origin' });
            if (!response.ok || !response.redirected || new URL(response.url).pathname.startsWith('/auth/')) {
                const text = await response.text();
                const document = new DOMParser().parseFromString(text, 'text/html');
                const message = document.querySelector('.validation-summary-errors')?.textContent.trim();
                throw new Error(message || (response.headers.get('content-type')?.includes('text/plain') ? text : '保存結果を確認できません。入力を控えて、接続とログイン状態を確認してください。'));
            }

            changed = false;
            refreshed = true;
            saving = false;
            if (new URL(response.url).pathname === '/anniversaries') {
                const link = document.createElement('a');
                link.href = '/anniversaries';
                link.textContent = '記念日';
                await open(link);
            } else {
                close();
            }
        } catch (failure) {
            error.textContent = failure.message;
            error.hidden = false;
        } finally {
            saving = false;
            buttons.forEach((button, index) => {
                button.disabled = states[index];
            });
        }
    });

    dialog.querySelectorAll('[data-close-page-dialog]').forEach(button => button.addEventListener('click', close));
    dialog.addEventListener('cancel', event => {
        event.preventDefault();
        close();
    });
    window.addEventListener('beforeunload', event => {
        if (dialog.open && (changed || saving)) {
            event.preventDefault();
            event.returnValue = '';
        }
    });
})();
