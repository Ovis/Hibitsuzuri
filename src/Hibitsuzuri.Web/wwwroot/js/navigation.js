document.querySelectorAll('[data-book-switch]').forEach(select => {
    select.addEventListener('change', () => {
        window.location.assign(select.value);
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
