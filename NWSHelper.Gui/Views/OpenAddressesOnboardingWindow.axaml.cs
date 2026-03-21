using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using NWSHelper.Gui.Services;
using WebViewControl;

namespace NWSHelper.Gui.Views;

public partial class OpenAddressesOnboardingWindow : Window
{
    private const string SimplifiedOnboardingInstruction = "Register for or log into OpenAddresses for API access to get address datasets";
    private const string ManualOnboardingInstruction = "Use OpenAddresses registration/profile pages to create an API key, then paste it below to continue dataset selection.";
    private const double SimplifiedWindowHeight = 620;
    private const double ManualWindowHeight = 850;
    private const double DebugWindowHeight = 980;
    private const int ScriptEvaluationTimeoutMs = 2500;
    private const int StandardAutomationMaxAttempts = 24;
    private const int AdvancedAutomationMaxAttempts = 70;
    private const int StandardManualScanMaxAttempts = 12;
    private const int AdvancedManualScanMaxAttempts = 45;
    private const string RegisterUrl = "https://batch.openaddresses.io/register";
    private const string ProfileUrl = "https://batch.openaddresses.io/profile";
    private const int DataToProfileRedirectDelayMs = 700;
    private const string ScrollToBottomScript = "window.scrollTo(0, Math.max(document.body?.scrollHeight || 0, document.documentElement?.scrollHeight || 0));";
    private const string ExtractTokenFromDomScript = """
(() => {
    const normalize = (value) => {
        if (!value || typeof value !== 'string') {
            return '';
        }

        return value.trim();
    };

    const pushTokenCandidate = (set, rawValue) => {
        const value = normalize(rawValue);
        if (!value) {
            return;
        }

        const matches = value.match(/[A-Za-z0-9._-]{20,}/g);
        if (!matches || matches.length === 0) {
            return;
        }

        for (const match of matches) {
            set.add(match);
        }
    };

    const collectTokens = () => {
        const candidates = new Set();

        document.querySelectorAll('[data-clipboard-text]').forEach(element => {
            pushTokenCandidate(candidates, element.getAttribute('data-clipboard-text') || '');
        });

        document.querySelectorAll('pre, code').forEach(element => {
            pushTokenCandidate(candidates, element.textContent || '');
        });

        const xpathSelectors = [
            "//pre",
            "//code",
            "//*[@data-clipboard-text]",
            "//*[contains(@class,'token') or contains(@class,'api')]",
            "//*[contains(normalize-space(.), 'NWSHelper')]/ancestor::*[self::tr or self::li or self::div][1]//*[self::pre or self::code or @data-clipboard-text]"
        ];

        for (const selector of xpathSelectors) {
            try {
                const result = document.evaluate(selector, document, null, XPathResult.ORDERED_NODE_SNAPSHOT_TYPE, null);
                for (let index = 0; index < result.snapshotLength; index++) {
                    const node = result.snapshotItem(index);
                    if (!node) {
                        continue;
                    }

                    const dataClipboardValue = node.getAttribute ? node.getAttribute('data-clipboard-text') : '';
                    pushTokenCandidate(candidates, dataClipboardValue || node.textContent || '');
                }
            } catch {
            }
        }

        const fullText = document.body?.innerText || '';
        pushTokenCandidate(candidates, fullText);

        return Array.from(candidates.values());
    };

    const chooseBestToken = (tokens) => {
        if (!tokens || tokens.length === 0) {
            return '';
        }

        tokens.sort((left, right) => right.length - left.length);
        return tokens[0] || '';
    };

    const findTokenNearNwsHelperLabel = () => {
        const xpaths = [
            "//*[contains(normalize-space(.), 'NWSHelper')]/ancestor::*[self::tr or self::li or self::div][1]//pre",
            "//*[contains(normalize-space(.), 'NWSHelper')]/ancestor::*[self::tr or self::li or self::div][1]//code",
            "//*[contains(normalize-space(.), 'NWSHelper')]/ancestor::*[self::tr or self::li or self::div][1]//*[@data-clipboard-text]"
        ];

        for (const selector of xpaths) {
            try {
                const result = document.evaluate(selector, document, null, XPathResult.ORDERED_NODE_SNAPSHOT_TYPE, null);
                const values = [];
                for (let index = 0; index < result.snapshotLength; index++) {
                    const node = result.snapshotItem(index);
                    if (!node) {
                        continue;
                    }

                    const raw = (node.getAttribute ? node.getAttribute('data-clipboard-text') : '') || node.textContent || '';
                    const tokenMatches = raw.match(/[A-Za-z0-9._-]{20,}/g) || [];
                    for (const token of tokenMatches) {
                        values.push(token);
                    }
                }

                const best = chooseBestToken(values);
                if (best) {
                    return best;
                }
            } catch {
            }
        }

        return '';
    };

    const tokens = collectTokens();
    const nearLabelToken = findTokenNearNwsHelperLabel();
    if (nearLabelToken) {
        return nearLabelToken;
    }

    return chooseBestToken(tokens);
})();
""";
    private const string AutoCreateAndReadApiKeyScript = """
(() => {
    const currentPath = (window.location?.pathname || '').replace(/\/+$/, '');
    if (currentPath !== '/profile') {
        return '';
    }

    const tokenRegex = /[A-Za-z0-9._~+\-/=]{10,}/g;
    const plusSelector = '.tabler-icon.tabler-icon-plus.cursor-pointer';
    const tokenInputSelector = 'input[type="text"].form-control[placeholder="Token Name"]';
    const confirmPathSelector = 'path[d="M5 12l5 5l10 -10"]';
    const postConfirmScanDelayMs = 3000;

    const shouldReset = !window.__nwsHelperState ||
        window.__nwsHelperState.pagePath !== currentPath ||
        window.__nwsHelperState.completed === true;

    const state = shouldReset ? {
        uniqueTokenName: `NWSHelper-${Date.now()}`,
        pagePath: currentPath,
        phase: 'init',
        plusClicked: false,
        plusClickedAt: 0,
        tokenNameSet: false,
        confirmClicked: false,
        confirmClickedAt: 0,
        notBeforeTokenScanAt: 0,
        confirmRetries: 0,
        textSignatureBeforeConfirm: '',
        domSettled: false,
        settleChecks: 0,
        tokensBeforeCreate: [],
        attempts: 0,
        completed: false,
        responseToken: '',
        token: ''
    } : window.__nwsHelperState;

    window.__nwsHelperState = state;
    state.attempts += 1;
    const now = Date.now();

    const extractTokensFromText = (rawValue) => {
        if (!rawValue || typeof rawValue !== 'string') {
            return [];
        }

        return rawValue.match(tokenRegex) || [];
    };

    const extractFirstOaToken = (rawValue) => {
        const matches = extractTokensFromText(rawValue || '');
        for (const match of matches) {
            if ((match || '').startsWith('oa.')) {
                return match;
            }
        }

        return '';
    };

    const computeTextSignature = () => {
        const text = (document.body?.innerText || '').trim();
        const head = text.slice(0, 180);
        const tail = text.slice(-180);
        return `${text.length}:${head}:${tail}`;
    };

    const maybeCaptureResponseToken = (rawValue) => {
        if (state.responseToken) {
            return;
        }

        const token = extractFirstOaToken(rawValue);
        if (token) {
            state.responseToken = token;
        }
    };

    if (!window.__nwsHelperHooksInstalled) {
        try {
            const originalFetch = window.fetch ? window.fetch.bind(window) : null;
            if (originalFetch) {
                window.fetch = (...args) => originalFetch(...args).then(response => {
                    try {
                        const clone = response.clone();
                        clone.text().then(text => {
                            maybeCaptureResponseToken(text);
                        }).catch(() => {});
                    } catch {
                    }

                    return response;
                });
            }

            const originalOpen = XMLHttpRequest.prototype.open;
            const originalSend = XMLHttpRequest.prototype.send;

            XMLHttpRequest.prototype.open = function (...args) {
                this.__nwsHelperUrl = args && args.length > 1 ? args[1] : '';
                return originalOpen.apply(this, args);
            };

            XMLHttpRequest.prototype.send = function (...args) {
                this.addEventListener('load', function () {
                    try {
                        maybeCaptureResponseToken(this.responseText || '');
                    } catch {
                    }
                });

                return originalSend.apply(this, args);
            };

            window.__nwsHelperHooksInstalled = true;
        } catch {
        }
    }

    const clickElement = (element) => {
        if (!element) {
            return false;
        }

        try {
            if (typeof element.click === 'function') {
                element.click();
            }

            element.dispatchEvent(new MouseEvent('mousedown', { view: window, bubbles: true, cancelable: true }));
            element.dispatchEvent(new MouseEvent('mouseup', { view: window, bubbles: true, cancelable: true }));
            element.dispatchEvent(new MouseEvent('click', { view: window, bubbles: true, cancelable: true }));
        } catch {
        }

        return true;
    };

    const isElementVisible = (element) => {
        if (!element) {
            return false;
        }

        try {
            const rect = element.getBoundingClientRect();
            const style = window.getComputedStyle(element);
            return rect.width > 0 &&
                   rect.height > 0 &&
                   style.display !== 'none' &&
                   style.visibility !== 'hidden' &&
                   style.opacity !== '0';
        } catch {
            return true;
        }
    };

    const findVisibleTokenInput = () => {
        const inputs = Array.from(document.querySelectorAll(tokenInputSelector));
        for (const input of inputs) {
            if (isElementVisible(input)) {
                return input;
            }
        }

        return null;
    };

    const findPlusButton = () => {
        const candidates = Array.from(document.querySelectorAll(plusSelector));
        if (!candidates || candidates.length === 0) {
            return null;
        }

        const visibleCandidates = candidates.filter(candidate => {
            try {
                return !!(candidate.offsetParent || candidate.getClientRects?.().length);
            } catch {
                return true;
            }
        });

        return visibleCandidates.length > 0 ? visibleCandidates[visibleCandidates.length - 1] : candidates[candidates.length - 1];
    };

    const findConfirmButton = (tokenInput) => {
        const confirmPath = document.querySelector(confirmPathSelector);
        let confirmButton = confirmPath?.closest('.tabler-icon.tabler-icon-plus.cursor-pointer') ||
                            confirmPath?.closest('svg') ||
                            confirmPath?.parentElement || null;

        if (confirmButton) {
            return confirmButton;
        }

        if (tokenInput) {
            const localContainer = tokenInput.closest('form, .modal, .dialog, .card, .popover, div');
            if (localContainer) {
                confirmButton = localContainer.querySelector('.tabler-icon.tabler-icon-plus.cursor-pointer, button[type="submit"], button.btn-primary, button');
                if (confirmButton) {
                    return confirmButton;
                }
            }
        }

        return null;
    };

    const appendMatches = (candidates, rawValue) => {
        const matches = extractTokensFromText(rawValue);
        for (const match of matches) {
            candidates.add(match);
        }
    };

    const collectTokenCandidates = () => {
        const candidates = new Set();

        document.querySelectorAll('[data-clipboard-text]').forEach(element => {
            appendMatches(candidates, element.getAttribute('data-clipboard-text') || '');
        });

        document.querySelectorAll('pre, code').forEach(element => {
            appendMatches(candidates, element.textContent || '');
        });

        const xpathSelectors = [
            "//pre",
            "//code",
            "//*[@data-clipboard-text]",
            "//*[contains(normalize-space(.), 'NWSHelper')]/ancestor::*[self::tr or self::li or self::div][1]//*[self::pre or self::code or @data-clipboard-text]"
        ];

        for (const selector of xpathSelectors) {
            try {
                const result = document.evaluate(selector, document, null, XPathResult.ORDERED_NODE_SNAPSHOT_TYPE, null);
                for (let index = 0; index < result.snapshotLength; index++) {
                    const node = result.snapshotItem(index);
                    if (!node) {
                        continue;
                    }

                    const raw = (node.getAttribute ? node.getAttribute('data-clipboard-text') : '') || node.textContent || '';
                    appendMatches(candidates, raw);
                }
            } catch {
            }
        }

        appendMatches(candidates, document.body?.innerText || '');
        return Array.from(candidates.values());
    };

    const bestToken = (tokens) => {
        if (!tokens || tokens.length === 0) {
            return '';
        }

        tokens.sort((left, right) => right.length - left.length);
        return tokens[0] || '';
    };

    const findCreatedTokenContainer = () => {
        const containerXpaths = [
            `//*[contains(normalize-space(.), '${state.uniqueTokenName}')]/ancestor::tr[1]`,
            `//*[contains(normalize-space(.), '${state.uniqueTokenName}')]/ancestor::li[1]`,
            `//*[contains(normalize-space(.), '${state.uniqueTokenName}')]/ancestor::*[self::tr or self::li or self::div][1]`
        ];

        for (const selector of containerXpaths) {
            try {
                const result = document.evaluate(selector, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null);
                if (result.singleNodeValue) {
                    return result.singleNodeValue;
                }
            } catch {
            }
        }

        return null;
    };

    const extractTokensFromNode = (node) => {
        const candidates = new Set();
        if (!node) {
            return [];
        }

        if (node.querySelectorAll) {
            node.querySelectorAll('[data-clipboard-text], pre, code').forEach(element => {
                const raw = (element.getAttribute ? element.getAttribute('data-clipboard-text') : '') || element.textContent || '';
                appendMatches(candidates, raw);
            });
        }

        appendMatches(candidates, node.textContent || '');
        return Array.from(candidates.values());
    };

    window.scrollTo(0, Math.max(document.body?.scrollHeight || 0, document.documentElement?.scrollHeight || 0));

    const tokenInput = findVisibleTokenInput();
    if (!tokenInput && !state.confirmClicked) {
        const plusButton = findPlusButton();
        const msSincePlusClick = now - (state.plusClickedAt || 0);
        const canClickPlus = !state.plusClicked || msSincePlusClick > 1500;

        if (plusButton && canClickPlus) {
            clickElement(plusButton);
            state.plusClicked = true;
            state.plusClickedAt = now;
            state.phase = 'clicked-plus';
            return '';
        }

        state.phase = plusButton ? 'waiting-token-input' : 'plus-not-found';
        return state.token || '';
    }

    if (tokenInput) {
        if (!state.tokenNameSet || tokenInput.value !== state.uniqueTokenName) {
            tokenInput.focus();
            tokenInput.value = state.uniqueTokenName;
            tokenInput.dispatchEvent(new Event('input', { bubbles: true }));
            tokenInput.dispatchEvent(new Event('change', { bubbles: true }));
            state.tokenNameSet = true;
            state.phase = 'filled-token-name';
        }

        if (!state.tokensBeforeCreate || state.tokensBeforeCreate.length === 0) {
            state.tokensBeforeCreate = collectTokenCandidates();
        }

        if (!state.confirmClicked) {
            const confirmButton = findConfirmButton(tokenInput);
            if (confirmButton) {
                state.textSignatureBeforeConfirm = computeTextSignature();
                state.domSettled = false;
                state.settleChecks = 0;
                clickElement(confirmButton);
                state.confirmClicked = true;
                state.confirmClickedAt = now;
                state.notBeforeTokenScanAt = now + postConfirmScanDelayMs;
                state.phase = 'clicked-confirm';
                return '';
            } else {
                state.phase = 'confirm-not-found';
                return state.token || '';
            }
        }
    }

    if (state.confirmClicked && tokenInput) {
        const msSinceConfirmClick = now - (state.confirmClickedAt || 0);
        if (msSinceConfirmClick > 1800 && state.confirmRetries < 2) {
            const retryConfirmButton = findConfirmButton(tokenInput);
            if (retryConfirmButton) {
                clickElement(retryConfirmButton);
                state.confirmRetries += 1;
                state.confirmClickedAt = now;
                state.notBeforeTokenScanAt = now + postConfirmScanDelayMs;
                state.phase = 'reclicked-confirm';
                return state.token || '';
            }
        }

        state.phase = 'waiting-confirm-dismiss';
    }

    if (state.confirmClicked && now < (state.notBeforeTokenScanAt || 0)) {
        state.phase = 'waiting-post-confirm-delay';
        return state.token || '';
    }

    if (state.confirmClicked && !state.domSettled) {
        const msSinceConfirmClick = now - (state.confirmClickedAt || 0);
        const currentSignature = computeTextSignature();
        const signatureChanged = !!state.textSignatureBeforeConfirm && currentSignature !== state.textSignatureBeforeConfirm;

        if (msSinceConfirmClick < 900 && !signatureChanged) {
            state.phase = 'waiting-dom-settle';
            return state.token || '';
        }

        state.settleChecks = (state.settleChecks || 0) + 1;
        if (!signatureChanged && state.settleChecks < 4) {
            state.phase = 'waiting-dom-change';
            return state.token || '';
        }

        state.domSettled = true;
        state.phase = 'dom-settled';
    }

    if (state.responseToken) {
        state.token = state.responseToken;
        state.completed = true;
        state.phase = 'token-from-network-response';
        return state.token;
    }

    const createdContainer = findCreatedTokenContainer();
    if (createdContainer) {
        const containerTokens = extractTokensFromNode(createdContainer);
        const containerToken = bestToken(containerTokens);
        if (containerToken) {
            state.token = containerToken;
            state.completed = true;
            state.phase = 'token-from-created-row';
            return containerToken;
        }
    }

    const allTokens = collectTokenCandidates();
    const newTokens = (state.tokensBeforeCreate && state.tokensBeforeCreate.length > 0)
        ? allTokens.filter(token => !state.tokensBeforeCreate.includes(token))
        : [];

    const newToken = bestToken(newTokens);
    if (newToken) {
        state.token = newToken;
        state.completed = true;
        state.phase = 'token-from-new-diff';
        return newToken;
    }

    if (state.confirmClicked) {
        const anyToken = bestToken(allTokens);
        if (anyToken) {
            state.token = anyToken;
            state.completed = true;
            state.phase = 'token-from-any';
            return anyToken;
        }
    }

    state.phase = state.confirmClicked ? 'awaiting-token-after-confirm' : state.phase;
    return state.token || '';
})();
""";
    private const string ReadAutomationProgressScript = """
(() => {
    const state = window.__nwsHelperState || {};
    const phase = state.phase || 'unknown';
    const name = state.uniqueTokenName || '';
    const confirm = state.confirmClicked ? 'yes' : 'no';
    const plus = state.plusClicked ? 'yes' : 'no';
    const retries = state.confirmRetries || 0;
    const attempts = state.attempts || 0;
    const settled = state.domSettled ? 'yes' : 'no';
    const waitMs = Math.max(0, (state.notBeforeTokenScanAt || 0) - Date.now());
    const beforeCount = Array.isArray(state.tokensBeforeCreate) ? state.tokensBeforeCreate.length : 0;
    const tokenLen = (state.token || '').length;
    const responseLen = (state.responseToken || '').length;
    return `${phase};name=${name};plus=${plus};confirm=${confirm};settled=${settled};waitMs=${waitMs};retries=${retries};attempts=${attempts};before=${beforeCount};tokenLen=${tokenLen};responseLen=${responseLen}`;
})();
""";
    private const string ForceDomReadRefreshScript = """
(() => {
    const state = window.__nwsHelperState || {};
    const text = (document.body?.innerText || '').toString();
    state.domReadEpoch = (state.domReadEpoch || 0) + 1;
    state.lastDomReadAt = Date.now();
    state.lastDomReadSignature = `${text.length}:${text.slice(0, 140)}:${text.slice(-140)}`;
    window.__nwsHelperState = state;
    return state.lastDomReadSignature;
})();
""";
    private const string InstallDomPickerScript = """
(() => {
    const state = window.__nwsDomPicker || {};
    if (state.installed) {
        return 'already-installed';
    }

    const toCssPath = (element) => {
        if (!element || !element.tagName) {
            return '';
        }

        const segments = [];
        let node = element;
        while (node && node.nodeType === Node.ELEMENT_NODE && segments.length < 10) {
            let selector = node.tagName.toLowerCase();
            if (node.id) {
                selector += `#${node.id}`;
                segments.unshift(selector);
                break;
            }

            if (node.classList && node.classList.length > 0) {
                selector += '.' + Array.from(node.classList).slice(0, 2).join('.');
            }

            const siblingElements = node.parentElement ? Array.from(node.parentElement.children).filter(child => child.tagName === node.tagName) : [];
            if (siblingElements.length > 1) {
                const index = siblingElements.indexOf(node) + 1;
                selector += `:nth-of-type(${index})`;
            }

            segments.unshift(selector);
            node = node.parentElement;
        }

        return segments.join(' > ');
    };

    const toXPath = (element) => {
        if (!element || element.nodeType !== Node.ELEMENT_NODE) {
            return '';
        }

        const parts = [];
        let node = element;
        while (node && node.nodeType === Node.ELEMENT_NODE) {
            let index = 1;
            let sibling = node.previousElementSibling;
            while (sibling) {
                if (sibling.tagName === node.tagName) {
                    index += 1;
                }

                sibling = sibling.previousElementSibling;
            }

            parts.unshift(`${node.tagName.toLowerCase()}[${index}]`);
            node = node.parentElement;
        }

        return '/' + parts.join('/');
    };

    const tokenRegex = /oa\.[A-Za-z0-9._~+\-/=]{20,}/g;
    const captureElement = (element) => {
        const text = (element.textContent || '').trim();
        const outer = (element.outerHTML || '').trim();
        const tokenMatches = text.match(tokenRegex) || [];

        const payload = {
            capturedAt: new Date().toISOString(),
            tag: element.tagName.toLowerCase(),
            id: element.id || '',
            classes: element.className || '',
            name: element.getAttribute('name') || '',
            placeholder: element.getAttribute('placeholder') || '',
            role: element.getAttribute('role') || '',
            ariaLabel: element.getAttribute('aria-label') || '',
            dataClipboardText: element.getAttribute('data-clipboard-text') || '',
            cssPath: toCssPath(element),
            xpath: toXPath(element),
            textPreview: text.slice(0, 500),
            outerHtmlPreview: outer.slice(0, 2000),
            tokenMatches
        };

        return payload;
    };

    const clearSelectionMarkers = (doc) => {
        const markers = doc.querySelectorAll('[data-nws-dom-selected="1"]');
        markers.forEach(marker => {
            try {
                marker.removeAttribute('data-nws-dom-selected');
                marker.removeAttribute('data-nws-dom-selected-at');
                marker.style.outline = '';
                marker.style.outlineOffset = '';
            } catch {
            }
        });

        try {
            doc.documentElement?.removeAttribute('data-nws-dom-selection-json');
            doc.documentElement?.removeAttribute('data-nws-dom-selection-source');
        } catch {
        }
    };

    const persistSelection = (doc, payload, source) => {
        try {
            const encoded = encodeURIComponent(JSON.stringify(payload));
            doc.documentElement?.setAttribute('data-nws-dom-selection-json', encoded);
            doc.documentElement?.setAttribute('data-nws-dom-selection-source', source || 'unknown');
        } catch {
        }
    };

    state.lastSelection = null;
    state.lastOutline = null;
    state.handler = (event) => {
        try {
            const target = event.target;
            if (!target || target.nodeType !== Node.ELEMENT_NODE) {
                return;
            }

            event.preventDefault();
            event.stopPropagation();

            clearSelectionMarkers(document);

            target.style.outline = '2px solid #ff2d55';
            target.style.outlineOffset = '2px';
            target.setAttribute('data-nws-dom-selected', '1');
            target.setAttribute('data-nws-dom-selected-at', Date.now().toString());
            state.lastOutline = target;
            state.lastSelection = captureElement(target);
            persistSelection(document, state.lastSelection, 'click-handler');
            window.__nwsDomPicker = state;
        } catch {
        }

        return false;
    };

    document.addEventListener('click', state.handler, true);
    state.installed = true;
    window.__nwsDomPicker = state;
    return 'installed';
})();
""";
    private const string ReadDomPickerSelectionScript = """
(() => {
    const readPersistedSelection = (doc) => {
        try {
            const encoded = doc.documentElement?.getAttribute('data-nws-dom-selection-json') || '';
            if (!encoded) {
                return '';
            }

            const decoded = decodeURIComponent(encoded);
            return decoded || '';
        } catch {
            return '';
        }
    };

    const toCssPath = (element) => {
        if (!element || !element.tagName) {
            return '';
        }

        const segments = [];
        let node = element;
        while (node && node.nodeType === Node.ELEMENT_NODE && segments.length < 10) {
            let selector = node.tagName.toLowerCase();
            if (node.id) {
                selector += `#${node.id}`;
                segments.unshift(selector);
                break;
            }

            if (node.classList && node.classList.length > 0) {
                selector += '.' + Array.from(node.classList).slice(0, 2).join('.');
            }

            const siblingElements = node.parentElement ? Array.from(node.parentElement.children).filter(child => child.tagName === node.tagName) : [];
            if (siblingElements.length > 1) {
                const index = siblingElements.indexOf(node) + 1;
                selector += `:nth-of-type(${index})`;
            }

            segments.unshift(selector);
            node = node.parentElement;
        }

        return segments.join(' > ');
    };

    const toXPath = (element) => {
        if (!element || element.nodeType !== Node.ELEMENT_NODE) {
            return '';
        }

        const parts = [];
        let node = element;
        while (node && node.nodeType === Node.ELEMENT_NODE) {
            let index = 1;
            let sibling = node.previousElementSibling;
            while (sibling) {
                if (sibling.tagName === node.tagName) {
                    index += 1;
                }

                sibling = sibling.previousElementSibling;
            }

            parts.unshift(`${node.tagName.toLowerCase()}[${index}]`);
            node = node.parentElement;
        }

        return '/' + parts.join('/');
    };

    const tokenRegex = /oa\.[A-Za-z0-9._~+\-/=]{20,}/g;
    const captureElement = (element) => {
        const text = (element.textContent || '').trim();
        const outer = (element.outerHTML || '').trim();
        const tokenMatches = text.match(tokenRegex) || [];

        return {
            capturedAt: new Date().toISOString(),
            tag: element.tagName.toLowerCase(),
            id: element.id || '',
            classes: element.className || '',
            name: element.getAttribute('name') || '',
            placeholder: element.getAttribute('placeholder') || '',
            role: element.getAttribute('role') || '',
            ariaLabel: element.getAttribute('aria-label') || '',
            dataClipboardText: element.getAttribute('data-clipboard-text') || '',
            selectedMarkerAt: element.getAttribute('data-nws-dom-selected-at') || '',
            cssPath: toCssPath(element),
            xpath: toXPath(element),
            textPreview: text.slice(0, 500),
            outerHtmlPreview: outer.slice(0, 2000),
            tokenMatches
        };
    };

    const docs = [document];
    const iframes = Array.from(document.querySelectorAll('iframe'));
    for (const frame of iframes) {
        try {
            if (frame.contentDocument) {
                docs.push(frame.contentDocument);
            }
        } catch {
        }
    }

    for (const doc of docs) {
        const persisted = readPersistedSelection(doc);
        if (persisted) {
            return persisted;
        }

        const selected = doc.querySelector('[data-nws-dom-selected="1"]');
        if (selected) {
            return JSON.stringify(captureElement(selected));
        }
    }

    const state = window.__nwsDomPicker || {};
    if (!state.lastSelection) {
        const tokenRegex = /oa\.[A-Za-z0-9._~+\-/=]{20,}/;
        const fallbackElement = Array.from(document.querySelectorAll('pre, code, div, span, p'))
            .find(element => tokenRegex.test((element.textContent || '').toString()));
        if (fallbackElement) {
            const fallbackPayload = captureElement(fallbackElement);
            fallbackPayload.captureMode = 'auto-token-fallback';
            return JSON.stringify(fallbackPayload);
        }

        return '';
    }

    return JSON.stringify(state.lastSelection);
})();
""";
    private const string StopDomPickerScript = """
(() => {
    const state = window.__nwsDomPicker || {};
    const clearSelectionMarkers = (doc) => {
        const markers = doc.querySelectorAll('[data-nws-dom-selected="1"]');
        markers.forEach(marker => {
            try {
                marker.removeAttribute('data-nws-dom-selected');
                marker.removeAttribute('data-nws-dom-selected-at');
                marker.style.outline = '';
                marker.style.outlineOffset = '';
            } catch {
            }
        });

        try {
            doc.documentElement?.removeAttribute('data-nws-dom-selection-json');
            doc.documentElement?.removeAttribute('data-nws-dom-selection-source');
        } catch {
        }
    };

    if (state.handler) {
        try {
            document.removeEventListener('click', state.handler, true);
        } catch {
        }
    }

    clearSelectionMarkers(document);
    const iframes = Array.from(document.querySelectorAll('iframe'));
    for (const frame of iframes) {
        try {
            if (frame.contentDocument) {
                clearSelectionMarkers(frame.contentDocument);
            }
        } catch {
        }
    }

    if (state.lastOutline && state.lastOutline.style) {
        state.lastOutline.style.outline = '';
        state.lastOutline.style.outlineOffset = '';
    }

    state.installed = false;
    state.handler = null;
    window.__nwsDomPicker = state;
    return 'stopped';
})();
""";
    private const string ReadWebViewProbeScript = """
(() => 'nws-webview-probe-ok')();
""";
    private const string ReadWebViewLightweightSnapshotScript = """
(() => {
    const payload = {
        capturedAt: new Date().toISOString(),
        url: window.location?.href || '',
        title: document.title || '',
        readyState: document.readyState || '',
        hasDocumentElement: !!document.documentElement,
        hasBody: !!document.body,
        documentOuterHtmlLength: (document.documentElement?.outerHTML || '').length,
        bodyInnerTextLength: (document.body?.innerText || '').length,
        bodyInnerHtmlLength: (document.body?.innerHTML || '').length
    };

    return JSON.stringify(payload);
})();
""";
    private const string ReadWebViewSnapshotScript = """
(() => {
    const text = (document.body?.innerText || '').toString();
    const html = (document.body?.innerHTML || '').toString();
    const tokenMatches = text.match(/oa\.[A-Za-z0-9._~+\-/=]{20,}/g) || [];

    const iframeSummaries = [];
    const iframes = Array.from(document.querySelectorAll('iframe'));
    for (const frame of iframes) {
        const summary = {
            src: frame.getAttribute('src') || '',
            accessible: false,
            textLength: 0,
            htmlLength: 0,
            error: ''
        };

        try {
            const doc = frame.contentDocument;
            if (doc) {
                summary.accessible = true;
                summary.textLength = (doc.body?.innerText || '').length;
                summary.htmlLength = (doc.body?.innerHTML || '').length;
            }
        } catch (error) {
            summary.error = (error && error.message) ? error.message : 'cross-origin-or-inaccessible';
        }

        iframeSummaries.push(summary);
    }

    const payload = {
        capturedAt: new Date().toISOString(),
        url: window.location?.href || '',
        title: document.title || '',
        readyState: document.readyState || '',
        bodyExists: !!document.body,
        bodyTextLength: text.length,
        bodyHtmlLength: html.length,
        bodyTextPreview: text.slice(0, 800),
        bodyHtmlPreview: html.slice(0, 2000),
        preCount: document.querySelectorAll('pre').length,
        codeCount: document.querySelectorAll('code').length,
        clipboardAttributeCount: document.querySelectorAll('[data-clipboard-text]').length,
        selectedMarkerCount: document.querySelectorAll('[data-nws-dom-selected="1"]').length,
        tokenMatchCount: tokenMatches.length,
        tokenMatchesPreview: tokenMatches.slice(0, 5),
        iframeCount: iframes.length,
        iframeSummaries
    };

    return JSON.stringify(payload);
})();
""";
    private const string ExtractOpenAddressesTokenSimpleScript = """
(() => {
    const normalize = (value) => (value || '').toString().trim();

    const fromPreOrCode = Array.from(document.querySelectorAll('pre, code'))
        .map(element => normalize(element.textContent))
        .find(value => value.startsWith('oa.') && value.length >= 30);

    if (fromPreOrCode) {
        return fromPreOrCode;
    }

    const fromClipboardAttributes = Array.from(document.querySelectorAll('[data-clipboard-text]'))
        .map(element => normalize(element.getAttribute('data-clipboard-text')))
        .find(value => value.startsWith('oa.') && value.length >= 30);

    if (fromClipboardAttributes) {
        return fromClipboardAttributes;
    }

    const allText = normalize(document.body?.innerText);
    const match = allText.match(/oa\.[A-Za-z0-9]{20,}/);
    return match ? match[0] : '';
})();
""";
    private const string ExtractOpenAddressesTokenDeepScanScript = """
(() => {
    const seen = new Set();
    const candidates = [];

    const addMatches = (rawText) => {
        const value = (rawText || '').toString();
        if (!value) {
            return;
        }

        const regex = /oa\.[A-Za-z0-9._~+\-/=]{20,}/g;
        let match = regex.exec(value);
        while (match) {
            const token = (match[0] || '').trim();
            if (token && !seen.has(token)) {
                seen.add(token);
                candidates.push(token);
            }

            match = regex.exec(value);
        }
    };

    const traverseNode = (node) => {
        if (!node) {
            return;
        }

        try {
            if (node.nodeType === Node.TEXT_NODE) {
                addMatches(node.textContent || '');
                return;
            }

            if (node.nodeType === Node.ELEMENT_NODE) {
                const element = node;
                if (typeof element.getAttribute === 'function') {
                    addMatches(element.getAttribute('data-clipboard-text') || '');
                }

                if (element.shadowRoot) {
                    traverseNode(element.shadowRoot);
                }

                if (element.tagName === 'IFRAME') {
                    try {
                        if (element.contentDocument) {
                            traverseNode(element.contentDocument);
                        }
                    } catch {
                    }
                }
            }

            const children = node.childNodes || [];
            for (let index = 0; index < children.length; index++) {
                traverseNode(children[index]);
            }
        } catch {
        }
    };

    traverseNode(document);
    addMatches(document.body?.innerText || '');

    if (candidates.length === 0) {
        return '';
    }

    candidates.sort((left, right) => right.length - left.length);
    return candidates[0] || '';
})();
""";
    private const string ReadTokenFromAutomationStateScript = """
(() => {
    const state = window.__nwsHelperState || {};
    return (state.responseToken || state.token || '').toString().trim();
})();
""";
    private const string ExtractOpenAddressesTokenNearConfirmPathScript = """
(() => {
    const tokenRegex = /oa\.[A-Za-z0-9._~+\-/=]{20,}/g;
    const extractFromText = (text) => {
        const matches = (text || '').toString().match(tokenRegex) || [];
        if (matches.length === 0) {
            return '';
        }

        matches.sort((left, right) => right.length - left.length);
        return matches[0] || '';
    };

    const confirmPath = document.querySelector('path[d="M5 12l5 5l10 -10"]');
    if (!confirmPath) {
        return '';
    }

    const containers = [
        confirmPath.closest('tr'),
        confirmPath.closest('li'),
        confirmPath.closest('div'),
        confirmPath.parentElement,
        confirmPath
    ].filter(Boolean);

    for (const container of containers) {
        const direct = extractFromText(container.textContent || '');
        if (direct) {
            return direct;
        }

        const descendants = Array.from(container.querySelectorAll('[data-clipboard-text], pre, code, div, span, p'));
        for (const element of descendants) {
            const fromAttr = extractFromText(element.getAttribute ? element.getAttribute('data-clipboard-text') : '');
            if (fromAttr) {
                return fromAttr;
            }

            const fromText = extractFromText(element.textContent || '');
            if (fromText) {
                return fromText;
            }
        }
    }

    return '';
})();
""";
    private const string ExtractOpenAddressesTokenFromStateContainerScript = """
(() => {
    const state = window.__nwsHelperState || {};
    const tokenName = (state.uniqueTokenName || '').toString().trim();
    if (!tokenName) {
        return '';
    }

    const tokenRegex = /oa\.[A-Za-z0-9._~+\-/=]{20,}/g;
    const extractFromText = (text) => {
        const matches = (text || '').toString().match(tokenRegex) || [];
        if (matches.length === 0) {
            return '';
        }

        matches.sort((left, right) => right.length - left.length);
        return matches[0] || '';
    };

    const xpaths = [
        `//*[contains(normalize-space(.), '${tokenName}')]/ancestor::tr[1]`,
        `//*[contains(normalize-space(.), '${tokenName}')]/ancestor::li[1]`,
        `//*[contains(normalize-space(.), '${tokenName}')]/ancestor::*[self::tr or self::li or self::div][1]`
    ];

    for (const xpath of xpaths) {
        try {
            const result = document.evaluate(xpath, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null);
            const node = result.singleNodeValue;
            if (!node) {
                continue;
            }

            const direct = extractFromText(node.textContent || '');
            if (direct) {
                return direct;
            }

            const descendants = Array.from(node.querySelectorAll('[data-clipboard-text], pre, code, div, span, p'));
            for (const element of descendants) {
                const fromAttr = extractFromText(element.getAttribute ? element.getAttribute('data-clipboard-text') : '');
                if (fromAttr) {
                    return fromAttr;
                }

                const fromText = extractFromText(element.textContent || '');
                if (fromText) {
                    return fromText;
                }
            }
        } catch {
        }
    }

    return '';
})();
""";
    private readonly bool useEmbeddedOnboardingBrowser;
    private readonly ApiOnboardingType apiOnboardingType;
    private readonly bool enableOpenAddressesAdvancedDiagnostics;
    private string currentOnboardingUrl = RegisterUrl;
    private int scrollRequestVersion;
    private int redirectWatchVersion;
    private int pendingDataRedirectVersion;
    private DateTime lastProfileRedirectAtUtc = DateTime.MinValue;
    private bool hasAutoSubmittedToken;
    private bool isAutoProvisionInProgress;
    private bool isManualScanInProgress;
    private bool isDomPickerActive;
    private bool hasRevealedManualOnboardingControls;
    private string? lastAutomationProgressSnapshot;

