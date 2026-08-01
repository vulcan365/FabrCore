const adaptiveCardsUrl = "https://cdn.jsdelivr.net/npm/adaptivecards@3.0.5/dist/adaptivecards.min.js";

let adaptiveCardsPromise;
const composerHandlers = new WeakMap();

export async function renderAdaptiveCard(container, envelopeId, cardJson, dotNetRef) {
    if (!container) {
        return;
    }

    const AdaptiveCards = await loadAdaptiveCards();
    const adaptiveCard = new AdaptiveCards.AdaptiveCard();
    adaptiveCard.hostConfig = createSurfaceHostConfig(AdaptiveCards);

    adaptiveCard.onExecuteAction = action => {
        dispatchAction(dotNetRef, envelopeId, action, adaptiveCard);
    };

    adaptiveCard.onAction = action => {
        const json = actionToJson(action);
        if (json.type === "Action.OpenUrl" && json.url) {
            window.open(json.url, "_blank", "noopener,noreferrer");
            return;
        }

        if (json.type === "Action.Submit") {
            dispatchAction(dotNetRef, envelopeId, action, adaptiveCard);
        }
    };

    adaptiveCard.parse(cardJson);
    const rendered = adaptiveCard.render();
    rendered.classList?.add("surface-adaptive-card");
    container.replaceChildren(rendered);
    dotNetRef.invokeMethodAsync("OnAdaptiveCardRenderedAsync", envelopeId, countRenderedActions(rendered));
    scrollTimelineIfLatestDescendant(container);
}

function createSurfaceHostConfig(AdaptiveCards) {
    return new AdaptiveCards.HostConfig({
        supportsInteractivity: true,
        fontFamily: "Inter, Segoe UI, Roboto, Helvetica Neue, Arial, sans-serif",
        fontSizes: {
            small: 12,
            default: 14,
            medium: 18,
            large: 22,
            extraLarge: 26
        },
        fontWeights: {
            lighter: 300,
            default: 400,
            bolder: 700
        },
        spacing: {
            small: 6,
            default: 10,
            medium: 14,
            large: 18,
            extraLarge: 24,
            padding: 18
        },
        separator: {
            lineThickness: 1,
            lineColor: "#e4eaf2"
        },
        containerStyles: {
            default: {
                backgroundColor: "#ffffff",
                foregroundColors: surfaceForegroundColors("#18202f", "#667085")
            },
            emphasis: {
                backgroundColor: "#f6f8fb",
                foregroundColors: surfaceForegroundColors("#18202f", "#667085")
            },
            good: {
                backgroundColor: "#ecfdf5",
                foregroundColors: surfaceForegroundColors("#065f46", "#047857")
            },
            warning: {
                backgroundColor: "#fffbeb",
                foregroundColors: surfaceForegroundColors("#7c2d12", "#92400e")
            },
            attention: {
                backgroundColor: "#fff1f2",
                foregroundColors: surfaceForegroundColors("#881337", "#be123c")
            },
            accent: {
                backgroundColor: "#eef6ff",
                foregroundColors: surfaceForegroundColors("#12315c", "#315b91")
            }
        },
        factSet: {
            title: {
                color: "default",
                size: "default",
                isSubtle: true,
                weight: "default",
                wrap: true
            },
            value: {
                color: "default",
                size: "default",
                isSubtle: false,
                weight: "bolder",
                wrap: true
            },
            spacing: 10
        },
        inputs: {
            label: {
                requiredInputs: {
                    weight: "bolder",
                    suffix: " *",
                    suffixColor: "attention"
                },
                optionalInputs: {
                    weight: "bolder"
                }
            },
            errorMessage: {
                color: "attention",
                weight: "bolder"
            }
        },
        actions: {
            maxActions: 6,
            spacing: "medium",
            buttonSpacing: 8,
            actionsOrientation: "horizontal",
            actionAlignment: "left",
            showCard: {
                actionMode: "inline",
                inlineTopMargin: 12,
                style: "emphasis"
            },
            actions: {
                positive: {
                    style: "positive"
                },
                destructive: {
                    style: "destructive"
                }
            }
        }
    });
}

function surfaceForegroundColors(defaultColor, subtleColor) {
    return {
        default: {
            default: defaultColor,
            subtle: subtleColor
        },
        dark: {
            default: "#18202f",
            subtle: "#667085"
        },
        light: {
            default: "#ffffff",
            subtle: "rgba(255, 255, 255, 0.72)"
        },
        accent: {
            default: "#1f6feb",
            subtle: "#4f7ebd"
        },
        good: {
            default: "#047857",
            subtle: "#2f8f70"
        },
        warning: {
            default: "#b45309",
            subtle: "#d97706"
        },
        attention: {
            default: "#be123c",
            subtle: "#e11d48"
        }
    };
}

export function getRenderedHeight(container) {
    if (!container || typeof container.getBoundingClientRect !== "function") {
        return 0;
    }

    return Math.ceil(container.getBoundingClientRect().height);
}

