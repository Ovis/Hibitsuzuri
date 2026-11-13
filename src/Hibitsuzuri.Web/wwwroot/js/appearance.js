(() => {
    'use strict';

    const root = document.documentElement;
    const storageKey = root.dataset.appearanceKey;
    const colors = ['neutral', 'purple', 'warm', 'blue'];
    let preference = { mode: 'light', color: 'neutral' };
    let warning = '';

    function readPreference() {
        try {
            const stored = JSON.parse(localStorage.getItem(storageKey) ?? 'null');
            if (stored === null) {
                return;
            }

            if (typeof stored !== 'object' || !['light', 'dark'].includes(stored.mode) || !colors.includes(stored.color)) {
                warning = '保存された表示設定を読み取れなかったため、標準の配色で表示しています。';
                return;
            }

            preference = { mode: stored.mode, color: stored.color };
        } catch {
            // ブラウザの保存制限や壊れた設定でも、画面自体は利用できる状態を保つ。
            warning = '表示設定を読み込めませんでした。この画面での変更は可能です。';
        }
    }

    function applyPreference() {
        root.dataset.theme = preference.mode;
        root.dataset.color = preference.color;
    }

    function initializeControls() {
        const mode = document.querySelector('#appearance-mode');
        const colorsField = document.querySelector('#light-colors');
        const colorInputs = [...document.querySelectorAll('[name="appearance-color"]')];
        const status = document.querySelector('#appearance-status');
        const warningElement = document.querySelector('#appearance-warning');
        if (!mode || !colorsField || !status || !warningElement) {
            return;
        }

        function updateControls() {
            mode.checked = preference.mode === 'dark';
            colorInputs.forEach(input => {
                input.checked = input.value === preference.color;
            });
            mode.disabled = false;
            colorsField.disabled = preference.mode === 'dark';
            warningElement.textContent = warning;
            warningElement.hidden = warning === '';
        }

        function savePreference() {
            preference = {
                mode: mode.checked ? 'dark' : 'light',
                color: colorInputs.find(input => input.checked)?.value ?? preference.color
            };
            applyPreference();

            try {
                localStorage.setItem(storageKey, JSON.stringify(preference));
                warning = '';
                status.textContent = '表示設定を保存しました。';
            } catch {
                warning = '表示設定を保存できませんでした。再読み込みすると変更が失われます。';
                status.textContent = '';
            }

            updateControls();
        }

        updateControls();
        mode.addEventListener('change', savePreference);
        colorInputs.forEach(input => input.addEventListener('change', savePreference));
    }

    // 初回描画より前に保存済みテーマを適用し、明暗の切替が一瞬見えることを防ぐ。
    readPreference();
    applyPreference();
    document.addEventListener('DOMContentLoaded', initializeControls);
})();
