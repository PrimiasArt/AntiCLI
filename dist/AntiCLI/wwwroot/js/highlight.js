// Lightweight Syntax Highlighter, Markdown Renderer, and Line Diff Engine

export function escapeHtml(str) {
  return String(str ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

const KEYWORDS = {
  js: /\b(async|await|break|case|catch|class|const|continue|default|delete|do|else|export|extends|false|finally|for|from|function|if|import|in|instanceof|let|new|null|of|return|static|super|switch|this|throw|true|try|typeof|undefined|var|void|while|yield)\b/g,
  ts: /\b(abstract|any|as|async|await|boolean|break|case|catch|class|const|continue|declare|default|do|else|enum|export|extends|false|finally|for|from|function|if|implements|import|in|interface|keyof|let|namespace|never|new|null|number|of|private|protected|public|readonly|return|static|string|super|switch|this|throw|true|try|type|typeof|undefined|unknown|var|void|while)\b/g,
  cs: /\b(abstract|as|async|await|base|bool|break|byte|case|catch|char|checked|class|const|continue|decimal|default|delegate|do|double|else|enum|event|explicit|extern|false|finally|fixed|float|for|foreach|goto|if|implicit|in|int|interface|internal|is|lock|long|namespace|new|null|object|operator|out|override|params|partial|private|protected|public|readonly|record|ref|return|sbyte|sealed|short|sizeof|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|var|virtual|void|volatile|where|while)\b/g,
  py: /\b(and|as|assert|async|await|break|class|continue|def|del|elif|else|except|False|finally|for|from|global|if|import|in|is|lambda|None|nonlocal|not|or|pass|raise|return|True|try|while|with|yield)\b/g,
  sh: /\b(if|then|else|elif|fi|for|do|done|while|in|case|esac|function|return|export|local|echo|cd|ls|rm|mkdir|cp|mv|git|npm|dotnet|agy|powershell|pwsh|Get-ChildItem|Set-Location|Write-Host|Invoke-WebRequest)\b/g,
  sql: /\b(SELECT|FROM|WHERE|JOIN|LEFT|RIGHT|INNER|OUTER|ON|GROUP|BY|ORDER|ASC|DESC|INSERT|INTO|VALUES|UPDATE|SET|DELETE|CREATE|TABLE|ALTER|DROP|INDEX|AS|AND|OR|NOT|NULL|LIMIT|DISTINCT|COUNT|SUM)\b/gi,
  go: /\b(break|case|chan|const|continue|default|defer|else|fallthrough|for|func|go|goto|if|import|interface|map|package|range|return|select|struct|switch|type|var|true|false|nil)\b/g,
  rs: /\b(as|async|await|break|const|continue|crate|dyn|else|enum|extern|false|fn|for|if|impl|in|let|loop|match|mod|move|mut|pub|ref|return|self|Self|static|struct|super|trait|true|type|unsafe|use|where|while)\b/g,
  css: /\b(display|flex|grid|position|color|background|border|padding|margin|width|height|font|overflow|transition|transform|z-index|opacity|box-shadow|gap|align-items|justify-content)\b/g
};

function normalizeLang(lang) {
  const l = (lang || '').toLowerCase().replace(/^\./, '');
  if (['javascript', 'jsx', 'mjs', 'cjs'].includes(l)) return 'js';
  if (['typescript', 'tsx'].includes(l)) return 'ts';
  if (['csharp', 'c#', 'cs'].includes(l)) return 'cs';
  if (['python', 'py'].includes(l)) return 'py';
  if (['bash', 'shell', 'sh', 'zsh', 'ps1', 'powershell', 'pwsh', 'cmd', 'bat'].includes(l)) return 'sh';
  if (['rust', 'rs'].includes(l)) return 'rs';
  if (['json', 'jsonc'].includes(l)) return 'json';
  if (['html', 'xml', 'csproj', 'svg'].includes(l)) return 'xml';
  if (['css', 'scss'].includes(l)) return 'css';
  if (['sql'].includes(l)) return 'sql';
  if (['go', 'golang'].includes(l)) return 'go';
  return l;
}

export function highlightCode(code, lang = '') {
  const norm = normalizeLang(lang);
  const src = String(code ?? '');

  if (norm === 'json') {
    return escapeHtml(src).replace(
      /(&quot;(?:\\.|[^&])*?&quot;)(\s*:)?|\b(true|false|null)\b|\b(-?\d+(?:\.\d+)?)\b/g,
      (m, str, colon, kw, num) => {
        if (str && colon) return `<span class="tok-key">${str}</span>${colon}`;
        if (str) return `<span class="tok-str">${str}</span>`;
        if (kw) return `<span class="tok-kw">${kw}</span>`;
        if (num) return `<span class="tok-num">${num}</span>`;
        return m;
      }
    );
  }

  if (norm === 'xml') {
    return escapeHtml(src)
      .replace(/(&lt;!--[\s\S]*?--&gt;)/g, '<span class="tok-cmt">$1</span>')
      .replace(/(&lt;\/?)([\w:-]+)([\s\S]*?)(\/?&gt;)/g, (_, open, tag, attrs, close) => {
        const hiAttrs = attrs.replace(/([\w:-]+)(=)(&quot;.*?&quot;)/g, '<span class="tok-key">$1</span>$2<span class="tok-str">$3</span>');
        return `${open}<span class="tok-type">${tag}</span>${hiAttrs}${close}`;
      });
  }

  // Tokenize comments & strings first using placeholders so keywords inside them aren't broken
  const slots = [];
  const stash = (html) => {
    const idx = slots.length;
    slots.push(html);
    return `\u0000T${idx}\u0000`;
  };

  let out = escapeHtml(src);

  // Comments
  if (norm === 'py' || norm === 'sh' || norm === 'yaml' || norm === 'yml') {
    out = out.replace(/(#[^\n]*)/g, (m) => stash(`<span class="tok-cmt">${m}</span>`));
  } else {
    out = out.replace(/(\/\*[\s\S]*?\*\/|\/\/[^\n]*)/g, (m) => stash(`<span class="tok-cmt">${m}</span>`));
  }

  // Strings
  out = out.replace(/(&quot;(?:\\.|[^&\\\n]|&(?!quot;))*?&quot;|'(?:\\.|[^'\\\n])*'|`(?:\\.|[^`\\])*`)/g, (m) =>
    stash(`<span class="tok-str">${m}</span>`)
  );

  // Numbers
  out = out.replace(/\b(0x[0-9a-fA-F]+|\d+(?:\.\d+)?)\b/g, '<span class="tok-num">$1</span>');

  // Keywords
  const kwRe = KEYWORDS[norm] || KEYWORDS.js;
  out = out.replace(kwRe, '<span class="tok-kw">$&</span>');

  // Restore stashed tokens
  out = out.replace(/\u0000T(\d+)\u0000/g, (_, idx) => slots[Number(idx)] || '');
  return out;
}

export function renderLineNumberedCode(content, lang = '') {
  const highlighted = highlightCode(content, lang);
  const lines = highlighted.split('\n');
  let rows = '';
  for (let i = 0; i < lines.length; i++) {
    rows += `<tr><td class="ln">${i + 1}</td><td class="lc">${lines[i] || ' '}</td></tr>`;
  }
  return `<table class="code-table"><tbody>${rows}</tbody></table>`;
}

// Markdown Renderer (fenced code blocks, tables, lists, headings, inline formatting)
export function renderMarkdown(mdText) {
  const src = String(mdText ?? '');
  if (!src) return '';

  const blocks = [];
  const stashBlock = (html) => {
    const id = blocks.length;
    blocks.push(html);
    return `\n\u0001B${id}\u0001\n`;
  };

  // Fenced code blocks
  let text = src.replace(/```([\w#+-]*)\r?\n([\s\S]*?)```/g, (_, lang, code) => {
    const cleanCode = code.replace(/\r?\n$/, '');
    const norm = normalizeLang(lang);
    const isShell = norm === 'sh';
    const encoded = encodeURIComponent(cleanCode);
    const runBtn = isShell
      ? `<button class="chip-btn md-run-btn" data-code="${encoded}" title="Chạy lệnh này trong PowerShell Terminal">▶ Run</button>`
      : '';
    return stashBlock(
      `<div class="md-code-block">` +
      `<div class="md-code-header"><span>${escapeHtml(lang || 'text')}</span>` +
      `<div class="md-code-actions">${runBtn}<button class="chip-btn md-copy-btn" data-code="${encoded}">Copy</button></div></div>` +
      `<pre class="md-code-pre"><code>${highlightCode(cleanCode, lang)}</code></pre></div>`
    );
  });

  // Escape HTML outside code blocks
  text = escapeHtml(text);

  // Tables
  text = text.replace(
    /^(\|.+\|)\r?\n(\|[\s:|-]+\|)\r?\n((?:\|.+\|\r?\n?)+)/gm,
    (_, headRow, _sep, bodyRows) => {
      const ths = headRow.split('|').slice(1, -1).map((c) => `<th>${renderInline(c.trim())}</th>`).join('');
      const trs = bodyRows.trim().split(/\r?\n/).map((r) => {
        const tds = r.split('|').slice(1, -1).map((c) => `<td>${renderInline(c.trim())}</td>`).join('');
        return `<tr>${tds}</tr>`;
      }).join('');
      return stashBlock(`<table><thead><tr>${ths}</tr></thead><tbody>${trs}</tbody></table>`);
    }
  );

  // Process line by line for headings, lists, blockquotes, paragraphs
  const lines = text.split(/\r?\n/);
  const htmlParts = [];
  let inUl = false;
  let inOl = false;

  const closeLists = () => {
    if (inUl) { htmlParts.push('</ul>'); inUl = false; }
    if (inOl) { htmlParts.push('</ol>'); inOl = false; }
  };

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const trimmed = line.trim();

    if (/^\u0001B(\d+)\u0001$/.test(trimmed)) {
      closeLists();
      htmlParts.push(trimmed);
      continue;
    }

    if (!trimmed) {
      closeLists();
      continue;
    }

    const hMatch = trimmed.match(/^(#{1,3})\s+(.+)$/);
    if (hMatch) {
      closeLists();
      const lvl = hMatch[1].length;
      htmlParts.push(`<h${lvl}>${renderInline(hMatch[2])}</h${lvl}>`);
      continue;
    }

    if (trimmed.startsWith('&gt; ')) {
      closeLists();
      htmlParts.push(`<blockquote>${renderInline(trimmed.slice(5))}</blockquote>`);
      continue;
    }

    const ulMatch = trimmed.match(/^[-*+]\s+(.+)$/);
    if (ulMatch) {
      if (inOl) { htmlParts.push('</ol>'); inOl = false; }
      if (!inUl) { htmlParts.push('<ul>'); inUl = true; }
      htmlParts.push(`<li>${renderInline(ulMatch[1])}</li>`);
      continue;
    }

    const olMatch = trimmed.match(/^\d+\.\s+(.+)$/);
    if (olMatch) {
      if (inUl) { htmlParts.push('</ul>'); inUl = false; }
      if (!inOl) { htmlParts.push('<ol>'); inOl = true; }
      htmlParts.push(`<li>${renderInline(olMatch[1])}</li>`);
      continue;
    }

    closeLists();
    htmlParts.push(`<p>${renderInline(line)}</p>`);
  }
  closeLists();

  let result = htmlParts.join('\n');
  result = result.replace(/\u0001B(\d+)\u0001/g, (_, idx) => blocks[Number(idx)] || '');
  return result;
}

function renderInline(str) {
  return str
    .replace(/`([^`]+)`/g, '<code class="inline-code">$1</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/\*([^*]+)\*/g, '<em>$1</em>')
    .replace(/~~([^~]+)~~/g, '<del>$1</del>')
    .replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g, '<a href="$2" class="ext-link" data-url="$2">$1</a>');
}

// Fast LCS Line Diff Engine
export function computeLineDiff(oldText, newText) {
  const a = String(oldText ?? '').replace(/\r\n/g, '\n').split('\n');
  const b = String(newText ?? '').replace(/\r\n/g, '\n').split('\n');
  if (oldText === '' && newText === '') return { rows: [], added: 0, removed: 0 };

  // Guard for very huge files to keep UI instant
  if (a.length * b.length > 350000) {
    const rows = [];
    const max = Math.max(a.length, b.length);
    let added = 0, removed = 0;
    for (let i = 0; i < max; i++) {
      if (i < a.length && i < b.length && a[i] === b[i]) {
        rows.push({ type: 'ctx', oldLn: i + 1, newLn: i + 1, text: a[i] });
      } else {
        if (i < a.length) { rows.push({ type: 'del', oldLn: i + 1, newLn: '', text: a[i] }); removed++; }
        if (i < b.length) { rows.push({ type: 'add', oldLn: '', newLn: i + 1, text: b[i] }); added++; }
      }
    }
    return { rows, added, removed };
  }

  const m = a.length, n = b.length;
  const dp = Array.from({ length: m + 1 }, () => new Int32Array(n + 1));
  for (let i = m - 1; i >= 0; i--) {
    for (let j = n - 1; j >= 0; j--) {
      dp[i][j] = a[i] === b[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
    }
  }

  const rows = [];
  let i = 0, j = 0, added = 0, removed = 0;
  while (i < m && j < n) {
    if (a[i] === b[j]) {
      rows.push({ type: 'ctx', oldLn: i + 1, newLn: j + 1, text: a[i] });
      i++; j++;
    } else if (dp[i + 1][j] >= dp[i][j + 1]) {
      rows.push({ type: 'del', oldLn: i + 1, newLn: '', text: a[i] });
      removed++; i++;
    } else {
      rows.push({ type: 'add', oldLn: '', newLn: j + 1, text: b[j] });
      added++; j++;
    }
  }
  while (i < m) { rows.push({ type: 'del', oldLn: i + 1, newLn: '', text: a[i++] }); removed++; }
  while (j < n) { rows.push({ type: 'add', oldLn: '', newLn: j + 1, text: b[j++] }); added++; }

  return { rows, added, removed };
}

export function renderUnifiedDiffHtml(diff, maxLines = 0) {
  const rows = maxLines > 0 ? diff.rows.slice(0, maxLines) : diff.rows;
  let html = '';
  for (const r of rows) {
    const sign = r.type === 'add' ? '+' : r.type === 'del' ? '-' : ' ';
    html += `<div class="diff-line ${r.type}">` +
      `<span class="diff-gutter">${r.oldLn}</span>` +
      `<span class="diff-gutter">${r.newLn}</span>` +
      `<span class="diff-sign">${sign}</span>` +
      `<span class="diff-code">${escapeHtml(r.text)}</span></div>`;
  }
  if (maxLines > 0 && diff.rows.length > maxLines) {
    html += `<div class="diff-line hunk">… còn ${diff.rows.length - maxLines} dòng nữa (mở Diff Inspector để xem đầy đủ)</div>`;
  }
  return html;
}

export function renderSplitDiffHtml(diff) {
  let left = '';
  let right = '';
  for (const r of diff.rows) {
    if (r.type === 'ctx') {
      const esc = escapeHtml(r.text);
      left += `<div class="diff-line ctx"><span class="diff-gutter">${r.oldLn}</span><span class="diff-code">${esc}</span></div>`;
      right += `<div class="diff-line ctx"><span class="diff-gutter">${r.newLn}</span><span class="diff-code">${esc}</span></div>`;
    } else if (r.type === 'del') {
      left += `<div class="diff-line del"><span class="diff-gutter">${r.oldLn}</span><span class="diff-code">${escapeHtml(r.text)}</span></div>`;
      right += `<div class="diff-line ctx"><span class="diff-gutter"></span><span class="diff-code"> </span></div>`;
    } else {
      left += `<div class="diff-line ctx"><span class="diff-gutter"></span><span class="diff-code"> </span></div>`;
      right += `<div class="diff-line add"><span class="diff-gutter">${r.newLn}</span><span class="diff-code">${escapeHtml(r.text)}</span></div>`;
    }
  }
  return `<div class="diff-split"><div class="diff-split-col">${left}</div><div class="diff-split-col">${right}</div></div>`;
}
