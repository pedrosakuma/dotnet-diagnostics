export const WEBVIEW_FIELD_STYLES = `
    input[type="text"], input[type="search"], select {
        box-sizing:border-box; min-width:0; max-width:100%; font:inherit;
        border-radius:2px; padding:.35rem .5rem;
    }
    input[type="text"], input[type="search"] {
        color:var(--vscode-input-foreground); background:var(--vscode-input-background);
        border:1px solid var(--vscode-input-border, var(--vscode-contrastBorder, transparent));
    }
    input[type="text"]::placeholder, input[type="search"]::placeholder {
        color:var(--vscode-input-placeholderForeground); opacity:1;
    }
    select {
        color:var(--vscode-dropdown-foreground); background:var(--vscode-dropdown-background);
        border:1px solid var(--vscode-dropdown-border, var(--vscode-contrastBorder, transparent));
    }
    select option { color:var(--vscode-dropdown-foreground); background:var(--vscode-dropdown-background); }
    input[type="text"]:disabled, input[type="search"]:disabled, select:disabled, select option:disabled {
        color:var(--vscode-disabledForeground); cursor:not-allowed;
    }
    input[type="text"][aria-invalid="true"], input[type="search"][aria-invalid="true"], select[aria-invalid="true"] {
        color:var(--vscode-inputValidation-errorForeground, var(--vscode-input-foreground));
        background:var(--vscode-inputValidation-errorBackground);
        border-color:var(--vscode-inputValidation-errorBorder);
    }
    input[type="text"]:focus-visible, input[type="search"]:focus-visible, select:focus-visible {
        outline:1px solid var(--vscode-focusBorder); outline-offset:1px;
    }
    .statics-form input[type="text"] { width:20rem; }
    .events-controls input[type="search"] { width:32rem; }
    .evidence-controls label, .cpu-profile-controls label { min-width:0; max-width:100%; }
    .exception-explorer select { max-width:calc(100% - .5rem); }
`;
