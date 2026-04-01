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

// Register drag-and-drop file handler on the chat dock panel.
// Prevents the browser's default file-open behavior at the document level,
// and uses the entire panel as the drop target for easier aiming.
export function registerDropHandler(panelElement, inputWrapperElement, dotNetRef) {
    if (!panelElement || !inputWrapperElement) return;

    const MAX_SIZE = 10 * 1024 * 1024; // 10 MB
    let dragCounter = 0;

    // Prevent browser from opening dropped files anywhere on the document.
    // Without this, Edge/Chrome navigate to the file on drop.
    document.addEventListener('dragover', (e) => { e.preventDefault(); });
    document.addEventListener('drop', (e) => { e.preventDefault(); });

    // Visual feedback and drop handling on the panel
    panelElement.addEventListener('dragenter', (e) => {
        e.preventDefault();
        e.stopPropagation();
        dragCounter++;
        if (dragCounter === 1) {
            inputWrapperElement.classList.add('chat-dock-drag-over');
        }
    });

    panelElement.addEventListener('dragover', (e) => {
        e.preventDefault();
        e.stopPropagation();
    });

    panelElement.addEventListener('dragleave', (e) => {
        e.preventDefault();
        e.stopPropagation();
        dragCounter--;
        if (dragCounter <= 0) {
            dragCounter = 0;
            inputWrapperElement.classList.remove('chat-dock-drag-over');
        }
    });

    panelElement.addEventListener('drop', (e) => {
        e.preventDefault();
        e.stopPropagation();
        dragCounter = 0;
        inputWrapperElement.classList.remove('chat-dock-drag-over');

        const files = e.dataTransfer.files;
        if (!files || files.length === 0) return;

        for (let i = 0; i < files.length; i++) {
            const file = files[i];
            if (file.size > MAX_SIZE) {
                dotNetRef.invokeMethodAsync('OnFileError', file.name, 'File too large (max 10 MB)');
                continue;
            }
            const reader = new FileReader();
            reader.onload = () => {
                const base64 = arrayBufferToBase64(reader.result);
                dotNetRef.invokeMethodAsync('OnFileDropped', file.name, base64, file.size);
            };
            reader.onerror = () => {
                dotNetRef.invokeMethodAsync('OnFileError', file.name, 'Failed to read file');
            };
            reader.readAsArrayBuffer(file);
        }
    });
}

function arrayBufferToBase64(buffer) {
    const bytes = new Uint8Array(buffer);
    let binary = '';
    const chunkSize = 8192;
    for (let i = 0; i < bytes.length; i += chunkSize) {
        const chunk = bytes.subarray(i, i + chunkSize);
        binary += String.fromCharCode.apply(null, chunk);
    }
    return btoa(binary);
}
