import { mount } from '@vue/test-utils'
import { defineComponent, h } from 'vue'
import { describe, expect, it } from 'vitest'
import { hasMatch, snippetParts } from './snippet'

describe('snippetParts', () => {
  it('splits a snippet at the markers', () => {
    expect(snippetParts('The \uE000printer\uE001 on \uE000floor\uE001 2')).toEqual([
      { text: 'The ', mark: false },
      { text: 'printer', mark: true },
      { text: ' on ', mark: false },
      { text: 'floor', mark: true },
      { text: ' 2', mark: false },
    ])
  })

  it('drops stray markers and keeps an unclosed match marked', () => {
    expect(snippetParts('\uE001a\uE000b')).toEqual([
      { text: 'a', mark: false },
      { text: 'b', mark: true },
    ])
    expect(snippetParts('\uE000\uE001')).toEqual([])
    expect(snippetParts(null)).toEqual([])
  })

  it('knows a snippet without a match', () => {
    expect(hasMatch('plain text')).toBe(false)
    expect(hasMatch('a \uE000b\uE001')).toBe(true)
    expect(hasMatch(undefined)).toBe(false)
  })

  it('renders HTML in a snippet as text, matches as mark', () => {
    const Snippet = defineComponent({
      props: { text: { type: String, required: true } },
      setup: (props) => () =>
        h(
          'span',
          snippetParts(props.text).map((p) => (p.mark ? h('mark', p.text) : p.text)),
        ),
    })
    const wrapper = mount(Snippet, { props: { text: '<img src=x onerror=alert(1)> \uE000<b>bold</b>\uE001' } })
    expect(wrapper.find('img').exists()).toBe(false)
    expect(wrapper.find('b').exists()).toBe(false)
    expect(wrapper.find('mark').text()).toBe('<b>bold</b>')
    expect(wrapper.text()).toContain('<img src=x onerror=alert(1)>')
  })
})
