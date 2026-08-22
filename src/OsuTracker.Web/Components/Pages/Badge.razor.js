// Clipboard access needs a user gesture and a secure context. The Blazor click that
// reaches here counts as the gesture, but plain http on a LAN address is not a secure
// context, so the execCommand path stays as the fallback for that case.
export async function copy(text) {
    try {
        if (navigator.clipboard && window.isSecureContext) {
            await navigator.clipboard.writeText(text);
            return true;
        }
    } catch {
        // fall through
    }

    const box = document.createElement("textarea");
    box.value = text;
    box.setAttribute("readonly", "");
    box.style.position = "fixed";
    box.style.opacity = "0";
    document.body.appendChild(box);
    box.select();

    try {
        return document.execCommand("copy");
    } finally {
        document.body.removeChild(box);
    }
}
