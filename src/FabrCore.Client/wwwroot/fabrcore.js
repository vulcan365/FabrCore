// ChatDock JavaScript Module
// Provides DOM manipulation functions for the ChatDock Blazor component

export function scrollToBottom(element) {
    if (element) {
        element.scrollTop = element.scrollHeight;
        return true;
    }
    return false;
}

// P2: Register keydown handler that only calls .NET for Enter without Shift.
// This avoids sending JS→.NET interop for every keypress.
export function registerKeyHandler(textareaElement, dotNetRef) {
    if (!textareaElement) return;

    textareaElement.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            dotNetRef.invokeMethodAsync('OnEnterPressed');
        }
    });
}
