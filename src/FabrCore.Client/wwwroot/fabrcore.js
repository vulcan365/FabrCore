// ChatDock JavaScript Module
// Provides DOM manipulation functions for the ChatDock Blazor component

export function moveToBody(panelId) {
    const panel = document.getElementById(panelId);
    if (panel && panel.parentElement !== document.body) {
        document.body.appendChild(panel);
        return true;
    }
    return false;
}

export function removePanel(panelId) {
    const panel = document.getElementById(panelId);
    if (panel) {
        panel.remove();
        return true;
    }
    return false;
}

export function scrollToBottom(element) {
    if (element) {
        element.scrollTop = element.scrollHeight;
        return true;
    }
    return false;
}