export function bindSurfaceComposer(textarea, dotNetRef) {
    if (!textarea) {
        return;
    }

    if (typeof textarea.addEventListener !== "function") {
        return;
    }

    const previous = composerHandlers.get(textarea);
    if (previous) {
        textarea.removeEventListener("keydown", previous);
    }

    const handler = event => {
        if (textarea.dataset.surfaceMentionOpen === "true"
            && ["ArrowDown", "ArrowUp", "Enter", "Tab", "Escape"].includes(event.key)) {
            event.preventDefault();
            dotNetRef.invokeMethodAsync("HandleComposerMentionKeyAsync", event.key);
            return;
        }

        if (event.key !== "Enter" || event.shiftKey || event.altKey || event.ctrlKey || event.metaKey || event.isComposing) {
            return;
        }

        event.preventDefault();
        dotNetRef.invokeMethodAsync("SendComposerMessageAsync");
    };

    composerHandlers.set(textarea, handler);
    textarea.addEventListener("keydown", handler);
}

export function focusSurfaceComposer(textarea) {
    if (!textarea || textarea.disabled || typeof textarea.focus !== "function") {
        return;
    }

    textarea.focus();
    if (typeof textarea.setSelectionRange === "function") {
        const end = textarea.value.length;
        textarea.setSelectionRange(end, end);
    }
}

export async function copyTextToClipboard(text) {
    const value = text ?? "";
    if (navigator.clipboard?.writeText && window.isSecureContext) {
        await navigator.clipboard.writeText(value);
        return;
    }

    const textarea = document.createElement("textarea");
    textarea.value = value;
    textarea.setAttribute("readonly", "");
    textarea.style.position = "fixed";
    textarea.style.top = "-1000px";
    textarea.style.left = "-1000px";
    document.body.appendChild(textarea);
    textarea.select();

    try {
        document.execCommand("copy");
    } finally {
        textarea.remove();
    }
}

export function scrollSurfaceTimelineToLatest(timeline) {
    if (!timeline || typeof timeline.querySelectorAll !== "function") {
        return;
    }

    const items = getTimelineScrollTargets(timeline);
    const latest = items[items.length - 1];
    if (!latest) {
        return;
    }

    const scrollToLatest = () => {
        const style = window.getComputedStyle?.(timeline);
        const paddingTop = Number.parseFloat(style?.paddingTop ?? "0") || 0;
        const paddingBottom = Number.parseFloat(style?.paddingBottom ?? "0") || 0;
        const visibleHeight = Math.max(0, timeline.clientHeight - paddingTop - paddingBottom);
        const timelineRect = timeline.getBoundingClientRect();
        const latestRect = latest.getBoundingClientRect();
        const latestTop = timeline.scrollTop + latestRect.top - timelineRect.top - paddingTop;
        const latestBottom = timeline.scrollTop + latestRect.bottom - timelineRect.top + paddingBottom;
        const desiredScrollTop = latestRect.height > visibleHeight
            ? latestTop
            : latestBottom - timeline.clientHeight;
        const maxScrollTop = Math.max(0, timeline.scrollHeight - timeline.clientHeight);

        timeline.scrollTop = Math.min(Math.max(0, desiredScrollTop), maxScrollTop);
    };

    requestAnimationFrame(() => {
        scrollToLatest();
        requestAnimationFrame(() => {
            scrollToLatest();
        });
    });
}

function scrollTimelineIfLatestDescendant(element) {
    const timeline = element?.closest?.(".surface-timeline");
    if (!timeline) {
        return;
    }

    const items = getTimelineScrollTargets(timeline);
    const latest = items[items.length - 1];
    if (latest?.contains(element)) {
        scrollSurfaceTimelineToLatest(timeline);
    }
}

function getTimelineScrollTargets(timeline) {
    return timeline.querySelectorAll("[data-surface-timeline-item], [data-surface-timeline-activity]");
}

async function loadAdaptiveCards() {
    if (window.AdaptiveCards) {
        return window.AdaptiveCards;
    }

    adaptiveCardsPromise ??= new Promise((resolve, reject) => {
        const script = document.createElement("script");
        script.src = adaptiveCardsUrl;
        script.async = true;
        script.onload = () => resolve(window.AdaptiveCards);
        script.onerror = () => reject(new Error("Unable to load Adaptive Cards renderer."));
        document.head.appendChild(script);
    });

    return adaptiveCardsPromise;
}

function dispatchAction(dotNetRef, envelopeId, action, adaptiveCard) {
    const json = actionToJson(action);
    const inputs = collectInputs(adaptiveCard);
    dotNetRef.invokeMethodAsync("OnAdaptiveCardActionAsync", envelopeId, json, inputs);
}

function actionToJson(action) {
    if (typeof action.getJson === "function") {
        return action.getJson();
    }

    return {
        type: action.getJsonTypeName?.() || action.type || "Action.Execute",
        title: action.title,
        verb: action.verb,
        url: action.url,
        data: action.data
    };
}

function collectInputs(adaptiveCard) {
    const values = {};
    if (typeof adaptiveCard.getAllInputs !== "function") {
        return values;
    }

    for (const input of adaptiveCard.getAllInputs()) {
        if (input.id) {
            values[input.id] = input.value;
        }
    }

    return values;
}

function countRenderedActions(rendered) {
    if (!rendered || typeof rendered.querySelectorAll !== "function") {
        return 0;
    }

    return rendered.querySelectorAll("button, a").length;
}
