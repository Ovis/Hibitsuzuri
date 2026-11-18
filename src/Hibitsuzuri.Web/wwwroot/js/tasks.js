(() => {
    "use strict";

    const root = document.querySelector("[data-task-app]");
    if (!root) {
        return;
    }

    const select = selector => root.querySelector(selector);
    const filterForm = select("[data-task-filters]");
    const intro = document.querySelector("[data-task-list-intro]");
    const list = select("[data-task-list]");
    const status = select("[data-task-status]");
    const editor = select("[data-task-editor]");
    const detail = select("[data-task-detail]");
    const deletion = select("[data-task-delete]");
    const form = select("[data-task-form]");
    const field = name => form.elements.namedItem(name);
    const day = root.dataset.day;
    const today = root.dataset.today;
    const readOnly = root.dataset.readonly === "true";
    const token = select('input[name="__RequestVerificationToken"]').value;
    let mode = "pending";
    let kind = "all";
    let page = 1;
    let current = null;
    let initialForm = "";
    let saving = false;
    let opener = null;
    let revision = 0;
    let undo = null;
    let completedItems = [];
    let completedGroups = [];
    let repeatPreviewRevision = 0;
    const rules = new Map();
    const pending = new Map();
    const reminders = window.TaskReminders;
    reminders.init(select("[data-task-reminders]"));

    function node(tag, text, className) {
        const element = document.createElement(tag);
        if (text !== undefined) {
            element.textContent = text;
        }

        if (className) {
            element.className = className;
        }

        return element;
    }

    function button(text, action, className) {
        const element = node("button", text, className);
        element.type = "button";
        element.addEventListener("click", action);
        return element;
    }

    async function request(path, body) {
        const options = body === undefined ? {} : {
            method: "POST",
            headers: { "Content-Type": "application/json", RequestVerificationToken: token },
            body: JSON.stringify(body)
        };
        const response = await fetch(path, options);
        const result = await response.json().catch(() => null);
        if (!response.ok || !result) {
            throw new Error(result?.message || (response.status === 401
                ? "ログインし直してください。入力内容はこの画面に残っています。"
                : "読み込み・保存に失敗しました。接続を確認して再試行してください。"));
        }

        return result;
    }

    function parameters(seriesId, targetPage = page, targetMode = mode) {
        const result = new URLSearchParams(filterForm ? new FormData(filterForm) : { bookId: root.dataset.book, day });
        // 未入力の日付は空文字のまま送ると、日付型の検証エラーになる。
        for (const [key, value] of [...result.entries()]) {
            if (!value) {
                result.delete(key);
            }
        }

        result.set("mode", targetMode);
        result.set("kind", kind);
        if (["dated", "undated"].includes(result.get("quick"))) {
            result.set("kind", result.get("quick"));
            result.set("quick", "all");
        }
        result.set("page", targetPage);
        if (targetMode === "completed") {
            result.delete("through");
            if (result.get("period") !== "custom") {
                result.delete("start");
                result.delete("end");
            }
        } else {
            for (const name of ["period", "start", "end"]) {
                result.delete(name);
            }
        }

        if (seriesId) {
            result.set("seriesId", seriesId);
        }

        return result;
    }

    async function load(append = false) {
        if (mode === "completed" && !append) {
            page = 1;
        }

        const requestRevision = ++revision;
        status.textContent = "読み込み中…";
        try {
            // 完了履歴の操作や検索後も、未完了タブを開かずに同じ条件の件数を更新する。
            const [data, pendingData] = await Promise.all([
                request("/api/tasks?" + parameters()),
                mode === "completed" && !day ? request("/api/tasks?" + parameters(undefined, 1, "pending")) : null
            ]);
            if (requestRevision !== revision) {
                return;
            }

            for (const [id, rule] of Object.entries(data.rules)) {
                rules.set(id, rule);
            }

            if (mode === "completed" && !day) {
                completedItems = append ? [...completedItems, ...data.items] : data.items;
                completedGroups = append ? [...completedGroups, ...data.groups] : data.groups;
                data.items = [...new Map(completedItems.map(item => [item.id, item])).values()];
                data.groups = [...new Map(completedGroups.map(group => [group.id, group])).values()];
            }

            if (page > 1 && (page - 1) * 50 >= data.total) {
                page = Math.max(1, Math.ceil(data.total / 50));
                await load();
                return;
            }

            const expanded = new Set([...list.querySelectorAll("[data-series-id][open]")].map(element => element.dataset.seriesId));
            if (select("[data-pending-count]")) {
                select("[data-pending-count]").textContent = (pendingData || data).matched;
            }

            list.replaceChildren();
            for (const count of root.querySelectorAll("[data-count]")) {
                count.textContent = data.counts[count.dataset.count];
            }

            if (day) {
                const pastNotice = select("[data-past-task-notice]");
                if (pastNotice) {
                    pastNotice.hidden = data.counts.dated === 0;
                }

                const dated = data.items.filter(item => item.dueDate);
                const undated = data.items.filter(item => !item.dueDate);
                dated.forEach(item => list.append(row(item)));
                const section = node("details", undefined, "task-series");
                section.append(node("summary", "期限なし（" + data.counts.undated + "）"));
                undated.forEach(item => section.append(row(item)));
                if (undated.length) {
                    list.append(section);
                }
            } else if (mode === "completed") {
                const rendered = new Set();
                for (const item of data.items) {
                    if (!item.seriesId) {
                        list.append(row(item));
                    } else if (!rendered.has(item.seriesId)) {
                        rendered.add(item.seriesId);
                        list.append(groupRow(data.groups.find(group => group.id === item.seriesId), data.items.filter(value => value.seriesId === item.seriesId)));
                    }
                }
            } else {
                const entries = [...data.items.map(item => ({ date: item.dueDate, id: item.id, element: row(item) })),
                    ...data.groups.map(group => ({ date: group.first, id: group.id, element: groupRow(group) }))];
                entries.sort((left, right) => (left.date || "9999-99-99").localeCompare(right.date || "9999-99-99") || left.id.localeCompare(right.id));
                entries.forEach(entry => list.append(entry.element));
            }

            for (const section of list.querySelectorAll("[data-series-id]")) {
                section.open = expanded.has(section.dataset.seriesId);
            }

            if (day) {
                refreshCalendar(data.pendingDays);
            }

            const controls = select("[data-task-pagination]");
            if (mode === "completed" && !day) {
                controls.replaceChildren();
                if (data.page * 50 < data.total) {
                    controls.append(button("さらに表示", () => {
                        page = data.page + 1;
                        load(true);
                    }));
                }
            } else {
                pagination(controls, data, number => {
                    page = number;
                    load();
                });
            }

            const waiting = select("[data-task-waiting]");
            waiting.replaceChildren();
            for (const item of mode === "completed" && !day ? [] : data.waiting) {
                const notice = node("p", item.title + "：祝日データ待ち ", "notice");
                notice.append(button("設定を確認", () => showDetail(item.id)));
                waiting.append(notice);
            }

            status.textContent = data.matched === 0 ? "該当するタスクはありません。"
                : mode === "completed" && !day ? data.total + "件中 " + data.items.length + "件を表示" : "";
            summarize();
        } catch (error) {
            status.textContent = error.message;
        }
    }

    function taskState(item) {
        if (item.readOnly) {
            return "停止中";
        }
        if (item.completedAt) {
            return "完了";
        }
        if (item.waiting) {
            return "祝日データ待ち";
        }
        return item.dueDate === today ? "本日期限" : item.dueDate && item.dueDate < today ? "期限超過" : "未完了";
    }

    function repeatSummary(rule) {
        if (!rule) {
            return "";
        }
        const units = { daily: "日", weekly: "週間", monthly: "か月", yearly: "年" };
        let text = rule.interval === 1 ? { daily: "毎日", weekly: "毎週", monthly: "毎月", yearly: "毎年" }[rule.kind]
            : rule.interval + units[rule.kind] + "ごと";
        if (rule.kind === "weekly") {
            text += " " + [..."日月火水木金土"].filter((_, index) => rule.weekdays & (1 << index)).join("・");
        }
        if (rule.adjustment !== "none") {
            text += " ／ 休日調整あり";
        }
        return text;
    }

    function row(item) {
        const container = node("div", undefined, "task-row" + (item.completedAt ? " completed" : ""));
        const checkbox = node("input");
        checkbox.type = "checkbox";
        checkbox.checked = item.completedAt !== null;
        checkbox.disabled = readOnly || item.readOnly || saving;
        checkbox.setAttribute("aria-label", item.title + "を" + (item.completedAt ? "未完了に戻す" : "完了する"));
        checkbox.addEventListener("change", () => complete(item, checkbox.checked, checkbox));
        const main = button("", () => showDetail(item.id), "task-row-main");
        main.append(node("span", item.title, "task-row-title"));
        const due = item.dueDate ? "期限 " + item.dueDate : "期限なし";
        const caption = due + " ・ " + taskState(item)
            + (!day && !filterForm.elements.namedItem("bookId").value ? " ・ " + item.bookName : "");
        if (item.completedAt) {
            main.append(node("small", "完了 " + new Date(item.completedAt).toLocaleString("ja-JP"), "muted"));
        }
        main.append(node("small", caption, !item.completedAt && item.dueDate && item.dueDate < today ? "task-overdue" : "muted"));
        if (item.seriesId) {
            main.append(node("small", "↻ " + repeatSummary(rules.get(item.segmentId)), "muted"));
        }
        container.append(checkbox, main);
        if (!readOnly && !item.readOnly) {
            container.append(button("編集", async () => {
                opener = document.activeElement;
                try {
                    const existing = await request("/api/tasks/item?id=" + encodeURIComponent(item.id));
                    showEditor(existing);
                } catch (error) {
                    status.textContent = error.message;
                }
            }, "quiet small"));
        }

        return container;
    }

    function groupRow(group, completedRows) {
        const section = node("details", undefined, "task-series");
        section.dataset.seriesId = group.id;
        const heading = node("summary");
        heading.append(node("span", group.title));
        heading.append(node("small", (mode === "pending" && group.next ? "次の期限 " + group.next + " · " : "")
            + "一致する" + (mode === "pending" ? "未完了 " : "完了 ") + group.count + "回"
            + (group.overdue ? " · 期限超過 " + group.overdue + "回" : "") + " · " + group.bookName, "muted"));
        section.append(heading);
        const content = node("div");
        section.append(content);
        let loaded = false;
        async function expand(number) {
            if (completedRows) {
                content.replaceChildren(...completedRows.map(row));
                if (completedRows.length < group.count) {
                    content.prepend(node("p", "期間内 " + group.count + "件中 " + completedRows.length + "件を表示しています。", "muted"));
                }

                loaded = true;
                return;
            }

            content.replaceChildren(node("p", "読み込み中…"));
            try {
                const data = await request("/api/tasks?" + parameters(group.id, number));
                for (const [id, rule] of Object.entries(data.rules)) {
                    rules.set(id, rule);
                }

                content.replaceChildren(...data.items.map(row));
                const controls = node("div", undefined, "task-pagination");
                pagination(controls, data, expand);
                content.append(controls);
                loaded = true;
            } catch (error) {
                content.replaceChildren(node("p", error.message), button("再試行", () => expand(number)));
            }
        }

        section.addEventListener("toggle", () => {
            if (section.open && !loaded) {
                expand(1);
            }
        });
        return section;
    }

    function refreshCalendar(dates) {
        const pendingDates = new Set(dates);
        for (const cell of document.querySelectorAll("[data-calendar-date]")) {
            const pending = pendingDates.has(cell.dataset.calendarDate);
            const previous = cell.querySelector("[data-task-marker]");
            if (pending && !previous) {
                const marker = node("span", undefined, "task-marker");
                const icon = document.querySelector('[data-calendar-task-key] .icon');
                if (icon) {
                    marker.append(icon.cloneNode(true));
                }
                marker.dataset.taskMarker = "";
                marker.setAttribute("aria-hidden", "true");
                cell.append(marker);
            } else if (!pending) {
                previous?.remove();
            }

            const label = cell.getAttribute("aria-label").replace("未完了タスクあり", "").trim();
            cell.setAttribute("aria-label", label + (pending ? " 未完了タスクあり" : ""));
        }
    }

    function pagination(target, data, change) {
        target.replaceChildren();
        if (data.total <= 50) {
            return;
        }

        const previous = button("前の50件", () => change(data.page - 1));
        previous.disabled = data.page <= 1;
        const next = button("次の50件", () => change(data.page + 1));
        next.disabled = data.page * 50 >= data.total;
        target.append(previous, node("span", data.page + " / " + Math.ceil(data.total / 50)), next);
    }

    function summarize() {
        if (!filterForm) {
            return;
        }

        const values = new FormData(filterForm);
        const book = filterForm.elements.namedItem("bookId");
        const summary = [];
        if (!book.value) {
            summary.push("全日記帳");
        } else if (book.value !== root.dataset.book) {
            summary.push(book.selectedOptions[0].textContent);
        }

        if (values.get("query")) {
            summary.push("「" + values.get("query") + "」");
        }

        const period = filterForm.elements.namedItem("period");
        summary.push(mode === "completed" ? "完了：" + period.selectedOptions[0].textContent : "繰り返し：" + values.get("through").replaceAll("-", "/") + "まで");
        const quick = filterForm.elements.namedItem("quick");
        if (quick.value !== "all") {
            summary.push(quick.selectedOptions[0].textContent);
        }

        select("[data-filter-summary]").textContent = summary.join(" ・ ");
    }

    function memoText(text) {
        const fragment = document.createDocumentFragment();
        const pattern = /https?:\/\/[^\s<>"']+/gu;
        let position = 0;
        for (const match of text.matchAll(pattern)) {
            fragment.append(document.createTextNode(text.slice(position, match.index)));
            const link = node("a", match[0]);
            link.href = match[0];
            link.target = "_blank";
            link.rel = "noopener noreferrer";
            fragment.append(link);
            position = match.index + match[0].length;
        }

        fragment.append(document.createTextNode(text.slice(position)));
        return fragment;
    }

    async function showDetail(id) {
        try {
            current = await request("/api/tasks/item?id=" + encodeURIComponent(id));
            opener = document.activeElement;
            const item = current.item;
            select("[data-detail-title]").textContent = item.title;
            const facts = select("[data-detail-meta]");
            facts.replaceChildren();
            const addFact = (label, value) => facts.append(node("dt", label), node("dd", value));
            addFact("日記帳", item.bookName);
            addFact("状態", taskState(item));
            addFact("期限", item.dueDate || (item.waiting ? "祝日データ待ち" : "期限なし"));
            if (item.completedAt) {
                addFact("完了日時", new Date(item.completedAt).toLocaleString("ja-JP"));
            }
            if (item.seriesId) {
                addFact("繰り返し", repeatSummary(current.rule));
                addFact("元の予定日", item.scheduledDate);
            }
            select("[data-detail-memo]").replaceChildren(memoText(item.memo || "メモはありません。"));
            addFact("リマインダー", reminders.describe(current.reminders) + "（" + current.timeZone + "）");
            if (current.notificationReview) {
                addFact("通知の再確認", "到来済みの通知は送信しません。通知が必要なら未来の時刻に変更してください。");
            }
            select("[data-edit-task]").hidden = readOnly || item.readOnly;
            select("[data-delete-task]").hidden = readOnly || item.readOnly;
            detail.showModal();
        } catch (error) {
            status.textContent = error.message;
        }
    }

    function setValue(name, value) {
        field(name).value = value ?? "";
    }

    function formSnapshot() {
        return JSON.stringify([...new FormData(form).entries()]);
    }

    function showEditor(existing) {
        detail.close();
        form.reset();
        current = existing;
        select("[data-task-error]").hidden = true;
        select("#task-editor-title").textContent = existing ? "タスクを編集" : "タスクを追加";
        select("[data-scope-field]").hidden = !existing?.item.seriesId;
        field("notebookId").disabled = Boolean(existing) || Boolean(day);
        setValue("notebookId", existing?.item.notebookId || filterForm?.elements.namedItem("bookId").value || root.dataset.book || field("notebookId").options[0]?.value);
        setValue("title", existing?.item.title);
        setValue("memo", existing?.item.memo);
        setValue("dueDate", existing ? existing.item.dueDate : day);
        setValue("startDate", existing?.item.scheduledDate || day || today);
        setValue("monthDay", Number((day || today).slice(8)));
        setValue("month", Number((day || today).slice(5, 7)));
        setValue("scope", existing?.item.waiting ? "following" : "once");
        field("repeating").checked = Boolean(existing?.rule);
        setValue("repeatChoice", existing?.rule?.kind || "none");
        select("[data-delete-task]").hidden = !existing || readOnly || existing.item.readOnly;
        reminders.set(existing?.reminders || []);

        const rule = existing?.rule;
        if (rule) {
            setValue("repeatKind", rule.kind);
            for (const name of ["interval", "monthMode", "monthDay", "ordinal", "weekday", "month", "endOffset", "missing", "adjustment", "until", "count"]) {
                if (rule[name] !== null && rule[name] !== undefined) {
                    setValue(name, rule[name]);
                }
            }

            for (const checkbox of form.querySelectorAll('input[name="weekdays"]')) {
                checkbox.checked = Boolean(rule.weekdays & (1 << Number(checkbox.value)));
            }

            field("avoidWeekends").checked = rule.avoidWeekends;
            field("avoidHolidays").checked = rule.avoidHolidays;
            setValue("ending", rule.until ? "until" : rule.count ? "count" : "none");
        }

        updateEditor();
        initialForm = formSnapshot();
        editor.showModal();
        field("title").focus();
    }

    function visible(selector, condition) {
        for (const element of root.querySelectorAll(selector)) {
            element.hidden = !condition;
        }
    }

    function updateEditor() {
        field("repeating").checked = field("repeatChoice").value !== "none";
        if (field("repeating").checked) {
            setValue("repeatKind", field("repeatChoice").value);
        }
        const once = Boolean(current?.item.seriesId) && field("scope").value === "once";
        const repeat = field("repeating").checked && !once;
        reminders.update(repeat);
        visible("[data-repeat-toggle]", !once);
        visible("[data-repeat-fields]", repeat);
        visible("[data-due-field]", !repeat);
        visible("[data-due-help]", !repeat && !once);
        select("[data-repeat-fields]").disabled = !repeat;
        visible("[data-scope-notice]", Boolean(current?.item.seriesId) && !once);
        select("[data-scope-notice]").textContent = "今回以降の未操作の予定を置き換えます。完了・個別編集済み " + (current?.retained || 0)
            + "件と手動スキップは元の系列に残ります。";
        const repeatKind = field("repeatKind").value;
        visible("[data-weekly]", repeatKind === "weekly");
        visible("[data-monthly]", repeatKind === "monthly" || repeatKind === "yearly");
        visible("[data-yearly]", repeatKind === "yearly");
        visible("[data-month-day]", field("monthMode").value === "day");
        visible("[data-month-weekday]", field("monthMode").value === "weekday");
        visible("[data-month-end]", field("monthMode").value === "end");
        visible("[data-ending-until]", field("ending").value === "until");
        visible("[data-ending-count]", field("ending").value === "count");
        field("until").required = repeat && field("ending").value === "until";
        field("count").required = repeat && field("ending").value === "count";
        updateRepeatPreview(repeat);
    }

    function closeEditor() {
        if (saving || (formSnapshot() !== initialForm && !window.confirm("入力中の内容を破棄して閉じますか？"))) {
            return;
        }

        editor.close();
        opener?.focus();
    }

    function changeBase(action) {
        return {
            action,
            id: current?.item.id || null,
            version: current?.item.version || "",
            seriesVersion: current?.seriesVersion || "",
            contextDay: day || null,
            contextBook: day ? root.dataset.book : null
        };
    }

    async function mutate(body) {
        const key = JSON.stringify(body);
        if (!pending.has(key)) {
            pending.set(key, crypto.randomUUID());
        }

        const result = await request("/api/tasks/change", { ...body, operationId: pending.get(key) });
        pending.delete(key);
        return result;
    }

    function readRule() {
        let weekdays = 0;
        for (const checkbox of form.querySelectorAll('input[name="weekdays"]:checked')) {
            weekdays |= 1 << Number(checkbox.value);
        }

        return {
            kind: field("repeatKind").value,
            interval: Number(field("interval").value),
            weekdays: field("repeatKind").value === "weekly" ? weekdays : 62,
            monthMode: field("monthMode").value,
            monthDay: Number(field("monthDay").value),
            ordinal: Number(field("ordinal").value),
            weekday: Number(field("weekday").value),
            month: Number(field("month").value),
            endOffset: Number(field("endOffset").value),
            missing: field("missing").value,
            adjustment: field("adjustment").value,
            avoidWeekends: field("avoidWeekends").checked,
            avoidHolidays: field("avoidHolidays").checked,
            until: field("ending").value === "until" ? field("until").value || null : null,
            count: field("ending").value === "count" ? Number(field("count").value) : null
        };
    }

    async function updateRepeatPreview(repeating) {
        const version = ++repeatPreviewRevision;
        const preview = select("[data-repeat-preview]");
        if (!repeating) {
            preview.replaceChildren();
            return;
        }

        preview.textContent = "予定を確認しています…";
        try {
            const data = await request("/api/tasks/preview", { startDate: field("startDate").value || null, rule: readRule() });
            if (version !== repeatPreviewRevision) {
                return;
            }

            const dates = node("ul");
            for (const item of data.items) {
                dates.append(node("li", item.waiting ? item.scheduled + "（祝日データ待ち）"
                    : item.due + (item.scheduled !== item.due ? "（元の予定 " + item.scheduled + "）" : "")));
            }
            preview.replaceChildren(data.items.length ? dates : node("p", "この条件では予定がありません。"));
        } catch (error) {
            if (version === repeatPreviewRevision) {
                preview.textContent = error.message;
            }
        }
    }

    async function save(event) {
        event.preventDefault();
        if (saving) {
            return;
        }

        const once = Boolean(current?.item.seriesId) && field("scope").value === "once";
        const repeating = field("repeating").checked && !once;
        const body = {
            ...changeBase("save"),
            notebookId: field("notebookId").value,
            title: field("title").value,
            memo: field("memo").value,
            dueDate: repeating ? null : field("dueDate").value || null,
            scope: field("scope").value,
            startDate: repeating ? field("startDate").value : null,
            rule: null,
            reminders: reminders.read()
        };
        if (repeating) {
            body.rule = readRule();
        }

        if (body.scope === "following" && current?.item.seriesId
            && !window.confirm("今回以降の予定を変更します。完了・個別編集済み " + current.retained + "件を保持します。よろしいですか？")) {
            return;
        }

        saving = true;
        const submit = form.querySelector('[type="submit"]');
        submit.disabled = true;
        try {
            await mutate(body);
            initialForm = formSnapshot();
            editor.close();
            opener?.focus();
            saving = false;
            await load();
        } catch (error) {
            const notice = select("[data-task-error]");
            notice.textContent = error.message;
            notice.hidden = false;
            notice.scrollIntoView({ block: "nearest" });
        } finally {
            saving = false;
            submit.disabled = false;
        }
    }

    async function complete(item, checked, checkbox) {
        if (saving) {
            checkbox.checked = !checked;
            return;
        }

        saving = true;
        checkbox.disabled = true;
        try {
            const result = await mutate({
                action: "complete", id: item.id, version: item.version, completed: checked,
                contextDay: day || null, contextBook: day ? root.dataset.book : null
            });
            undo = { item: { ...item, version: result.version }, completed: !checked };
            const toast = select("[data-task-toast]");
            toast.querySelector("span").textContent = checked ? "完了しました。" : "未完了に戻しました。";
            toast.hidden = false;
            saving = false;
            await load();
        } catch (error) {
            checkbox.checked = !checked;
            checkbox.disabled = false;
            status.textContent = error.message;
        } finally {
            saving = false;
        }
    }

    async function remove() {
        if (saving || !current) {
            return;
        }

        if (formSnapshot() !== initialForm && !window.confirm("編集中の変更を破棄して削除へ進みますか？")) {
            return;
        }

        if (current.item.seriesId) {
            editor.close();
            select("[data-delete-target]").textContent = current.item.title;
            select("[data-delete-date]").textContent = "対象の期限：" + (current.item.dueDate || "期限なし")
                + (current.item.scheduledDate ? " ／ 元の予定日：" + current.item.scheduledDate : "");
            select("[data-delete-scope]").value = current.item.waiting ? "following" : "once";
            select("[data-delete-retained]").textContent = "今回以降を選んだ場合も、完了・個別編集済み " + current.retained + "件と手動スキップは保持します。";
            select("[data-delete-error]").hidden = true;
            deletion.showModal();
            return;
        }

        await executeDelete("once");
    }

    async function executeDelete(scope) {
        if (saving) {
            return false;
        }

        const message = scope === "following"
            ? "今回以降の未操作の予定を削除します。完了・個別編集済み " + current.retained + "件と手動スキップは保持します。"
            : current.item.seriesId ? "今回の予定をスキップします。元に戻せません。" : "このタスクを完全に削除します。元に戻せません。";
        if (!window.confirm(current.item.title + "\n対象の期限：" + (current.item.dueDate || "期限なし") + "\n\n" + message)) {
            return false;
        }

        saving = true;
        try {
            await mutate({ ...changeBase("delete"), scope });
            detail.close();
            editor.close();
            saving = false;
            await load();
            opener?.focus();
            return true;
        } catch (error) {
            status.textContent = error.message;
            select("[data-delete-error]").textContent = error.message;
            select("[data-delete-error]").hidden = false;
            select("[data-task-error]").textContent = error.message;
            select("[data-task-error]").hidden = false;
            return false;
        } finally {
            saving = false;
        }
    }

    select("[data-add-task]")?.addEventListener("click", () => {
        opener = document.activeElement;
        showEditor(null);
    });
    select("[data-edit-task]").addEventListener("click", () => showEditor(current));
    select("[data-delete-task]").addEventListener("click", remove);
    select("[data-confirm-delete]").addEventListener("click", async () => {
        if (await executeDelete(select("[data-delete-scope]").value)) {
            deletion.close();
            opener?.focus();
        }
    });
    for (const selector of ["[data-close-delete]", "[data-cancel-delete]"]) {
        select(selector).addEventListener("click", () => {
            if (!saving) {
                deletion.close();
                opener?.focus();
            }
        });
    }

    deletion.addEventListener("cancel", event => {
        if (saving) {
            event.preventDefault();
        }
    });
    select("[data-close-detail]").addEventListener("click", () => detail.close());
    select("[data-close-editor]").addEventListener("click", closeEditor);
    select("[data-cancel-editor]").addEventListener("click", closeEditor);
    editor.addEventListener("cancel", event => {
        event.preventDefault();
        closeEditor();
    });
    editor.addEventListener("keydown", event => {
        if (event.key === "Escape") {
            // CloseWatcherの強制クローズより先に処理し、未保存確認をEscでも維持する。
            event.preventDefault();
            event.stopPropagation();
            closeEditor();
        }
    });
    field("scope").addEventListener("change", () => {
        reminders.set(field("scope").value === "following" ? current?.commonReminders : current?.reminders);
    });
    form.addEventListener("change", updateEditor);
    form.addEventListener("submit", save);
    window.addEventListener("beforeunload", event => {
        if (editor.open && formSnapshot() !== initialForm) {
            event.preventDefault();
            event.returnValue = "";
        }
    });
    select("[data-task-undo]").addEventListener("click", () => {
        if (!undo) {
            return;
        }

        const action = undo;
        undo = null;
        select("[data-task-toast]").hidden = true;
        complete(action.item, action.completed, document.createElement("input"));
    });

    filterForm?.addEventListener("submit", event => {
        event.preventDefault();
        page = 1;
        load();
        if (window.matchMedia("(max-width: 700px)").matches) {
            select(".task-filters").open = false;
            select(".task-filters summary").focus();
        }
    });
    select("[data-reset-filters]")?.addEventListener("click", () => {
        filterForm.reset();
        kind = "all";
        root.querySelectorAll("[data-kind]").forEach(item => item.setAttribute("aria-pressed", String(item.dataset.kind === "all")));
        visible("[data-custom-period]", false);
        page = 1;
        load();
    });
    filterForm?.addEventListener("change", () => {
        visible("[data-custom-period]", mode === "completed" && filterForm.elements.namedItem("period").value === "custom");
    });
    for (const tab of root.querySelectorAll("[data-mode], [data-kind]")) {
        tab.addEventListener("click", () => {
            const category = tab.dataset.mode ? "mode" : "kind";
            if (category === "mode") {
                mode = tab.dataset.mode;
                kind = "all";
                const overdue = filterForm.querySelector('option[value="overdue"]');
                overdue.disabled = mode === "completed";
                overdue.hidden = mode === "completed";
                root.querySelectorAll("[data-kind]").forEach(item => item.setAttribute("aria-pressed", String(item.dataset.kind === "all")));
                if (mode === "completed" && filterForm.elements.namedItem("quick").value === "overdue") {
                    filterForm.elements.namedItem("quick").value = "all";
                }

                if (intro) {
                    intro.textContent = mode === "completed"
                        ? "完了済みのタスクを完了日時の新しい順に表示します。繰り返しは展開すると各回を確認できます。"
                        : "未完了のタスクを期限順に表示します。繰り返しは展開すると各回を確認できます。";
                }
            } else {
                kind = tab.dataset.kind;
                filterForm.elements.namedItem("quick").value = "all";
            }

            root.querySelectorAll("[data-" + category + "]").forEach(item => item.setAttribute("aria-pressed", String(item === tab)));
            visible("[data-completed-filter]", mode === "completed");
            visible("[data-pending-filter]", mode === "pending");
            visible("[data-custom-period]", mode === "completed" && filterForm.elements.namedItem("period").value === "custom");
            page = 1;
            load();
        });
    }

    load();
    const linkedTask = new URL(location.href).searchParams.get("taskId");
    if (linkedTask) {
        showDetail(linkedTask);
    }
})();