    public string LastSuccessfulApiTokenCaptureSummary { get; private set; } = string.Empty;

    public OpenAddressesOnboardingWindow()
        : this(true, ApiOnboardingType.Automated)
    {
    }

    public OpenAddressesOnboardingWindow(
        bool useEmbeddedOnboardingBrowser = true,
        ApiOnboardingType apiOnboardingType = ApiOnboardingType.Automated)
    {
        this.apiOnboardingType = apiOnboardingType;
        this.enableOpenAddressesAdvancedDiagnostics = apiOnboardingType == ApiOnboardingType.Debugging;
        this.useEmbeddedOnboardingBrowser = apiOnboardingType switch
        {
            ApiOnboardingType.FullyManual => false,
            ApiOnboardingType.EmbeddedManual => true,
            ApiOnboardingType.Debugging => true,
            _ => useEmbeddedOnboardingBrowser
        };

        InitializeComponent();
        ApplyOnboardingMode();
        ApplyAdvancedDiagnosticsMode();

        if (this.apiOnboardingType == ApiOnboardingType.Automated)
        {
            ApplySimplifiedOnboardingUx();
        }
        else
        {
            RevealManualOnboardingControls();
        }

        if (this.useEmbeddedOnboardingBrowser)
        {
            UpdateAutomationStatus("Waiting for Profile page view");
        }
        else
        {
            RevealManualOnboardingControls();
            UpdateAutomationStatus("External browser mode. Copy API key and paste below.");
        }

        if (this.useEmbeddedOnboardingBrowser)
        {
            EmbeddedWebView.Navigated += OnEmbeddedWebViewNavigated;
            EmbeddedWebView.PropertyChanged += OnEmbeddedWebViewPropertyChanged;
        }

        _ = NavigateToEmbeddedAsync(RegisterUrl);
    }

