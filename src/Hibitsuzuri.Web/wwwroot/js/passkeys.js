'use strict';

for (const form of document.querySelectorAll('.passkey-form')) {
    const button = form.querySelector('button[type="submit"]');
    const status = form.querySelector('.passkey-status');
    const purpose = form.dataset.purpose;
    const creation = purpose === 'register';
    const supported = window.isSecureContext && window.PublicKeyCredential
        && (creation ? PublicKeyCredential.parseCreationOptionsFromJSON : PublicKeyCredential.parseRequestOptionsFromJSON)
        && PublicKeyCredential.prototype.toJSON;
    if (!supported) {
        button.disabled = true;
        status.textContent = 'この環境ではPasskeyを利用できません。対応ブラウザとHTTPS接続を確認してください。パスワードは引き続き利用できます。';
        continue;
    }

    form.addEventListener('submit', async event => {
        event.preventDefault();
        button.disabled = true;
        status.textContent = '端末で本人確認してください。';

        try {
            const fields = new FormData(form);
            if (purpose === 'login') {
                fields.set('rememberMe', document.querySelector('#RememberMe').checked.toString());
            }

            const options = await send(`/api/passkeys/${purpose}/options`, fields);
            const credential = creation
                ? await navigator.credentials.create({ publicKey: PublicKeyCredential.parseCreationOptionsFromJSON(options.publicKeyOptions) })
                : await navigator.credentials.get({ publicKey: PublicKeyCredential.parseRequestOptionsFromJSON(options.publicKeyOptions) });
            if (!(credential instanceof PublicKeyCredential)) {
                throw new Error('操作がキャンセルされました。');
            }

            fields.set('ChallengeId', options.challengeId);
            fields.set('Credential', JSON.stringify(credential.toJSON()));
            const result = await send(`/api/passkeys/${purpose}/verify`, fields);
            const returnUrl = form.dataset.returnUrl;
            // サーバーが検証した戻り先のみを使用し、外部URLへの移動は許可しない。
            window.location.assign(returnUrl?.startsWith('/') && !returnUrl.startsWith('//') ? returnUrl : result.redirect);
        } catch (error) {
            status.textContent = error instanceof DOMException && error.name === 'NotAllowedError'
                ? '操作がキャンセルされたか、本人確認の時間が切れました。もう一度試してください。'
                : error instanceof Error ? error.message : 'Passkeyの操作に失敗しました。';
        } finally {
            button.disabled = false;
        }
    });
}

async function send(path, body) {
    const response = await fetch(path, { method: 'POST', body, credentials: 'same-origin', redirect: 'error' });
    if (!response.ok) {
        throw new Error(response.status === 403 ? '本人確認の期限が切れました。本人確認画面で再認証してください。'
            : response.status === 429 ? '操作が多いため一時的に制限しています。時間をおいて再試行してください。'
                : 'Passkeyの操作を完了できませんでした。登録状態や接続を確認して再試行してください。');
    }

    return response.json();
}
