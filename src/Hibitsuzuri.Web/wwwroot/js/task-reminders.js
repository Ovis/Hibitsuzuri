"use strict";

window.TaskReminders = (() => {
    let root;
    let recurring = false;
    let sequence = 0;

    function read() {
        return [...root.querySelectorAll("[data-reminder]")].map(row => ({
            kind: row.querySelector("select").value,
            daysBefore: Number(row.querySelector('[type="number"]').value),
            time: row.querySelector('[type="time"]').value,
            at: row.querySelector('[type="datetime-local"]').value
        }));
    }

    function update(repeat) {
        recurring = repeat;
        for (const row of root.querySelectorAll("[data-reminder]")) {
            const select = row.querySelector("select");
            select.querySelector('[value="absolute"]').disabled = repeat;
            const absolute = select.value === "absolute";
            for (const type of ["number", "time", "datetime-local"]) {
                const input = row.querySelector(`[type="${type}"]`);
                const enabled = type === "datetime-local" ? absolute : !absolute;
                input.closest("label").hidden = !enabled;
                input.required = enabled;
                input.disabled = !enabled;
            }
        }

        root.querySelector("[data-reminder-add]").disabled = read().length >= 3;
    }

    function add(item = {}) {
        const row = document.createElement("div");
        row.dataset.reminder = "";
        row.className = "task-reminder";
        // 固定テンプレートだけをHTMLにし、利用者の値はvalueで設定する。
        row.innerHTML = '<label>指定方法<select><option value="relative">期限から指定</option><option value="absolute">日時を指定</option></select></label>'
            + '<label>何日前<input type="number" min="0" max="365" value="0"></label>'
            + '<label>時刻<input type="time" value="09:00"></label>'
            + '<label>日時<input type="datetime-local"></label><button type="button">削除</button>';
        row.querySelector("select").value = item.kind || "relative";
        row.querySelector('[type="number"]').value = item.daysBefore || 0;
        row.querySelector('[type="time"]').value = item.time || "09:00";
        row.querySelector('[type="datetime-local"]').value = item.at || "";
        for (const [index, input] of [...row.querySelectorAll("input,select")].entries()) {
            input.name = `reminder-${sequence}-${index}`;
        }

        sequence++;
        row.querySelector("button").addEventListener("click", () => {
            row.remove();
            update(recurring);
        });
        row.querySelector("select").addEventListener("change", () => update(recurring));
        root.querySelector("[data-reminder-list]").append(row);
        update(recurring);
    }

    function set(items) {
        root.querySelector("[data-reminder-list]").replaceChildren();
        for (const item of items || []) {
            add(item);
        }

        update(recurring);
    }

    function init(element) {
        root = element;
        root.querySelector("[data-reminder-add]").addEventListener("click", () => {
            if (read().length < 3) {
                add();
            }
        });
    }

    function describe(items) {
        return items.length ? items.map(item => item.kind === "absolute" ? item.at.replace("T", " ") : `期限の${item.daysBefore}日前 ${item.time}`).join(" / ") : "通知なし";
    }

    return { init, set, read, update, describe };
})();
