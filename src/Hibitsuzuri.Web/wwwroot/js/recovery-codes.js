'use strict';

const recoveryText = document.querySelector('#recovery-codes').textContent;
const codesStatus = document.querySelector('#codes-status');

document.querySelector('#copy-codes').addEventListener('click', async () => {
    try {
        await navigator.clipboard.writeText(recoveryText);
        codesStatus.textContent = 'コピーしました。安全な保存先へ貼り付けてください。';
    } catch {
        codesStatus.textContent = 'コピーできませんでした。テキスト保存または手動で保存してください。';
    }
});

document.querySelector('#download-codes').addEventListener('click', () => {
    const url = URL.createObjectURL(new Blob([recoveryText], { type: 'text/plain;charset=utf-8' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = 'hibitsuzuri-recovery-codes.txt';
    link.click();
    URL.revokeObjectURL(url);
    codesStatus.textContent = '保存先にファイルがあることを確認してください。';
});
