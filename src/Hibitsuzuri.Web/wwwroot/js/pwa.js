"use strict";

window.HibitsuzuriPwa = (() => {
    let installPrompt = null;
    const supported = "serviceWorker" in navigator && window.isSecureContext;
    const registration = supported ? navigator.serviceWorker.register("/sw.js", { updateViaCache: "none" }) : Promise.resolve(null);
    // 登録失敗を画面の通知設定へ伝え、通常の日記操作には影響させない。
    registration.catch(error => console.warn("アプリ機能を登録できませんでした。", error.name));

    async function ready() {
        let timer;
        try {
            return await Promise.race([
                registration.then(() => navigator.serviceWorker.ready),
                new Promise((resolve, reject) => {
                    timer = setTimeout(() => reject(new Error("アプリ機能の準備を確認できませんでした。接続を確認してお試しください。")), 5000);
                })
            ]);
        } finally {
            clearTimeout(timer);
        }
    }

    async function bind(binding) {
        if (!supported) {
            return;
        }

        const active = await ready();
        await new Promise((resolve, reject) => {
            const channel = new MessageChannel();
            const timer = setTimeout(() => reject(new Error("ブラウザの通知設定を保存できませんでした。")), 5000);
            channel.port1.onmessage = () => {
                clearTimeout(timer);
                channel.port1.close();
                resolve();
            };
            active.active.postMessage({ type: "push-binding", binding }, [channel.port2]);
        });
    }

    window.addEventListener("beforeinstallprompt", event => {
        event.preventDefault();
        installPrompt = event;
        document.querySelector("[data-pwa-install]")?.removeAttribute("hidden");
    });
    document.querySelector("[data-pwa-install]")?.addEventListener("click", async () => {
        if (installPrompt) {
            await installPrompt.prompt();
            installPrompt = null;
            document.querySelector("[data-pwa-install]").hidden = true;
        }
    });
    window.addEventListener("appinstalled", () => {
        const status = document.querySelector("[data-pwa-status]");
        if (status) {
            status.textContent = "アプリをインストールしました。";
        }
    });

    async function synchronize() {
        const owner = document.documentElement.dataset.owner;
        if (!owner || !supported) {
            return;
        }

        const response = await fetch("/api/push/device", { cache: "no-store" });
        if (!response.ok) {
            return;
        }

        const current = await response.json();
        // 古いアカウントを表示したままの別タブから購読を上書きしない。
        if (current.owner === owner) {
            await bind(current.device?.enabled ? { id: current.device.id, generation: current.device.generation } : null);
        }
    }

    synchronize().catch(error => console.warn("通知の状態を同期できませんでした。", error.name));
    return { registration, ready, bind };
})();
