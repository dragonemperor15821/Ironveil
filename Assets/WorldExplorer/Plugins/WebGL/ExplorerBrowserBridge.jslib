// IRONVEIL World Explorer: minimal browser bridge (Web builds only).
// - URL:       history.replaceState with the canonical query (no history entries).
// - Clipboard: navigator.clipboard.writeText, document.execCommand('copy') as a
//              second attempt; the real outcome is reported back to C#.
// - Random:    crypto.getRandomValues for new world seeds.
// - Fallback:  a native, selectable HTML link field over the canvas.
mergeInto(LibraryManager.library, {

  ExplorerBridge_ReplaceQuery: function (queryPtr) {
    try {
      var query = UTF8ToString(queryPtr);
      window.history.replaceState(window.history.state, '', query);
      return 1;
    } catch (e) {
      return 0;
    }
  },

  ExplorerBridge_GetHref: function () {
    var href = String(window.location.href);
    var size = lengthBytesUTF8(href) + 1;
    var buffer = _malloc(size);
    stringToUTF8(href, buffer, size);
    return buffer;
  },

  // Starts a copy. The outcome is read with ExplorerBridge_CopyStatus:
  // -1 = pending, 1 = Clipboard API, 2 = execCommand, 0 = failed.
  ExplorerBridge_CopyText: function (textPtr) {
    var text = UTF8ToString(textPtr);
    window.__ironveilCopyStatus = -1;
    var report = function (code) { window.__ironveilCopyStatus = code; };
    var legacyCopy = function () {
      var ok = false;
      var previous = document.activeElement;
      var area = document.createElement('textarea');
      try {
        area.value = text;
        area.setAttribute('readonly', '');
        area.style.position = 'fixed';
        area.style.left = '-10000px';
        area.style.top = '0';
        document.body.appendChild(area);
        area.select();
        ok = document.execCommand('copy') === true;
      } catch (e) {
        ok = false;
      }
      if (area.parentNode) area.parentNode.removeChild(area);
      if (previous && previous.focus) { try { previous.focus(); } catch (e) {} }
      return ok;
    };
    try {
      if (navigator.clipboard && navigator.clipboard.writeText && window.isSecureContext) {
        navigator.clipboard.writeText(text).then(
          function () { report(1); },
          function () { report(legacyCopy() ? 2 : 0); });
      } else {
        report(legacyCopy() ? 2 : 0);
      }
    } catch (e) {
      report(0);
    }
  },

  ExplorerBridge_CopyStatus: function () {
    var status = window.__ironveilCopyStatus;
    return (typeof status === 'number') ? status : 0;
  },

  // Returns 32 random bits as a signed int, or 0 when the crypto API is unavailable
  // (0 maps to no valid seed, so C# then uses its own generator).
  ExplorerBridge_RandomBits: function () {
    try {
      var values = new Uint32Array(1);
      window.crypto.getRandomValues(values);
      return values[0] | 0;
    } catch (e) {
      return 0;
    }
  },

  ExplorerBridge_ShowLinkField: function (urlPtr, topPx, leftPx) {
    var url = UTF8ToString(urlPtr);
    var box = document.getElementById('ironveil-share-fallback');
    if (!box) {
      box = document.createElement('div');
      box.id = 'ironveil-share-fallback';
      box.style.cssText = 'position:fixed;z-index:1000;display:flex;gap:8px;align-items:center;' +
        'padding:7px 9px;background:rgba(22,24,27,0.94);border:1px solid rgba(110,120,130,0.6);border-radius:3px;' +
        'font:12px Arial,sans-serif;color:#d9dde1;box-shadow:0 3px 10px rgba(0,0,0,0.45);letter-spacing:0.02em';
      var label = document.createElement('span');
      label.textContent = 'Copy this link';
      label.style.cssText = 'color:#9aa2aa;white-space:nowrap';
      var input = document.createElement('input');
      input.id = 'ironveil-share-fallback-input';
      input.type = 'text';
      input.readOnly = true;
      input.spellcheck = false;
      input.style.cssText = 'width:320px;max-width:42vw;padding:4px 6px;background:#0e1012;color:#eef1f4;' +
        'border:1px solid #66707a;border-radius:2px;font:12px Consolas,monospace';
      var close = document.createElement('button');
      close.type = 'button';
      close.textContent = 'Close';
      close.title = 'Close (Esc)';
      close.style.cssText = 'padding:4px 10px;background:#2b3035;color:#d9dde1;border:1px solid #66707a;' +
        'border-radius:2px;cursor:pointer;font:12px Arial,sans-serif';
      close.onclick = function () { box.style.display = 'none'; };
      // Keep copy shortcuts and selection inside the field instead of the game; Esc closes it.
      var keep = function (e) {
        e.stopPropagation();
        if (e.type === 'keydown' && e.key === 'Escape') box.style.display = 'none';
      };
      ['keydown', 'keyup', 'keypress', 'mousedown', 'mouseup', 'pointerdown', 'pointerup', 'wheel', 'touchstart']
        .forEach(function (type) { box.addEventListener(type, keep); });
      input.addEventListener('focus', function () { input.select(); });
      box.appendChild(label); box.appendChild(input); box.appendChild(close);
      document.body.appendChild(box);
    }
    var canvas = (typeof Module !== 'undefined' && Module.canvas) ? Module.canvas : document.querySelector('canvas');
    var rect = canvas ? canvas.getBoundingClientRect() : { top: 0, left: 0 };
    box.style.top = Math.round(rect.top + topPx) + 'px';
    box.style.left = Math.round(rect.left + leftPx) + 'px';
    box.style.display = 'flex';
    var field = document.getElementById('ironveil-share-fallback-input');
    field.value = url;
    try { field.focus(); field.select(); } catch (e) {}
  },

  ExplorerBridge_HideLinkField: function () {
    var box = document.getElementById('ironveil-share-fallback');
    if (box) box.style.display = 'none';
  }
});
