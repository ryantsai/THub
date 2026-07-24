window.thub = {
    clickElement: (elementId) => document.getElementById(elementId)?.click(),
    downloadTextFile: (fileName, content) => {
        const blob = new Blob([content], { type: "application/json;charset=utf-8" });
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
    }
};

window.thubLocalization = {
    catalog: null,
    observer: null,
    translatableAttributes: ["aria-label", "title", "placeholder", "data-placeholder"],

    translateValue(value) {
        if (!this.catalog || !value) {
            return value;
        }

        const leading = value.match(/^\s*/)?.[0] ?? "";
        const trailing = value.match(/\s*$/)?.[0] ?? "";
        const text = value.slice(leading.length, value.length - trailing.length);
        return `${leading}${this.catalog[text] ?? text}${trailing}`;
    },

    translateNode(node) {
        if (node.nodeType === Node.TEXT_NODE) {
            const translated = this.translateValue(node.nodeValue);
            if (translated !== node.nodeValue) {
                node.nodeValue = translated;
            }
            return;
        }

        if (!(node instanceof Element) || node.closest("code, pre, script, style, textarea")) {
            return;
        }

        for (const attribute of this.translatableAttributes) {
            if (node.hasAttribute(attribute)) {
                const current = node.getAttribute(attribute);
                const translated = this.translateValue(current);
                if (translated !== current) {
                    node.setAttribute(attribute, translated);
                }
            }
        }

        for (const child of node.childNodes) {
            this.translateNode(child);
        }
    },

    async start() {
        if (document.documentElement.lang.toLowerCase() !== "zh-tw") {
            return;
        }

        const response = await fetch(
            new URL("locales/zh-TW.json", document.baseURI),
            { cache: "no-cache" });
        if (!response.ok) {
            return;
        }

        this.catalog = await response.json();
        this.translateNode(document.documentElement);
        this.observer = new MutationObserver(mutations => {
            for (const mutation of mutations) {
                for (const node of mutation.addedNodes) {
                    this.translateNode(node);
                }
                if (mutation.type === "characterData") {
                    this.translateNode(mutation.target);
                }
            }
        });
        this.observer.observe(document.documentElement, {
            childList: true,
            subtree: true,
            characterData: true
        });
    }
};

window.thubLocalization.start();
