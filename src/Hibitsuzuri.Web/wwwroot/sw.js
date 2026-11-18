"use strict";

const publicCache = "hibitsuzuri-public-v1";
const publicAssets = ["/offline.html", "/css/offline.css", "/js/offline.js", "/images/icon-192.png", "/images/icon-512.png"];

self.addEventListener("install", event => {
    // 編集画面を新しいworkerへ強制移行しない。全画面を閉じてから更新する。
    event.waitUntil(caches.open(publicCache).then(cache => cache.addAll(publicAssets)));
});

self.addEventListener("activate", event => {
    event.waitUntil((async () => {
        for (const key of await caches.keys()) {
            if (key.startsWith("hibitsuzuri-public-") && key !== publicCache) {
                await caches.delete(key);
            }
        }

        await self.clients.claim();
    })());
});

self.addEventListener("fetch", event => {
    const request = event.request;
    if (request.method !== "GET" || new URL(request.url).origin !== self.location.origin || new URL(request.url).pathname.startsWith("/api/")) {
        return;
    }

    if (request.mode === "navigate") {
        event.respondWith((async () => {
            try {
                // HTTPエラーはそのまま返す。接続失敗した画面遷移だけを案内へ置き換える。
                return await fetch(request);
            } catch (error) {
                const fallback = await caches.match("/offline.html");
                if (!fallback) {
                    throw error;
                }

                return new Response(await fallback.arrayBuffer(), {
                    status: 503,
                    headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" }
                });
            }
        })());
    } else if (publicAssets.includes(new URL(request.url).pathname)) {
        event.respondWith(caches.match(new URL(request.url).pathname).then(cached => cached || fetch(request)));
    }
});

function database() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open("hibitsuzuri-push", 1);
        request.onupgradeneeded = () => request.result.createObjectStore("state");
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

async function state(action) {
    const db = await database();
    try {
        return await new Promise((resolve, reject) => {
            const transaction = db.transaction("state", "readwrite");
            const store = transaction.objectStore("state");
            const request = action(store);
            transaction.oncomplete = () => resolve(request?.result);
            transaction.onerror = () => reject(transaction.error);
            transaction.onabort = () => reject(transaction.error);
        });
    } finally {
        db.close();
    }
}

self.addEventListener("message", event => {
    if (event.data?.type !== "push-binding" || !event.source?.url || new URL(event.source.url).origin !== self.location.origin) {
        return;
    }

    event.waitUntil((async () => {
        await state(store => store.put(event.data.binding || null, "binding"));
        for (const notification of await self.registration.getNotifications()) {
            if (!event.data.binding || notification.data?.generation !== event.data.binding.generation) {
                notification.close();
            }
        }

        event.ports[0]?.postMessage({ saved: true });
    })());
});

self.addEventListener("push", event => {
    event.waitUntil((async () => {
        const message = event.data?.json();
        const binding = await state(store => store.get("binding"));
        if (!message || !binding || binding.id !== message.device || binding.generation !== message.generation || message.expires <= Date.now()) {
            return;
        }

        const receipt = new URL(message.receipt, self.location.origin);
        if (receipt.origin !== self.location.origin || !receipt.pathname.startsWith("/api/push/receipt/")) {
            return;
        }

        // 強制失効後の配送待ち通知も止める。確認できない間は個人情報を表示しない。
        const response = await fetch(receipt, { cache: "no-store", credentials: "omit" });
        if (!response.ok) {
            return;
        }

        const permission = await response.json();
        if (!permission.allowed) {
            return;
        }

        const current = await state(store => store.get("binding"));
        if (!current || current.id !== binding.id || current.generation !== binding.generation || message.expires <= Date.now()) {
            return;
        }

        const seen = await state(store => store.get("seen")) || [];
        if (seen.some(item => item.id === message.id)) {
            return;
        }

        await self.registration.showNotification(message.title, {
            body: permission.showName || message.url === "/settings/notifications" ? message.body : "タスクの予定時刻になりました。",
            icon: "/images/icon-192.png",
            tag: message.id,
            renotify: false,
            data: { url: message.url, generation: message.generation }
        });
        await state(store => store.put([...seen.filter(item => item.expires > Date.now()), { id: message.id, expires: message.expires }], "seen"));
    })());
});

self.addEventListener("notificationclick", event => {
    event.notification.close();
    event.waitUntil((async () => {
        const url = new URL(event.notification.data?.url || "/tasks", self.location.origin);
        if (url.origin === self.location.origin && ["/tasks", "/settings/notifications"].includes(url.pathname)) {
            // 開いている日記やタスクの未保存入力を別のURLへの移動で失わせない。
            await self.clients.openWindow(url.href);
        }
    })());
});
