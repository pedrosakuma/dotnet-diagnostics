function extendDom(Element) {
    Element.prototype.focus ??= function () { this.focused = true; };
    Element.prototype.scrollIntoView = function () { this.scrolledIntoView = true; };
    Object.defineProperties(Element.prototype, {
        dataset: { get() { return this._dataset ??= {}; } },
        parentElement: { get() { return this._parent ?? null; } },
        nextSibling: { get() {
            const siblings = this.parentElement?.children ?? [];
            return siblings[siblings.indexOf(this) + 1] ?? null;
        } },
        nextElementSibling: { get() { return this.nextSibling; } },
        previousElementSibling: { get() {
            const siblings = this.parentElement?.children ?? [];
            return siblings[siblings.indexOf(this) - 1] ?? null;
        } },
    });
    const original = Element.prototype.appendChild;
    Element.prototype.appendChild = function (child) {
        child.remove();
        child._parent = this;
        return original.call(this, child);
    };
    Element.prototype.remove = function () {
        if (this._parent) {
            const index = this._parent.children.indexOf(this);
            if (index >= 0) this._parent.children.splice(index, 1);
        }
        this._parent = undefined;
    };
    Element.prototype.removeAttribute = function (name) { delete this.attributes[name]; };
    Element.prototype.closest = function (selector) {
        const attribute = /^\[([^\]]+)\]$/.exec(selector)?.[1];
        for (let current = this; current; current = current.parentElement)
            if (attribute && Object.hasOwn(current.attributes, attribute)) return current;
        return null;
    };
    Element.prototype.insertBefore = function (child, next) {
        child.remove();
        child._parent = this;
        const index = this.children.indexOf(next);
        if (index < 0) this.children.push(child); else this.children.splice(index, 0, child);
        return child;
    };
    Element.prototype.querySelectorAll = function (selector) {
        const parts = selector.split(/\s+/);
        const matches = (element, part) => part.startsWith(".")
            ? element.classList.contains(part.slice(1))
            : part.startsWith("[") ? Object.hasOwn(element.attributes, part.slice(1, -1))
            : element.tagName === part;
        const result = [];
        function visit(parent) {
            for (const child of parent.children) {
                if (matches(child, parts.at(-1))) {
                    let ancestor = child.parentElement;
                    let i = parts.length - 2;
                    while (ancestor && i >= 0) {
                        if (matches(ancestor, parts[i])) i--;
                        ancestor = ancestor.parentElement;
                    }
                    if (i < 0) result.push(child);
                }
                visit(child);
            }
        }
        visit(this);
        return result;
    };
}
module.exports = { extendDom };
