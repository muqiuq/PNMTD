import { describe, expect, it } from 'vitest'
import { mountWithUi } from '@/test/mount'
import RedmineTextView from './RedmineTextView.vue'

describe('RedmineTextView', () => {
  it('keeps plain text as text, with URLs and ticket numbers as links (§6.8)', () => {
    const view = mountWithUi(RedmineTextView, {
      props: {
        text: { format: 'text', text: 'See #12 and https://example.com/a?b=1.\n<b>not html</b> &#34; issue&#35;3' },
      },
    })
    const links = view.findAll('a')
    expect(links.map((a) => a.text())).toEqual(['#12', 'https://example.com/a?b=1'])
    expect(links[1]!.attributes('href')).toBe('https://example.com/a?b=1')
    expect(links[1]!.attributes('rel')).toContain('noopener')
    expect(view.find('b').exists()).toBe(false)
    expect(view.text()).toContain('<b>not html</b>')
  })

  it('renders Markdown through MarkdownView', () => {
    const view = mountWithUi(RedmineTextView, { props: { text: { format: 'markdown', text: '**bold** text' } } })
    expect(view.find('strong').text()).toBe('bold')
  })
})
