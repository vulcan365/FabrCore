const adaptiveCardsUrl = "https://cdn.jsdelivr.net/npm/adaptivecards@3.0.5/dist/adaptivecards.min.js";

let adaptiveCardsPromise;

export async function renderAdaptiveCard(container, envelopeId, cardJson, dotNetRef) {
    if (!container) {
        return;
    }

    const AdaptiveCards = await loadAdaptiveCards();
    const adaptiveCard = new AdaptiveCards.AdaptiveCard();

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
    container.replaceChildren(rendered);
    dotNetRef.invokeMethodAsync("OnAdaptiveCardRenderedAsync", envelopeId, countRenderedActions(rendered));
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