    private void ApplySimplifiedOnboardingUx()
    {
        OnboardingInstructionText.Text = SimplifiedOnboardingInstruction;
        SetManualOnboardingControlsVisibility(false);
    }

    private void RevealManualOnboardingControls()
    {
        if (hasRevealedManualOnboardingControls)
        {
            return;
        }

        hasRevealedManualOnboardingControls = true;
        OnboardingInstructionText.Text = ManualOnboardingInstruction;
        SetManualOnboardingControlsVisibility(true);
    }

    private void SetManualOnboardingControlsVisibility(bool isVisible)
    {
        ManualApiEntryPanel.IsVisible = isVisible;
        BottomActionPanel.IsVisible = isVisible;

        var showEmbeddedManualControls = isVisible && useEmbeddedOnboardingBrowser;
        EmbeddedOnboardingTitleText.IsVisible = showEmbeddedManualControls;
        RegisterButton.IsVisible = showEmbeddedManualControls;
        ProfileButton.IsVisible = showEmbeddedManualControls;
        OpenExternalBrowserButton.IsVisible = showEmbeddedManualControls;
        ApiKeySectionLabel.IsVisible = isVisible;
        ApiTokenTextBox.IsVisible = isVisible;
        var showApiTools = isVisible && apiOnboardingType != ApiOnboardingType.FullyManual;
        ApiKeyToolsPanel.IsVisible = showApiTools;
        PasteFromClipboardButton.IsVisible = showApiTools;
        ClearApiKeyButton.IsVisible = showApiTools;
        CancelButton.IsVisible = isVisible && apiOnboardingType != ApiOnboardingType.FullyManual;
        SaveApiKeyButton.IsVisible = isVisible;

        if (useEmbeddedOnboardingBrowser)
        {
            Height = isVisible
                ? (apiOnboardingType == ApiOnboardingType.Debugging ? DebugWindowHeight : ManualWindowHeight)
                : SimplifiedWindowHeight;
            MinHeight = Math.Min(MinHeight, SimplifiedWindowHeight);
        }
        else
        {
            Height = ManualWindowHeight;
        }
    }

