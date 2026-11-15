document.querySelectorAll('[data-book-picker]').forEach(picker => {
    document.addEventListener('click', event => {
        if (!picker.contains(event.target)) {
            picker.open = false;
        }
    });
    picker.addEventListener('keydown', event => {
        if (event.key === 'Escape') {
            picker.open = false;
            picker.querySelector('summary').focus();
        }
    });
});

document.querySelectorAll('[data-open-dialog]').forEach(button => {
    button.addEventListener('click', () => {
        document.getElementById(button.dataset.openDialog).showModal();
    });
});

document.querySelectorAll('[data-close-dialog]').forEach(button => {
    button.addEventListener('click', () => button.closest('dialog').close());
});

// 詳細セクションへのリンクでは、対象を開いてからスクロール位置を確定する。
function revealFragment() {
    const target = document.getElementById(location.hash.slice(1));
    if (target instanceof HTMLDetailsElement) {
        target.open = true;
        target.scrollIntoView();
    }
}

revealFragment();
window.addEventListener('hashchange', revealFragment);

document.querySelectorAll('[data-history-select]').forEach(select => {
    select.addEventListener('change', () => {
        const dialog = select.closest('dialog');
        dialog.querySelectorAll('[data-history-preview]').forEach(preview => {
            preview.hidden = preview.dataset.historyPreview !== select.value;
        });

        const edit = dialog.querySelector('[data-history-edit]');
        edit.href = edit.dataset.editPath + (select.value === 'current' ? '' : '?revision=' + encodeURIComponent(select.value));
    });
});

document.querySelectorAll('[data-month-picker] input[type="month"]').forEach(input => {
    input.addEventListener('change', () => {
        if (input.checkValidity()) {
            input.form.requestSubmit();
        }
    });
});
