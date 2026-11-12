import { describe, expect, it } from 'vitest'
import { fileSlug, htmlToMarkdown, mdCode, mdFence, mdTable, mdText } from './markdown'

function md(html: string): string {
  const root = document.createElement('div')
  root.innerHTML = html
  return htmlToMarkdown(root)
}

describe('htmlToMarkdown', () => {
  it('turns headings, paragraphs and inline markup into Markdown', () => {
    expect(
      md(`<section><h2 class="panel-header">1. Exchange model</h2>
        <div class="panel-body"><p>
          <strong>Only the device starts.</strong> PNMTD answers <em>within</em> 100 ms;
          the type is <code>TYPE &amp; 0x8</code>, 2<sup>−32</sup> per attempt, <a href="https://x.test/">a link</a>.
        </p></div></section>`),
    ).toBe(
      '## 1. Exchange model\n\n' +
        '**Only the device starts.** PNMTD answers *within* 100 ms; the type is `TYPE & 0x8`, ' +
        '2<sup>−32</sup> per attempt, [a link](https://x.test/).',
    )
  })

  it('escapes text that would be markup, but not underscores inside words', () => {
    expect(md('<p>OTA_CHUNK and CMD_ID, *not bold*, [not a link], &lt;key&gt;, _x_</p>')).toBe(
      'OTA_CHUNK and CMD_ID, \\*not bold\\*, \\[not a link\\], \\<key>, \\_x\\_',
    )
  })

  it('keeps pre-formatted blocks as fences, with their language', () => {
    expect(md('<pre>node   server\n │ ──► │</pre><pre data-md-lang="c">a = 1;</pre>')).toBe(
      '```\nnode   server\n │ ──► │\n```\n\n```c\na = 1;\n```',
    )
  })

  it('writes lists, nested lists, task lists and definition lists', () => {
    expect(md('<ul><li>One <code>A</code></li><li>Two<ul><li>Inner</li></ul></li></ul>')).toBe(
      '- One `A`\n- Two\n  - Inner',
    )
    expect(md('<ol><li>First</li><li>Second</li></ol>')).toBe('1. First\n2. Second')
    expect(md('<ul data-md-task><li>Persist FCNT</li><li>Verify the tag</li></ul>')).toBe(
      '- [ ] Persist FCNT\n- [ ] Verify the tag',
    )
    expect(
      md('<dl><dt>Endpoint</dt><dd><code>host:5028</code> (UDP)</dd><dt>Largest</dt><dd>508 bytes</dd></dl>'),
    ).toBe('- **Endpoint**: `host:5028` (UDP)\n- **Largest**: 508 bytes')
  })

  it('writes tables with a header row, pipes escaped, a headerless table under its first row', () => {
    expect(
      md(`<div class="table-wrap"><table>
        <thead><tr><th>Field</th><th>Meaning</th></tr></thead>
        <tbody><tr><td><code>TAG</code></td><td>HMAC(K, FCNT32 || P)</td></tr>
        <tr><td>x</td><td><p>one</p><p>two</p></td></tr></tbody></table></div>`),
    ).toBe('| Field | Meaning |\n| --- | --- |\n| `TAG` | HMAC(K, FCNT32 \\|\\| P) |\n| x | one<br>two |')
    expect(md('<table><tr><td>NONE</td><td>No tag</td></tr><tr><td>A</td><td>B</td></tr></table>')).toBe(
      '| NONE | No tag |\n| --- | --- |\n| A | B |',
    )
  })

  it('follows data-md and leaves out buttons, hidden and aria-hidden elements', () => {
    expect(
      md(`<p><code>HmacMd5_32</code><span class="mark" data-md=" (legacy)">legacy</span><span hidden data-md=" · **allowed**"></span>
        <i class="pi pi-copy" aria-hidden="true"></i><button>Copy</button><span hidden>secret</span>
        <span data-md-skip>skipped</span></p>`),
    ).toBe('`HmacMd5_32` (legacy) · **allowed**')
  })
})

describe('Markdown helpers', () => {
  it('builds code spans, fences and tables', () => {
    expect(mdCode('a  `b` c')).toBe('`` a `b` c ``')
    expect(mdCode('  ')).toBe('')
    expect(mdFence('x = "```"\n\n', 'py')).toBe('````py\nx = "```"\n````')
    expect(mdTable(['A', 'B'], [['1'], ['2', 'x|y\nz']])).toBe(
      '| A | B |\n| --- | --- |\n| 1 |   |\n| 2 | x\\|y<br>z |',
    )
    expect(mdText('a*b')).toBe('a\\*b')
  })

  it('makes file names from titles', () => {
    expect(fileSlug('Climate node, Büro Lugano #2')).toBe('climate-node-buro-lugano-2')
    expect(fileSlug('***')).toBe('')
  })
})