    private async void OnPasteFromClipboardClick(object? sender, RoutedEventArgs e)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        var clipboardText = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrWhiteSpace(clipboardText))
        {
            ApiTokenTextBox.Text = clipboardText.Trim();
            ValidationMessage.IsVisible = false;
        }
    }

    private void OnClearApiKeyClick(object? sender, RoutedEventArgs e)
    {
        ApiTokenTextBox.Text = string.Empty;
        ValidationMessage.IsVisible = false;
    }

    private async void OnStartDomPickerClick(object? sender, RoutedEventArgs e)
    {
        if (!enableOpenAddressesAdvancedDiagnostics)
        {
            return;
        }

        if (!useEmbeddedOnboardingBrowser)
        {
            UpdateAutomationStatus("DOM picker is available only in embedded mode.");
            return;
        }

        var result = await EvaluateScriptWithTimeoutAsync(InstallDomPickerScript, ScriptEvaluationTimeoutMs);
        isDomPickerActive = true;
        UpdateAutomationStatus(string.Equals(result, "installed", StringComparison.OrdinalIgnoreCase)
            ? "DOM picker started. Click an element in the embedded page, then click Capture Selected DOM."
            : "DOM picker is already running. Click an element in the embedded page, then click Capture Selected DOM.");
    }

    private async void OnCaptureSelectedDomClick(object? sender, RoutedEventArgs e)
    {
        if (!enableOpenAddressesAdvancedDiagnostics)
        {
            return;
        }

        if (!useEmbeddedOnboardingBrowser)
        {
            UpdateAutomationStatus("DOM picker capture is available only in embedded mode.");
            return;
        }

        if (!isDomPickerActive)
        {
            UpdateAutomationStatus("DOM picker is not active. Click Start DOM Picker first.");
            return;
        }

        var json = await EvaluateScriptWithTimeoutAsync(ReadDomPickerSelectionScript, ScriptEvaluationTimeoutMs);
        if (string.IsNullOrWhiteSpace(json))
        {
            UpdateAutomationStatus("No DOM element captured yet. Click an element in the embedded page first, or click directly on the oa token row.");
            return;
        }

        try
        {
            using var parsed = JsonDocument.Parse(json);
            var formatted = JsonSerializer.Serialize(parsed.RootElement, new JsonSerializerOptions { WriteIndented = true });
            DomExplorerOutputTextBox.Text = formatted;
            UpdateAutomationStatus("Captured selected DOM element. Copy DOM Output to share it.");
        }
        catch
        {
            DomExplorerOutputTextBox.Text = json;
            UpdateAutomationStatus("Captured selected DOM element (raw output).");
        }
    }

    private async void OnStopDomPickerClick(object? sender, RoutedEventArgs e)
    {
        if (!enableOpenAddressesAdvancedDiagnostics)
        {
            return;
        }

        if (!useEmbeddedOnboardingBrowser)
        {
            return;
        }

        await EvaluateScriptWithTimeoutAsync(StopDomPickerScript, ScriptEvaluationTimeoutMs);
        isDomPickerActive = false;
        UpdateAutomationStatus("DOM picker stopped.");
    }

    private async void OnCopyDomOutputClick(object? sender, RoutedEventArgs e)
    {
        if (!enableOpenAddressesAdvancedDiagnostics)
        {
            return;
        }

        var output = DomExplorerOutputTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(output))
        {
            UpdateAutomationStatus("DOM output is empty. Capture a selected element first.");
            return;
        }

        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            UpdateAutomationStatus("Clipboard is not available.");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(output);
            UpdateAutomationStatus("DOM output copied to clipboard.");
        }
        catch
        {
            UpdateAutomationStatus("Failed to copy DOM output to clipboard.");
        }
    }

    private async void OnReadWebViewSnapshotClick(object? sender, RoutedEventArgs e)
    {
        if (!enableOpenAddressesAdvancedDiagnostics)
        {
            return;
        }

        if (!useEmbeddedOnboardingBrowser)
        {
            UpdateAutomationStatus("WebView snapshot is available only in embedded mode.");
            return;
        }

        var probeResult = await TryEvaluateScriptWithTimeoutAsync(ReadWebViewProbeScript, 2000);
        var locationProbeResult = await TryEvaluateScriptWithTimeoutAsync("window.location ? window.location.href : ''", 2000);
        var lightweightSnapshotResult = await TryEvaluateScriptWithTimeoutAsync(ReadWebViewLightweightSnapshotScript, 4000);
        var fullSnapshotResult = await TryEvaluateScriptWithTimeoutAsync(ReadWebViewSnapshotScript, 5000);

        if (!string.IsNullOrWhiteSpace(fullSnapshotResult.Value))
        {
            try
            {
                using var parsed = JsonDocument.Parse(fullSnapshotResult.Value);
                var formatted = JsonSerializer.Serialize(parsed.RootElement, new JsonSerializerOptions { WriteIndented = true });
                DomExplorerOutputTextBox.Text = formatted;
                UpdateAutomationStatus("WebView snapshot captured. Copy DOM Output to share it.");
                return;
            }
            catch
            {
                DomExplorerOutputTextBox.Text = fullSnapshotResult.Value;
                UpdateAutomationStatus("WebView snapshot captured (raw output).");
                return;
            }
        }

        var diagnosticLines = new[]
        {
            "WebView snapshot diagnostics",
            "----------------------------",
            $"EmbeddedWebView.Address: {EmbeddedWebView.Address ?? "(null)"}",
            $"EmbeddedWebView.IsBrowserInitialized: {EmbeddedWebView.IsBrowserInitialized}",
            $"EmbeddedWebView.IsJavascriptEngineInitialized: {EmbeddedWebView.IsJavascriptEngineInitialized}",
            DescribeScriptEvaluationResult("probe", probeResult),
            DescribeScriptEvaluationResult("location", locationProbeResult),
            DescribeScriptEvaluationResult("lightweight", lightweightSnapshotResult),
            DescribeScriptEvaluationResult("full", fullSnapshotResult)
        };

        if (!string.IsNullOrWhiteSpace(lightweightSnapshotResult.Value))
        {
            DomExplorerOutputTextBox.Text = string.Join(Environment.NewLine, diagnosticLines)
                + Environment.NewLine
                + Environment.NewLine
                + "Lightweight snapshot payload:"
                + Environment.NewLine
                + lightweightSnapshotResult.Value;

            UpdateAutomationStatus("Full snapshot failed, but lightweight WebView snapshot succeeded.");
            return;
        }

        var executionChannelUnavailable =
            IsScriptEvaluationChannelUnavailable(probeResult) ||
            IsScriptEvaluationChannelUnavailable(locationProbeResult);

        if (executionChannelUnavailable)
        {
            DomExplorerOutputTextBox.Text = string.Join(Environment.NewLine, diagnosticLines)
                + Environment.NewLine
                + Environment.NewLine
                + "Script evaluation channel appears unavailable for this page/context in embedded mode.";
            UpdateAutomationStatus("Embedded page script access appears blocked/unavailable. Use external browser mode for API key copy.");
            return;
        }

        DomExplorerOutputTextBox.Text = string.Join(Environment.NewLine, diagnosticLines);
        UpdateAutomationStatus("WebView snapshot returned empty output with diagnostics.");
    }

    private async void OnFindKeyOnPageClick(object? sender, RoutedEventArgs e)
    {
        if (!enableOpenAddressesAdvancedDiagnostics)
        {
            return;
        }

        if (isManualScanInProgress)
        {
            return;
        }

        if (!useEmbeddedOnboardingBrowser)
        {
            UpdateAutomationStatus("Manual page scan is available only in embedded mode. Paste key manually.");
            return;
        }

        isManualScanInProgress = true;
        try
        {
            UpdateAutomationStatus("Manual key scan started...");
            string token = string.Empty;
            string source = string.Empty;
            string? lastProgress = null;
            var refreshedAfterWait = false;
            var maxManualScanAttempts = enableOpenAddressesAdvancedDiagnostics
                ? AdvancedManualScanMaxAttempts
                : StandardManualScanMaxAttempts;

            await RefreshDomReadAsync(false);
            UpdateAutomationStatus("Manual key scan refreshed DOM read on click.");

            for (var attempt = 0; attempt < maxManualScanAttempts; attempt++)
            {
                UpdateAutomationStatus($"Manual key scan attempt {attempt + 1}/{maxManualScanAttempts}...");

                var progress = await EvaluateScriptWithTimeoutAsync(ReadAutomationProgressScript, ScriptEvaluationTimeoutMs);
                progress = progress?.Trim();
                if (!string.IsNullOrWhiteSpace(progress))
                {
                    lastProgress = progress;
                }

                var waitMs = ExtractWaitMsFromProgress(progress);
                if (waitMs > 0)
                {
                    refreshedAfterWait = false;
                    UpdateAutomationStatus($"Manual key scan waiting {waitMs}ms before scanning...");
                    await Task.Delay(Math.Min(waitMs, 500));
                    continue;
                }

                if (!refreshedAfterWait)
                {
                    await RefreshDomReadAsync(false);
                    UpdateAutomationStatus("Manual key scan refreshed DOM read after wait.");
                    refreshedAfterWait = true;
                }

                await RefreshDomReadAsync(false);

                (token, source) = await TryScanForApiKeyAsync();
                if (IsOpenAddressesApiToken(token))
                {
                    ApiTokenTextBox.Text = token;
                    ValidationMessage.IsVisible = false;
                    UpdateAutomationStatus($"Manual key scan found API key via {source}.");
                    return;
                }

                await Task.Delay(300);
            }

            if (!string.IsNullOrWhiteSpace(lastProgress))
            {
                UpdateAutomationStatus($"Manual key scan found no key after methods: state token, state container, confirm-path, direct oa, deep DOM, legacy fallback. Last auto-step: {lastProgress}");
            }
            else
            {
                UpdateAutomationStatus("Manual key scan found no key after methods: state token, state container, confirm-path, direct oa, deep DOM, legacy fallback.");
            }
        }
        catch
        {
            UpdateAutomationStatus("Manual key scan failed. Paste key manually.");
        }
        finally
        {
            isManualScanInProgress = false;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void OnSaveAndContinueClick(object? sender, RoutedEventArgs e)
    {
        var apiToken = ApiTokenTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(apiToken))
        {
            ValidationMessage.IsVisible = true;
            return;
        }

        ValidationMessage.IsVisible = false;
        LastSuccessfulApiTokenCaptureSummary = BuildSuccessSummary();
        Close(apiToken);
    }

    private string BuildSuccessSummary()
    {
        var mode = useEmbeddedOnboardingBrowser ? "Embedded" : "External";
        var status = AutomationStatusText.Text?.Trim() ?? string.Empty;
        if (status.StartsWith("Status:", StringComparison.OrdinalIgnoreCase))
        {
            status = status["Status:".Length..].Trim();
        }

        if (string.IsNullOrWhiteSpace(status) ||
            status.StartsWith("Opening ", StringComparison.OrdinalIgnoreCase) ||
            status.StartsWith("Waiting for profile page", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "API key copied to textbox.", StringComparison.OrdinalIgnoreCase))
        {
            status = hasAutoSubmittedToken
                ? "API key saved from auto-detected token."
                : "API key saved from pasted token.";
        }

        return $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{mode}] {status}";
    }

    private void OnOpenRegisterClick(object? sender, RoutedEventArgs e)
    {
        _ = NavigateToEmbeddedAsync(RegisterUrl);
    }

    private void OnOpenProfileClick(object? sender, RoutedEventArgs e)
    {
        _ = NavigateToEmbeddedAsync(ProfileUrl);
    }

    private void OnOpenExternalBrowserClick(object? sender, RoutedEventArgs e)
    {
        OpenUrl(currentOnboardingUrl);
    }

    private void ApplyOnboardingMode()
    {
        EmbeddedOnboardingPanel.IsVisible = useEmbeddedOnboardingBrowser;
        FallbackOnboardingPanel.IsVisible = !useEmbeddedOnboardingBrowser;
    }

    private void ApplyAdvancedDiagnosticsMode()
    {
        FindKeyOnPageButton.IsVisible = enableOpenAddressesAdvancedDiagnostics;
        StartDomPickerButton.IsVisible = enableOpenAddressesAdvancedDiagnostics;
        CaptureSelectedDomButton.IsVisible = enableOpenAddressesAdvancedDiagnostics;
        StopDomPickerButton.IsVisible = enableOpenAddressesAdvancedDiagnostics;
        CopyDomOutputButton.IsVisible = enableOpenAddressesAdvancedDiagnostics;
        ReadWebViewSnapshotButton.IsVisible = enableOpenAddressesAdvancedDiagnostics;
        DomExplorerOutputTextBox.IsVisible = enableOpenAddressesAdvancedDiagnostics;
    }

    private Task NavigateToEmbeddedAsync(string url)
    {
        currentOnboardingUrl = url;
        if (IsOpenAddressesProfileUrl(url))
        {
            UpdateAutomationStatus("Opening profile page...");
        }
        else if (string.Equals(url, RegisterUrl, StringComparison.OrdinalIgnoreCase))
        {
            UpdateAutomationStatus("Waiting for Profile page view");
        }

        if (!useEmbeddedOnboardingBrowser)
        {
            OpenUrl(url);
            return Task.CompletedTask;
        }

        try
        {
            EmbeddedWebView.Address = url;
            var watchVersion = ++redirectWatchVersion;
            _ = WatchForDataRedirectAsync(watchVersion);
        }
        catch
        {
            OpenUrl(url);
        }

        return Task.CompletedTask;
    }

    private void OnEmbeddedWebViewNavigated(string url, string frameName)
    {
        if (TryRedirectDataUrlToProfile(url))
        {
            return;
        }

        var isMainFrame = string.IsNullOrWhiteSpace(frameName) ||
                          string.Equals(frameName, "main", StringComparison.OrdinalIgnoreCase);
        if (!isMainFrame)
        {
            return;
        }

        if (!IsOpenAddressesProfileUrl(url))
        {
            UpdateAutomationStatus("Waiting for Profile page view");
        }

        RequestScrollToBottom();

        if (IsOpenAddressesProfileUrl(url))
        {
            if (apiOnboardingType == ApiOnboardingType.EmbeddedManual)
            {
                UpdateAutomationStatus("Profile page loaded. Paste API key manually.");
            }
            else
            {
                UpdateAutomationStatus("Profile page loaded. Attempting API key automation...");
                _ = TryAutoCreateApiKeyAndSubmitAsync();
            }
        }
    }

    private void OnEmbeddedWebViewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WebView.AddressProperty || e.NewValue is not string url)
        {
            return;
        }

        if (IsOpenAddressesProfileUrl(url))
        {
            RequestScrollToBottom();
            if (apiOnboardingType == ApiOnboardingType.EmbeddedManual)
            {
                UpdateAutomationStatus("Profile URL detected. Paste API key manually.");
            }
            else
            {
                UpdateAutomationStatus("Profile URL detected. Attempting API key automation...");
                _ = TryAutoCreateApiKeyAndSubmitAsync();
            }
        }

        _ = TryRedirectDataUrlToProfile(url);
    }

    private bool TryRedirectDataUrlToProfile(string? url)
    {
        if (!IsOpenAddressesDataUrl(url))
        {
            return false;
        }

        var utcNow = DateTime.UtcNow;
        if ((utcNow - lastProfileRedirectAtUtc) < TimeSpan.FromSeconds(1))
        {
            UpdateAutomationStatus("Data URL detected; redirect already in progress...");
            return true;
        }

        lastProfileRedirectAtUtc = utcNow;
        var redirectVersion = ++pendingDataRedirectVersion;
        UpdateAutomationStatus($"Data URL detected; redirecting to profile in {DataToProfileRedirectDelayMs}ms...");

        _ = RedirectDataUrlToProfileAsync(redirectVersion);

        return true;
    }

    private async Task RedirectDataUrlToProfileAsync(int redirectVersion)
    {
        await Task.Delay(DataToProfileRedirectDelayMs);

        if (redirectVersion != pendingDataRedirectVersion)
        {
            return;
        }

        var currentUrl = EmbeddedWebView.Address;
        if (!IsOpenAddressesDataUrl(currentUrl))
        {
            return;
        }

        UpdateAutomationStatus("Redirecting to profile page...");

        _ = NavigateToEmbeddedAsync(ProfileUrl);
    }

    private static bool IsOpenAddressesDataUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Host, "batch.openaddresses.io", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(uri.AbsolutePath.TrimEnd('/'), "/data", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOpenAddressesProfileUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Host, "batch.openaddresses.io", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(uri.AbsolutePath.TrimEnd('/'), "/profile", StringComparison.OrdinalIgnoreCase);
    }

    private void RequestScrollToBottom()
    {
        var requestVersion = ++scrollRequestVersion;
        _ = ScrollToBottomWithRetriesAsync(requestVersion);
    }

    private async Task ScrollToBottomWithRetriesAsync(int requestVersion)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await Task.Delay(250);

            if (requestVersion != scrollRequestVersion)
            {
                return;
            }

            try
            {
                EmbeddedWebView.ExecuteScript(ScrollToBottomScript);
            }
            catch
            {
            }
        }
    }

    private async Task TryAutoCreateApiKeyAndSubmitAsync()
    {
        if (apiOnboardingType == ApiOnboardingType.EmbeddedManual)
        {
            return;
        }

        if (!useEmbeddedOnboardingBrowser || hasAutoSubmittedToken || isAutoProvisionInProgress)
        {
            return;
        }

        isAutoProvisionInProgress = true;
        try
        {
            var probe = await TryEvaluateScriptWithTimeoutAsync(ReadWebViewProbeScript, 2000);
            if (IsScriptEvaluationChannelUnavailable(probe))
            {
                RevealManualOnboardingControls();
                UpdateAutomationStatus("Embedded script access is unavailable. Click Open in External Browser, then paste the API key.");
                return;
            }

            var maxAutomationAttempts = enableOpenAddressesAdvancedDiagnostics
                ? AdvancedAutomationMaxAttempts
                : StandardAutomationMaxAttempts;
            lastAutomationProgressSnapshot = null;
            UpdateAutomationStatus("Trying to create API key...");
            string token = string.Empty;
            var refreshedAfterWait = false;
            for (var attempt = 0; attempt < maxAutomationAttempts; attempt++)
            {
                var progress = await EvaluateScriptWithTimeoutAsync(ReadAutomationProgressScript, ScriptEvaluationTimeoutMs);
                progress = progress?.Trim();
                if (!string.IsNullOrWhiteSpace(progress))
                {
                    if (enableOpenAddressesAdvancedDiagnostics)
                    {
                        lastAutomationProgressSnapshot = progress;
                    }

                    var waitMs = ExtractWaitMsFromProgress(progress);
                    if (waitMs > 0)
                    {
                        refreshedAfterWait = false;
                        if (enableOpenAddressesAdvancedDiagnostics)
                        {
                            UpdateAutomationStatus($"Auto-step {attempt + 1}/{maxAutomationAttempts}: {progress}");
                        }

                        await Task.Delay(Math.Min(waitMs, 500));
                        continue;
                    }

                    if (!refreshedAfterWait)
                    {
                        await RefreshDomReadAsync(false);
                        refreshedAfterWait = true;
                    }
                }
                else
                {
                    if (enableOpenAddressesAdvancedDiagnostics)
                    {
                        lastAutomationProgressSnapshot = $"progress-unavailable;attempt={attempt + 1}/{maxAutomationAttempts}";
                        UpdateAutomationStatus($"API key not found yet; retrying page automation ({attempt + 1}/{maxAutomationAttempts})...");
                    }
                }

                token = await EvaluateScriptWithTimeoutAsync(AutoCreateAndReadApiKeyScript, ScriptEvaluationTimeoutMs);
                token = NormalizeTokenValue(token);
                token = IsOpenAddressesApiToken(token) ? token : string.Empty;
                if (!string.IsNullOrWhiteSpace(token))
                {
                    if (enableOpenAddressesAdvancedDiagnostics)
                    {
                        var progressSuffix = string.IsNullOrWhiteSpace(lastAutomationProgressSnapshot)
                            ? string.Empty
                            : $" Last auto-step: {lastAutomationProgressSnapshot}";
                        UpdateAutomationStatus($"API key found via auto-step {attempt + 1}/{maxAutomationAttempts}.{progressSuffix}");
                    }
                    else
                    {
                        UpdateAutomationStatus("API key found automatically.");
                    }

                    break;
                }

                await Task.Delay(350);
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                UpdateAutomationStatus(enableOpenAddressesAdvancedDiagnostics
                    ? "Trying direct oa.* token scan..."
                    : "Trying token scan...");
                token = await EvaluateScriptWithTimeoutAsync(ExtractOpenAddressesTokenSimpleScript, ScriptEvaluationTimeoutMs);
                token = NormalizeTokenValue(token);
                token = IsOpenAddressesApiToken(token) ? token : string.Empty;
                if (!string.IsNullOrWhiteSpace(token))
                {
                    UpdateAutomationStatus(enableOpenAddressesAdvancedDiagnostics
                        ? "API key found via direct oa.* scan."
                        : "API key found automatically.");
                }
            }

            if (enableOpenAddressesAdvancedDiagnostics && string.IsNullOrWhiteSpace(token))
            {
                UpdateAutomationStatus("Trying deep DOM token scan...");
                token = await EvaluateScriptWithTimeoutAsync(ExtractOpenAddressesTokenDeepScanScript, ScriptEvaluationTimeoutMs);
                token = NormalizeTokenValue(token);
                token = IsOpenAddressesApiToken(token) ? token : string.Empty;
                if (!string.IsNullOrWhiteSpace(token))
                {
                    UpdateAutomationStatus("API key found via deep DOM scan.");
                }
            }

            if (enableOpenAddressesAdvancedDiagnostics && string.IsNullOrWhiteSpace(token))
            {
                UpdateAutomationStatus("Trying fallback token detection...");
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    token = await EvaluateScriptWithTimeoutAsync(ExtractTokenFromDomScript, ScriptEvaluationTimeoutMs);
                    token = NormalizeTokenValue(token);
                    token = IsOpenAddressesApiToken(token) ? token : string.Empty;
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        UpdateAutomationStatus("API key found via page fallback.");
                        break;
                    }

                    await Task.Delay(300);
                }

            }

            if (string.IsNullOrWhiteSpace(token))
            {
                RevealManualOnboardingControls();
                if (enableOpenAddressesAdvancedDiagnostics)
                {
                    var finalProgress = await EvaluateScriptWithTimeoutAsync(ReadAutomationProgressScript, ScriptEvaluationTimeoutMs);
                    finalProgress = finalProgress?.Trim();
                    if (!string.IsNullOrWhiteSpace(finalProgress))
                    {
                        lastAutomationProgressSnapshot = finalProgress;
                    }
                }

                if (enableOpenAddressesAdvancedDiagnostics && !string.IsNullOrWhiteSpace(lastAutomationProgressSnapshot))
                {
                    UpdateAutomationStatus($"API key not detected automatically from page. Last auto-step: {lastAutomationProgressSnapshot}. Paste key manually.");
                }
                else
                {
                    var advancedSuffix = enableOpenAddressesAdvancedDiagnostics
                        ? string.Empty
                        : " Enable advanced OpenAddresses onboarding diagnostics in Settings to run supplemental scan methods.";
                    UpdateAutomationStatus($"API key not detected automatically from page. Paste key manually.{advancedSuffix}");
                }

                return;
            }

            hasAutoSubmittedToken = true;
            ApiTokenTextBox.Text = token;
            ValidationMessage.IsVisible = false;
            OnSaveAndContinueClick(this, new RoutedEventArgs());
        }
        catch
        {
            RevealManualOnboardingControls();
            UpdateAutomationStatus("Automation encountered an error. Paste key manually.");
        }
        finally
        {
            isAutoProvisionInProgress = false;
        }
    }

    private async Task<(string Token, string Source)> TryScanForApiKeyAsync()
    {
        var probe = await TryEvaluateScriptWithTimeoutAsync(ReadWebViewProbeScript, 2000);
        if (IsScriptEvaluationChannelUnavailable(probe))
        {
            return (string.Empty, "script-evaluation-unavailable");
        }

        var token = NormalizeTokenValue(await EvaluateScriptWithTimeoutAsync(ReadTokenFromAutomationStateScript, ScriptEvaluationTimeoutMs));
        if (IsOpenAddressesApiToken(token))
        {
            return (token, "automation state token");
        }

        token = NormalizeTokenValue(await EvaluateScriptWithTimeoutAsync(ExtractOpenAddressesTokenFromStateContainerScript, ScriptEvaluationTimeoutMs));
        if (IsOpenAddressesApiToken(token))
        {
            return (token, "state-linked container scan");
        }

        token = NormalizeTokenValue(await EvaluateScriptWithTimeoutAsync(ExtractOpenAddressesTokenNearConfirmPathScript, ScriptEvaluationTimeoutMs));
        if (IsOpenAddressesApiToken(token))
        {
            return (token, "confirm-path adjacent scan");
        }

        token = NormalizeTokenValue(await EvaluateScriptWithTimeoutAsync(ExtractOpenAddressesTokenSimpleScript, ScriptEvaluationTimeoutMs));
        if (IsOpenAddressesApiToken(token))
        {
            return (token, "direct oa.* scan");
        }

        token = NormalizeTokenValue(await EvaluateScriptWithTimeoutAsync(ExtractOpenAddressesTokenDeepScanScript, ScriptEvaluationTimeoutMs));
        if (IsOpenAddressesApiToken(token))
        {
            return (token, "deep DOM scan");
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            token = NormalizeTokenValue(await EvaluateScriptWithTimeoutAsync(ExtractTokenFromDomScript, ScriptEvaluationTimeoutMs));
            if (IsOpenAddressesApiToken(token))
            {
                return (token, "legacy DOM fallback");
            }

            await Task.Delay(250);
        }

        return (string.Empty, string.Empty);
    }

    private static bool IsOpenAddressesApiToken(string? token)
    {
        return !string.IsNullOrWhiteSpace(token) &&
               token.StartsWith("oa.", StringComparison.OrdinalIgnoreCase) &&
               token.Length >= 20;
    }

    private static int ExtractWaitMsFromProgress(string? progress)
    {
        if (string.IsNullOrWhiteSpace(progress))
        {
            return 0;
        }

        var match = Regex.Match(progress, "waitMs=(\\d+)");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var waitMs))
        {
            return 0;
        }

        return Math.Max(0, waitMs);
    }

    private async Task<string> RefreshDomReadAsync(bool includeStatus)
    {
        if (!useEmbeddedOnboardingBrowser)
        {
            return string.Empty;
        }

        try
        {
            var signature = await EvaluateScriptWithTimeoutAsync(ForceDomReadRefreshScript, ScriptEvaluationTimeoutMs);
            signature = signature?.Trim() ?? string.Empty;
            if (includeStatus)
            {
                if (string.IsNullOrWhiteSpace(signature))
                {
                    UpdateAutomationStatus("DOM read refresh attempted, but no signature was returned.");
                }
                else
                {
                    UpdateAutomationStatus("DOM read refreshed.");
                }
            }

            return signature;
        }
        catch
        {
            if (includeStatus)
            {
                UpdateAutomationStatus("DOM read refresh failed.");
            }

            return string.Empty;
        }
    }

    private async Task<string> EvaluateScriptWithTimeoutAsync(string script, int timeoutMs)
    {
        var result = await TryEvaluateScriptWithTimeoutAsync(script, timeoutMs);
        return result.Value;
    }

    private async Task<ScriptEvaluationResult> TryEvaluateScriptWithTimeoutAsync(string script, int timeoutMs)
    {
        try
        {
            var scriptToEvaluate = BuildReturningScript(script);
            var evaluationTask = EmbeddedWebView.EvaluateScript<string>(scriptToEvaluate);
            var completedTask = await Task.WhenAny(evaluationTask, Task.Delay(timeoutMs));
            if (completedTask != evaluationTask)
            {
                return new ScriptEvaluationResult(string.Empty, TimedOut: true, Error: null);
            }

            var value = (await evaluationTask)?.Trim() ?? string.Empty;
            return new ScriptEvaluationResult(value, TimedOut: false, Error: null);
        }
        catch (Exception ex)
        {
            return new ScriptEvaluationResult(string.Empty, TimedOut: false, Error: ex.Message);
        }
    }

    private static string BuildReturningScript(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return "return '';";
        }

        var trimmed = script.Trim();
        if (trimmed.StartsWith("return ", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var expression = trimmed.TrimEnd(';').Trim();
        return $"return ({expression});";
    }

    private static string DescribeScriptEvaluationResult(string name, ScriptEvaluationResult result)
    {
        if (result.TimedOut)
        {
            return $"{name}: timeout";
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            return $"{name}: error={result.Error}";
        }

        if (string.IsNullOrWhiteSpace(result.Value))
        {
            return $"{name}: success-empty";
        }

        var preview = result.Value.Length > 180
            ? result.Value[..180] + "..."
            : result.Value;
        preview = preview.Replace("\r", " ").Replace("\n", " ");
        return $"{name}: success len={result.Value.Length} preview={preview}";
    }

    private readonly record struct ScriptEvaluationResult(string Value, bool TimedOut, string? Error);

    private static bool IsScriptEvaluationChannelUnavailable(ScriptEvaluationResult result)
    {
        return result.TimedOut || !string.IsNullOrWhiteSpace(result.Error);
    }

    private static string NormalizeTokenValue(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        var normalized = token.Trim();
        if (normalized.Length > 1 && normalized[0] == '"' && normalized[^1] == '"')
        {
            normalized = normalized[1..^1]
                .Replace("\\\"", "\"")
                .Replace("\\n", "\n")
                .Trim();
        }

        var tokenMatches = Regex.Matches(normalized, "[A-Za-z0-9._~+/=-]{20,}");
        if (tokenMatches.Count > 0)
        {
            string? oaSelected = null;
            var selected = tokenMatches[0].Value;
            for (var index = 1; index < tokenMatches.Count; index++)
            {
                if (tokenMatches[index].Value.Length > selected.Length)
                {
                    selected = tokenMatches[index].Value;
                }
            }

            for (var index = 0; index < tokenMatches.Count; index++)
            {
                var candidate = tokenMatches[index].Value;
                if (!candidate.StartsWith("oa.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (oaSelected is null || candidate.Length > oaSelected.Length)
                {
                    oaSelected = candidate;
                }
            }

            return (oaSelected ?? selected).Trim();
        }

        return normalized;
    }

    private async Task WatchForDataRedirectAsync(int watchVersion)
    {
        if (!useEmbeddedOnboardingBrowser)
        {
            return;
        }

        for (var attempt = 0; attempt < 24; attempt++)
        {
            await Task.Delay(250);

            if (watchVersion != redirectWatchVersion)
            {
                return;
            }

            var currentUrl = EmbeddedWebView.Address;
            if (TryRedirectDataUrlToProfile(currentUrl))
            {
                return;
            }
        }
    }

    private void UpdateAutomationStatus(string status)
    {
        void SetStatus()
        {
            AutomationStatusText.Text = $"Status: {status}";
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            SetStatus();
            return;
        }

        Dispatcher.UIThread.Post(SetStatus);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }
}