"use strict";

(() => {
    const root = document.querySelector("[data-notification-settings]");
    const status = root.querySelector("[data-push-status]");
    const name = root.querySelector("[data-push-name]");
    const enable = root.querySelector("[data-push-enable]");
    const disable = root.querySelector("[data-push-disable]");
    const test = root.querySelector("[data-push-test]");
    const pwa = window.HibitsuzuriPwa;
    const supported = window.isSecureContext && "PushManager" in window && "Notification" in window && "serviceWorker" in navigator;
    let current;
    let busy = false;

    async function request(url, body) {
        const response = await fetch(url, body === undefined ? { cache: "no-store" } : {
            method: "POST",
            headers: { "Content-Type": "application/json", RequestVerificationToken: root.querySelector('input[name="__RequestVerificationToken"]').value },
            body: JSON.stringify(body)
        });
        if (!response.ok) {
            const result = await response.json().catch(() => null);
            throw new Error(result?.message || (response.status === 401 ? "再ログインしてからお試しください。" : "設定を保存できませんでした。接続を確認してください。"));
        }

        return response.json();
    }

    function buttons() {
        enable.disabled = busy || !current || !supported || Notification.permission === "denied";
        disable.disabled = busy || !current?.device?.enabled;
        test.disabled = busy || !current?.device?.enabled || !supported || Notification.permission !== "granted";
        name.disabled = busy;
    }

    async function load() {
        current = await request("/api/push/device");
        name.checked = Boolean(current.device?.showName);
        root.querySelector("[data-push-zone]").textContent = current.timeZone;
        status.textContent = !supported ? "この環境では通知を利用できません。HTTPSでChromeまたはEdgeを開いてください。"
            : Notification.permission === "denied" ? "ブラウザで通知が拒否されています。アドレス欄のサイト設定から通知を許可した後、有効にしてください。"
                : current.device?.error || (current.device?.enabled ? "このブラウザへの通知は有効です。" : "このブラウザへの通知は停止しています。");
        buttons();
    }

    async function act(action) {
        if (busy) {
            return;
        }

        busy = true;
        buttons();
        try {
            const message = await action();
            await load();
            if (message) {
                status.textContent = message;
            }
        } catch (error) {
            status.textContent = error.message || "接続を確認してもう一度お試しください。";
        } finally {
            busy = false;
            buttons();
        }
    }

    function publicKey(value) {
        const encoded = value.replace(/-/g, "+").replace(/_/g, "/");
        return Uint8Array.from(atob(encoded.padEnd(Math.ceil(encoded.length / 4) * 4, "=")), character => character.charCodeAt(0));
    }

    enable.addEventListener("click", () => {
        // ブラウザの許可要求は利用者のクリックに直接つなぐ。
        const permission = Notification.requestPermission();
        act(async () => {
            if (await permission !== "granted") {
                throw new Error("通知が許可されませんでした。ブラウザのサイト設定を確認してください。");
            }

            const registration = await pwa.ready();
            let subscription = await registration.pushManager.getSubscription();
            const key = publicKey(current.publicKey);
            const previousKey = subscription?.options.applicationServerKey;
            if (subscription && previousKey && !new Uint8Array(previousKey).every((byte, index) => byte === key[index])) {
                await subscription.unsubscribe();
                subscription = null;
            }

            subscription ||= await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key });
            const binding = await request("/api/push/enable", { subscription: subscription.toJSON(), showName: name.checked });
            await pwa.bind(binding);
        });
    });
    disable.addEventListener("click", () => act(async () => {
        await request("/api/push/disable", {});
        await pwa.bind(null);
    }));
    name.addEventListener("change", () => {
        if (current?.device) {
            act(async () => {
                await request("/api/push/privacy", { showName: name.checked });
            });
        }
    });
    test.addEventListener("click", () => act(async () => (await request("/api/push/test", {})).message));

    load().catch(error => {
        status.textContent = error.message;
        buttons();
    });
})();
