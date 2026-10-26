#!/usr/bin/env node
// Enforces the bilingual documentation rules from AGENTS.md. No dependencies; run from anywhere:
//   node scripts/check-docs.mjs
//
// Pairs checked: docs/pt-br <-> docs/en-us (every .md, recursively) and README.md <-> README-en-us.md.
// For each pair it requires:
//   1. the same files in both trees;
//   2. byte-identical fenced code blocks, in the same order (prompts, Mermaid labels, trees, code comments);
//   3. the same heading levels in the same order;
//   4. the same table shape (rows and columns per table);
// and for every markdown file it requires that relative links and #anchors resolve.
// pt-BR is the source of truth, so a mismatch is reported against the en-US file.

import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, normalize, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const SOURCE = 'docs/pt-br';
const MIRROR = 'docs/en-us';
const READMES = ['README.md', 'README-en-us.md'];
const LINK_ONLY = ['AGENTS.md', '.github/pull_request_template.md'];

const failures = [];
const fail = (file, line, message) => {
  failures.push({ file, line, message });
  const loc = line ? `,line=${line}` : '';
  console.log(`::error file=${file}${loc}::${message}`);
};

const read = (rel) => readFileSync(join(root, rel), 'utf8').replace(/\r\n/g, '\n');

function listMarkdown(dir) {
  const abs = join(root, dir);
  if (!existsSync(abs)) return [];
  const out = [];
  for (const name of readdirSync(abs)) {
    const rel = `${dir}/${name}`;
    if (statSync(join(root, rel)).isDirectory()) out.push(...listMarkdown(rel));
    else if (name.endsWith('.md')) out.push(rel);
  }
  return out.sort();
}

