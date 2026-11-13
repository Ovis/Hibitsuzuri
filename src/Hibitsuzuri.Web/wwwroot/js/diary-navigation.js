(() => {
    'use strict';

    document.querySelectorAll('form[data-confirm]').forEach(form => {
        form.addEventListener('submit', event => {
            if (!window.confirm(form.dataset.confirm)) {
                event.preventDefault();
            }
        });
    });

    document.querySelector('[data-day-picker]')?.addEventListener('submit', event => {
        event.preventDefault();
        const day = document.querySelector('#selected-day').value;
        if (/^\d{4}-\d{2}-\d{2}$/.test(day)) {
            window.location.assign(`${event.currentTarget.getAttribute('action')}/${day}`);
        }
    });
})();
