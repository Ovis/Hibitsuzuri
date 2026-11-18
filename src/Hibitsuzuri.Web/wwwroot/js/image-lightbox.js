const lightbox = document.querySelector('#image-lightbox');
const enlarged = lightbox.querySelector('img');
let opener;

document.addEventListener('click', event => {
    const trigger = event.target.closest('[data-image-src]');
    if (!trigger) {
        return;
    }

    const source = trigger.dataset.imageSrc;
    if (!/^\/images\/[a-f0-9]{32}$/.test(source)) {
        return;
    }

    opener = trigger;
    event.preventDefault();
    enlarged.src = source;
    lightbox.showModal();
});

lightbox.querySelector('[data-close-image]').addEventListener('click', () => {
    lightbox.close();
});

lightbox.addEventListener('click', event => {
    const bounds = lightbox.getBoundingClientRect();
    if (event.target === lightbox && (event.clientX < bounds.left || event.clientX > bounds.right
        || event.clientY < bounds.top || event.clientY > bounds.bottom)) {
        lightbox.close();
    }
});

lightbox.addEventListener('close', () => {
    enlarged.removeAttribute('src');
    opener?.focus();
});
