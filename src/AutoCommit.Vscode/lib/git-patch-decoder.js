'use strict';

// Decode patch lines independently. For a very long line, stream its remainder
// with the chosen decoder so a multibyte character can cross a window boundary.
class GitPatchDecoder {
  constructor() {
    this.line = Buffer.allocUnsafe(64 * 1024);
    this.length = 0;
    this.continuation = null;
    this.utf8 = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });
  }

  decode(endOfLine) {
    const bytes = this.line.subarray(0, this.length);
    this.length = 0;
    if (this.continuation) {
      const text = this.continuation.decode(bytes, { stream: !endOfLine });
      if (endOfLine) this.continuation = null;
      return text;
    }
    if (endOfLine) {
      try { return this.utf8.decode(bytes); } catch { /* Try the legacy code page. */ }
    } else if (bytes.every(byte => byte < 128)) {
      return bytes.toString('ascii');
    }
    for (const name of endOfLine ? ['gbk'] : ['utf-8', 'gbk']) {
      try {
        const probe = new TextDecoder(name, { fatal: true, ignoreBOM: true });
        const text = probe.decode(bytes, { stream: !endOfLine });
        if (endOfLine) return text;
        // Later bytes in an exceptionally long line may be malformed.
        this.continuation = new TextDecoder(name, { ignoreBOM: true });
        return this.continuation.decode(bytes, { stream: true });
      } catch { /* Try the next encoding. */ }
    }
    if (endOfLine) return bytes.toString('utf8');
    this.continuation = new TextDecoder('utf-8', { ignoreBOM: true });
    return this.continuation.decode(bytes, { stream: true });
  }

  write(chunk) {
    let text = '';
    for (let start = 0; start < chunk.length;) {
      const available = chunk.subarray(start, start + this.line.length - this.length);
      const lf = available.indexOf(10);
      const take = lf < 0 ? available.length : lf + 1;
      available.copy(this.line, this.length, 0, take);
      this.length += take;
      start += take;
      if (lf >= 0 || this.length === this.line.length) text += this.decode(lf >= 0);
    }
    return text;
  }

  end() { return this.decode(true); }
}

module.exports = { GitPatchDecoder };
