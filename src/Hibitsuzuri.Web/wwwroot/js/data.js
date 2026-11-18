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
    const options = document.querySelector("#restore-options");
    const modes = document.querySelector("#restore-modes");
    const targets = document.querySelector("#restore-targets");
    const confirmLabel = document.querySelector("#restore-confirm-label");
    const token = upload.querySelector('[name="__RequestVerificationToken"]').value;
    const key = `hibitsuzuri.restore.${document.documentElement.dataset.owner}`;
    let operation = sessionStorage.getItem(key);
    let busy = false;
    let completed = false;
    let planToken = null;
    let markdown = false;

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
        planToken = result.planToken || null;
        options.hidden = true;
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
            status.textContent = "復元が完了しました。日記帳から記録を確認できます。通知端末の設定は維持されています。";
        } else if (result.state === "ready") {
            markdown = result.markdown;
            options.hidden = false;
            modes.hidden = markdown;
            targets.hidden = !markdown;
            targets.replaceChildren();
            if (result.mode && !markdown) {
                options.elements.mode.value = result.mode;
            }

            if (markdown) {
                for (const book of result.books) {
                    const label = document.createElement("label");
                    const select = document.createElement("select");
                    select.id = `restore-target-${book.id}`;
                    select.name = book.id;
                    label.htmlFor = select.id;
                    label.textContent = `「${book.name}」の取り込み先`;
                    select.add(new Option("新しい日記帳を作る", ""));
                    for (const existing of result.existing) {
                        select.add(new Option(`${existing.name}${existing.archived ? "（アーカイブ）" : ""}`, existing.id));
                    }

                    select.value = book.target || "";
                    targets.append(label, select);
                }
            }

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
                row.textContent = book.destination
                    ? `${book.name} → ${book.destination}${book.target ? "（同日の本文・履歴を置き換え）" : "（新規作成）"}`
                    : `${book.name}（${book.archived ? "アーカイブ" : "使用中"}・タスク${book.paused ? "停止" : "継続"}）`;
                list.append(row);
            }

            names.append(summary, list);
            preview.append(counts, names);
            if (result.missingImages > 0) {
                const missing = document.createElement("p");
                missing.textContent = `ZIPに実体のない画像参照が${result.missingImages}件あります。${markdown ? "同じアカウント内に元画像が残っていれば再利用します。" : "本文には参照を残し、画像は復元しません。"}`;
                preview.append(missing);
            }

            confirmLabel.textContent = markdown
                ? "選択した日記帳の同日の本文・編集履歴を置き換える（元の履歴には戻せません）"
                : result.mode === "replace"
                    ? "ゴミ箱を含む現在の記録・画像を削除し、このバックアップに置き換える（元に戻せません）"
                    : "既存の記録を残し、表示された日記帳を追加する";
            if (result.replacing) {
                const warning = document.createElement("p");
                warning.className = "validation-summary-errors";
                warning.textContent = `現在の日記帳${result.replacing.books}冊・日記${result.replacing.entries}件・画像${result.replacing.images}件と、履歴・テンプレート・タスク・記念日を、ゴミ箱も含めて削除して置き換えます。${result.includesImages ? "" : "画像なしのバックアップのため、現在の画像も失われます。"}`;
                preview.append(warning);
            }

            status.textContent = planToken ? "変更内容を確認し、下の確認欄にチェックして復元してください。" : "復元方法・取り込み先を選び、復元内容を確認してください。";
            confirm.hidden = !planToken;
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
        options.querySelectorAll("input, select").forEach(control => { control.disabled = true; });
        try {
            await action();
        } catch (error) {
            status.textContent = error instanceof Error ? error.message : "処理を確認できませんでした。状態を確認してください。";
        } finally {
            busy = false;
            buttons.forEach(button => { button.disabled = false; });
            options.querySelectorAll("input, select").forEach(control => { control.disabled = false; });
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
        if (!operation || !accepted.checked || !planToken) {
            return;
        }

        run(async () => {
            status.textContent = "復元しています。通信が切れても、状態の確認で結果を確認できます。";
            show(await request(`/api/data/restore/${operation}/confirm`, new URLSearchParams({ confirmed: "true", planToken })));
        });
    });

    options.addEventListener("change", () => {
        planToken = null;
        confirm.hidden = true;
        accepted.checked = false;
        status.textContent = "選択を変更しました。復元内容をもう一度確認してください。";
    });

    options.addEventListener("submit", event => {
        event.preventDefault();
        const selected = Object.fromEntries([...targets.querySelectorAll("select")].map(select => [select.name, select.value]));
        const mode = markdown ? "markdown" : options.elements.mode.value;
        run(async () => {
            show(await request(`/api/data/restore/${operation}/preview`, new URLSearchParams({ mode, targets: markdown ? JSON.stringify(selected) : "null" })));
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
