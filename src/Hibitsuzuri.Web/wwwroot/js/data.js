(() => {
    "use strict";

    const upload = document.querySelector("#restore-upload");
    const confirm = document.querySelector("#restore-confirm");
    const file = document.querySelector("#restore-file");
    const status = document.querySelector("#restore-status");
    const preview = document.querySelector("#restore-preview");
    const refresh = document.querySelector("#restore-refresh");
    const cancel = document.querySelector("#restore-cancel");
    const accepted = document.querySelector("#restore-accepted");
    const token = upload.querySelector('[name="__RequestVerificationToken"]').value;
    const key = `hibitsuzuri.restore.${document.documentElement.dataset.owner}`;
    let operation = sessionStorage.getItem(key);
    let busy = false;
    let completed = false;

    async function request(path, body) {
        const response = await fetch(path, {
            method: body === undefined ? "GET" : "POST",
            headers: body === undefined ? {} : { RequestVerificationToken: token },
            body
        });
        if (response.redirected || !response.headers.get("content-type")?.includes("application/json")) {
            throw new Error("ログイン状態または通信を確認し、状態を確認してください。");
        }

        const result = await response.json();
        if (!response.ok) {
            throw new Error(result.error || "処理に失敗しました。状態を確認してください。");
        }

        return result;
    }

    function show(result) {
        completed = result.state === "completed";
        preview.replaceChildren();
        preview.hidden = true;
        confirm.hidden = true;
        accepted.checked = false;
        cancel.hidden = true;
        upload.hidden = false;

        if (result.id) {
            operation = result.id;
            sessionStorage.setItem(key, operation);
        }

        if (result.state === "none") {
            operation = null;
            sessionStorage.removeItem(key);
            status.textContent = "確認待ちの復元はありません。";
        } else if (result.state === "completed") {
            status.textContent = "復元が完了しました。日記帳から記録を確認できます。通知は全端末で停止しています。";
        } else if (result.state === "ready") {
            upload.hidden = true;
            cancel.hidden = false;
            preview.hidden = false;
            const counts = document.createElement("p");
            counts.textContent = `日記 ${result.entries}件／履歴 ${result.revisions}版／画像 ${result.images}件／タスク操作 ${result.tasks}件／繰り返し ${result.series}件／記念日 ${result.anniversaries}件`;
            const names = document.createElement("details");
            const summary = document.createElement("summary");
            summary.textContent = `復元する日記帳：${result.books.length}冊`;
            const list = document.createElement("ul");
            for (const book of result.books) {
                const row = document.createElement("li");
                row.textContent = `${book.name}（${book.archived ? "アーカイブ" : "使用中"}・タスク${book.paused ? "停止" : "継続"}）`;
                list.append(row);
            }

            names.append(summary, list);
            preview.append(counts, names);
            if (result.missingImages > 0) {
                const missing = document.createElement("p");
                missing.textContent = `元のデータにも実体がない画像参照が${result.missingImages}件あります。本文には参照を残し、画像は復元しません。`;
                preview.append(missing);
            }

            status.textContent = result.problem || "内容を確認しました。復元する前に対象を確認してください。";
            confirm.hidden = Boolean(result.problem);
        } else {
            cancel.hidden = false;
            upload.hidden = result.state === "expired";
            status.textContent = result.state === "expired"
                ? "確認期限が過ぎました。取り消して、もう一度アップロードしてください。"
                : "アップロード待ち、または検証中です。失敗した場合はファイルを再選択できます。";
        }
    }

    async function run(action) {
        if (busy) {
            return;
        }

        busy = true;
        const buttons = document.querySelectorAll("#restore-panel button");
        buttons.forEach(button => { button.disabled = true; });
        try {
            await action();
        } catch (error) {
            status.textContent = error instanceof Error ? error.message : "処理を確認できませんでした。状態を確認してください。";
        } finally {
            busy = false;
            buttons.forEach(button => { button.disabled = false; });
        }
    }

    async function check() {
        show(await request(`/api/data/restore/status${operation ? `?id=${encodeURIComponent(operation)}` : ""}`));
    }

    upload.addEventListener("submit", event => {
        event.preventDefault();
        run(async () => {
            const selected = file.files[0];
            if (!selected || selected.size > 2000000000) {
                throw new Error("2GB以内のZIPを選択してください。");
            }

            if (!operation || completed) {
                const started = await request("/api/data/restore", new URLSearchParams());
                operation = started.id;
                sessionStorage.setItem(key, operation);
            }

            status.textContent = "アップロードして内容を検証しています。この画面を開いたままお待ちください。";
            show(await request(`/api/data/restore/${operation}/upload`, selected));
        });
    });

    confirm.addEventListener("submit", event => {
        event.preventDefault();
        if (!operation || !accepted.checked) {
            return;
        }

        run(async () => {
            status.textContent = "復元しています。通信が切れても、状態の確認で結果を確認できます。";
            show(await request(`/api/data/restore/${operation}/confirm`, new URLSearchParams({ confirmed: "true" })));
        });
    });

    refresh.addEventListener("click", () => run(check));
    cancel.addEventListener("click", () => run(async () => {
        if (operation) {
            show(await request(`/api/data/restore/${operation}/cancel`, new URLSearchParams()));
        }
    }));

    run(check);
})();