// Splits a markdown file into fenced blocks, headings, table rows and prose lines.
function parse(text) {
  const blocks = [];
  const headings = [];
  const tables = [];
  const prose = [];
  let fence = null;
  let table = null;
  const lines = text.split('\n');
  lines.forEach((line, i) => {
    const n = i + 1;
    if (fence) {
      fence.lines.push(line);
      const close = line.match(/^\s*(`{3,}|~{3,})\s*$/);
      if (close && close[1][0] === fence.char && close[1].length >= fence.len) {
        blocks.push(fence);
        fence = null;
      }
      return;
    }
    const open = line.match(/^\s*(`{3,}|~{3,})/);
    if (open) {
      table = null;
      fence = { line: n, char: open[1][0], len: open[1].length, lines: [line] };
      return;
    }
    const heading = line.match(/^(#{1,6})\s+(.*?)(?:\s+#+)?\s*$/);
    if (heading) {
      table = null;
      headings.push({ line: n, level: heading[1].length, text: heading[2] });
      return;
    }
    if (line.startsWith('|')) {
      const cells = line.replace(/\s+$/, '').split(/(?<!\\)\|/).length - 1;
      if (!table) {
        table = { line: n, rows: [] };
        tables.push(table);
      }
      table.rows.push(cells);
      prose.push({ line: n, text: line });
      return;
    }
    table = null;
    prose.push({ line: n, text: line });
  });
  if (fence) blocks.push(fence);
  return { blocks, headings, tables, prose };
}

// GitHub's heading slug: lower-case, drop markup and punctuation, spaces to hyphens, -N for repeats.
function anchorsOf(headings) {
  const seen = new Map();
  const set = new Set();
  for (const { text } of headings) {
    const plain = text
      .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
      .replace(/[`*_~]/g, (c) => (c === '_' ? '_' : ''));
    const base = plain
      .toLowerCase()
      .replace(/[^\p{L}\p{N}_\- ]/gu, '')
      .replace(/ /g, '-');
    const count = seen.get(base) ?? 0;
    seen.set(base, count + 1);
    set.add(count === 0 ? base : `${base}-${count}`);
  }
  return set;
}

const cache = new Map();
const parsed = (rel) => {
  if (!cache.has(rel)) cache.set(rel, parse(read(rel)));
  return cache.get(rel);
};

function comparePair(src, mirror) {
  const a = parsed(src);
  const b = parsed(mirror);

  if (a.blocks.length !== b.blocks.length) {
    fail(mirror, 0, `has ${b.blocks.length} fenced blocks but ${src} has ${a.blocks.length}`);
  }
  for (let i = 0; i < Math.min(a.blocks.length, b.blocks.length); i++) {
    const x = a.blocks[i].lines;
    const y = b.blocks[i].lines;
    if (x.length === y.length && x.every((l, k) => l === y[k])) continue;
    const k = x.findIndex((l, j) => l !== y[j]);
    const at = k === -1 ? Math.min(x.length, y.length) : k;
    fail(
      mirror,
      b.blocks[i].line + at,
      `fenced block ${i + 1} differs from ${src}:${a.blocks[i].line + at}; code blocks must be identical in both languages (AGENTS.md rule 5)`,
    );
  }

  const levels = (h) => h.map((x) => x.level).join('');
  if (levels(a.headings) !== levels(b.headings)) {
    const k = [...levels(a.headings)].findIndex((c, j) => c !== levels(b.headings)[j]);
    const at = k === -1 ? Math.min(a.headings.length, b.headings.length) : k;
    fail(
      mirror,
      b.headings[at]?.line ?? 0,
      `heading structure differs from ${src} (${a.headings.length} vs ${b.headings.length} headings; first difference at heading ${at + 1})`,
    );
  }

  if (a.tables.length !== b.tables.length) {
    fail(mirror, 0, `has ${b.tables.length} tables but ${src} has ${a.tables.length}`);
  }
  for (let i = 0; i < Math.min(a.tables.length, b.tables.length); i++) {
    const x = a.tables[i].rows;
    const y = b.tables[i].rows;
    if (x.length !== y.length || x.some((c, k) => c !== y[k])) {
      fail(mirror, b.tables[i].line, `table ${i + 1} has a different shape from ${src}:${a.tables[i].line} (rows or columns)`);
    }
  }
}

function checkLinks(rel) {
  const { prose } = parsed(rel);
  const own = anchorsOf(parsed(rel).headings);
  for (const { line, text } of prose) {
    const clean = text.replace(/`[^`]*`/g, '');
    for (const m of clean.matchAll(/\]\(([^)\s]+)\)/g)) {
      const url = m[1];
      if (/^[a-z][a-z0-9+.-]*:/i.test(url)) continue;
      const [file, anchor] = url.split('#');
      const target = file ? normalize(join(dirname(rel), file)) : rel;
      if (!existsSync(join(root, target))) {
        fail(rel, line, `broken link: ${url}`);
        continue;
      }
      if (anchor && target.endsWith('.md')) {
        const anchors = target === rel ? own : anchorsOf(parsed(target).headings);
        if (!anchors.has(decodeURIComponent(anchor))) fail(rel, line, `broken anchor: ${url}`);
      }
    }
  }
}

// 1. File parity and pairwise comparison.
const sourceFiles = listMarkdown(SOURCE).map((f) => relative(SOURCE, f));
const mirrorFiles = listMarkdown(MIRROR).map((f) => relative(MIRROR, f));
const pairs = [];
for (const f of sourceFiles) {
  if (mirrorFiles.includes(f)) pairs.push([`${SOURCE}/${f}`, `${MIRROR}/${f}`]);
  else fail(`${SOURCE}/${f}`, 0, `missing the en-US mirror ${MIRROR}/${f}`);
}
for (const f of mirrorFiles) {
  if (!sourceFiles.includes(f)) fail(`${MIRROR}/${f}`, 0, `missing the pt-BR source ${SOURCE}/${f}`);
}
if (READMES.every((f) => existsSync(join(root, f)))) pairs.push(READMES);
else for (const f of READMES) if (!existsSync(join(root, f))) fail(f, 0, 'root README is missing');

for (const [src, mirror] of pairs) comparePair(src, mirror);

// 2. Links and anchors in every markdown file we own.
const linkFiles = new Set([...pairs.flat(), ...LINK_ONLY.filter((f) => existsSync(join(root, f)))]);
for (const f of linkFiles) checkLinks(f);

if (failures.length > 0) {
  console.error(`\ncheck-docs: ${failures.length} problem(s) found (see AGENTS.md, "Documentation is bilingual").`);
  process.exit(1);
}
console.log(`check-docs: ${pairs.length} document pairs and ${linkFiles.size} files OK.`);
