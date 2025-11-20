document.addEventListener("DOMContentLoaded", () => {
    const codeEditable = document.getElementById("codeEditable");
    const codeDisplay = document.getElementById("codeDisplay");
    const codeDisplayInner = document.getElementById("codeDisplayInner");
    const btnRun = document.getElementById("btnRun");
    const btnStep = document.getElementById("btnStep");
    const btnFullRun = document.getElementById("btnFullRun");
    const btnEdit = document.getElementById("btnEdit");
    const varsBody = document.querySelector("#varsTable tbody");
    const consoleEl = document.getElementById("console");

    let sessionId = null;
    let currentStep = -1;
    let isFullRunning = false;

    const log = (...args) => console.log("[TraceFox]", new Date().toISOString(), ...args);

    function escapeHtml(str) {
        return String(str ?? "")
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;");
    }

    // --- Live highlighting while typing ---
    function applyLiveHighlight() {
        const text = getPlainTextFromEditable();
        const html = Prism.highlight(text, Prism.languages.python, "python");
        codeEditable.innerHTML = html.replace(/\n/g, "<br>");
        placeCaretAtEnd(codeEditable);
    }

    function placeCaretAtEnd(el) {
        const range = document.createRange();
        const sel = window.getSelection();
        range.selectNodeContents(el);
        range.collapse(false);
        sel.removeAllRanges();
        sel.addRange(range);
    }

    function getPlainTextFromEditable() {
        return codeEditable.innerText.replace(/\u00A0/g, " ");
    }

    // --- Render code into Prism-highlighted display ---
    function renderCodeIntoDisplay(codeText) {
        codeDisplayInner.innerHTML = escapeHtml(codeText);
        Prism.highlightElement(codeDisplayInner);

        const lines = codeDisplayInner.innerHTML.split(/\r?\n/);
        codeDisplayInner.innerHTML = lines
            .map((ln, idx) => `<span class="line-span" data-line="${idx + 1}">${ln || "&nbsp;"}</span>`)
            .join("\n");
    }

    function highlightLine(lineNo) {
        codeDisplayInner.querySelectorAll(".line-span.line-highlight")
            .forEach(el => el.classList.remove("line-highlight"));
        const target = codeDisplayInner.querySelector(`.line-span[data-line="${lineNo}"]`);
        if (target) {
            target.classList.add("line-highlight");
            target.scrollIntoView({ block: "center", behavior: "smooth" });
        }
    }

    function switchToDisplay(codeText) {
        renderCodeIntoDisplay(codeText);
        codeEditable.style.display = "none";
        codeDisplay.style.display = "block";
    }

    function switchToEdit() {
        codeDisplay.style.display = "none";
        codeEditable.style.display = "block";
        applyLiveHighlight();
    }

    function renderVariables(vars) {
        varsBody.innerHTML = "";
        for (const [k, v] of Object.entries(vars || {})) {
            const tr = document.createElement("tr");
            tr.innerHTML = `<td>${escapeHtml(k)}</td><td>${escapeHtml(v)}</td>`;
            varsBody.appendChild(tr);
        }
    }

    async function startDebug() {
        sessionId = null;
        isFullRunning = false;
        currentStep = -1;
        renderVariables({});
        consoleEl.textContent = "";
        codeDisplayInner.querySelectorAll(".line-span.line-highlight")
            .forEach(el => el.classList.remove("line-highlight"));

        const code = getPlainTextFromEditable();
        await saveCode(code);  // Save code before running

        switchToDisplay(code);

        const resp = await fetch("/api/Debug/start", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            credentials: "include",
            body: JSON.stringify({ code })
        });

        let data;
        try { data = await resp.json(); }
        catch { alert("Invalid start response."); return; }

        if (!resp.ok) {
            alert("Start failed: " + (data.error || resp.statusText));
            return;
        }

        sessionId = data.sessionId;
        btnRun.disabled = true;
        btnStep.disabled = false;
        btnFullRun.disabled = false;
        btnEdit.disabled = false;

        await handleStep(data.step);
    }

    // --- FullRun simulation (runs remaining lines only) ---
    // --- FullRun simulation (runs all or remaining lines reliably) ---
    async function fullRun() {
        if (isFullRunning) return;

        // --- if no active session, start fresh ---
        if (!sessionId) {
            log("No session found, starting a new one for full run...");
            await startDebug(); // fresh session
        }

        isFullRunning = true;
        btnRun.disabled = true;
        btnStep.disabled = true;
        btnFullRun.disabled = true;

        const totalLines = getPlainTextFromEditable().split(/\r?\n/).length;
        log(`Starting FullRun from step ${currentStep + 1}/${totalLines}`);

        try {
            // keep stepping until backend says done
            while (true) {
                const result = await step();
                await new Promise(res => setTimeout(res, 200));

                if (!result || result.done || result.finished || result.isComplete || result.endOfCode) {
                    log("End of execution detected (FullRun loop ended)");
                    break;
                }

                // safety check to prevent endless loop
                if (currentStep + 1 >= totalLines) {
                    log("Reached end of code lines (safety stop)");
                    break;
                }
            }
        } catch (err) {
            log("Error during FullRun", err);
        } finally {
            isFullRunning = false;
            btnRun.disabled = false;
            btnStep.disabled = false;
            btnFullRun.disabled = false;
            btnEdit.disabled = false;
            log("FullRun finished & UI reset");

            // reset state so next run starts clean
            sessionId = null;
            currentStep = -1;
        }
    }

    

    // Save code to DB when Run is clicked
    async function saveCode(code) {
        try {
            await fetch("/api/Code/save", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ code })
            });
        } catch (err) {
            console.error("Failed to save code:", err);
        }
    }


    async function step() {
        if (!sessionId) return null;

        const resp = await fetch("/api/Debug/step", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            credentials: "include"
        });

        let data;
        try { data = await resp.json(); }
        catch { return null; }

        if (!resp.ok) {
            log("Step failed:", data);
            return null;
        }

        await handleStep(data);
        return data; // important for fullRun()
    }


    // Load code from DB when page loads
    async function loadCode() {
        try {
            const res = await fetch("/api/Code/load");
            const data = await res.json();
            const code = data.code || "";
            codeEditable.innerText = code;
            applyLiveHighlight();
        } catch (err) {
            console.error("Failed to load code:", err);
        }
    }

    async function handleStep(step) {
        if (!step) return;
        const line = step.line ?? step.Line ?? -1;
        const rawVars = step.vars ?? step.Vars ?? {};
        const output = step.output ?? step.Output ?? "";

        highlightLine(line);

        let vars = {};
        if (typeof rawVars === "string") {
            try { vars = JSON.parse(rawVars); } catch { }
        } else if (typeof rawVars === "object") vars = rawVars;

        renderVariables(vars);
        consoleEl.textContent = (output || "").trim();
        consoleEl.scrollTop = consoleEl.scrollHeight;
    }

    btnEdit.onclick = () => {
        switchToEdit();
        btnRun.disabled = false;
        btnStep.disabled = true;
        btnFullRun.disabled = true;
    };

    btnRun.onclick = startDebug;
    btnStep.onclick = step;
    btnFullRun.onclick = fullRun;

    // --- Live syntax highlight binding ---
    let typingTimer;
    codeEditable.addEventListener("input", () => {
        clearTimeout(typingTimer);
        typingTimer = setTimeout(applyLiveHighlight, 200);
    });

    log("TraceFox (live contenteditable highlighting) initialized.");
    loadCode();
});