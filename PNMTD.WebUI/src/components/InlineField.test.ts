import { flushPromises } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/client'
import { mountWithUi } from '@/test/mount'
import InlineField from './InlineField.vue'

describe('InlineField', () => {
  it('saves with Enter and shows the new value at once', async () => {
    const save = vi.fn(() => new Promise<void>(() => {}))
    const field = mountWithUi(InlineField, { props: { value: 'web-01', label: 'Name', save } })

    await field.find('button').trigger('click')
    const input = field.find('input')
    await input.setValue('web-02')
    await input.trigger('keydown', { key: 'Enter' })

    expect(save).toHaveBeenCalledWith('web-02')
    expect(field.text()).toContain('web-02')
  })

  it('cancels with Esc', async () => {
    const save = vi.fn(async () => {})
    const field = mountWithUi(InlineField, { props: { value: 'web-01', label: 'Name', save } })

    await field.find('button').trigger('click')
    await field.find('input').setValue('typo')
    await field.find('input').trigger('keydown', { key: 'Escape' })

    expect(save).not.toHaveBeenCalled()
    expect(field.find('input').exists()).toBe(false)
    expect(field.text()).toContain('web-01')
  })

  it('rolls back a failed save and shows the server message at the field', async () => {
    const save = vi.fn(async () => {
      throw new ApiError(400, { code: 'validation_failed', errors: { name: ['1 to 128 characters.'] } })
    })
    const field = mountWithUi(InlineField, { props: { value: 'web-01', label: 'Name', field: 'name', save } })

    await field.find('button').trigger('click')
    await field.find('input').setValue('')
    await field.find('input').trigger('blur')
    await flushPromises()

    expect(save).toHaveBeenCalledWith('')
    expect(field.find('.shown').text()).toContain('web-01')
    expect(field.find('[role="alert"]').text()).toBe('1 to 128 characters.')
  })

  it('does not save an unchanged value, and parses numbers', async () => {
    const save = vi.fn(async () => {})
    const field = mountWithUi(InlineField, { props: { value: 60, label: 'Interval', type: 'number', save } })

    await field.find('button').trigger('click')
    await field.find('input').trigger('keydown', { key: 'Enter' })
    expect(save).not.toHaveBeenCalled()

    await field.find('button').trigger('click')
    await field.find('input').setValue('120')
    await field.find('input').trigger('keydown', { key: 'Enter' })
    expect(save).toHaveBeenCalledWith(120)
  })

  it('is read-only for a role that may not edit', async () => {
    const save = vi.fn(async () => {})
    const field = mountWithUi(InlineField, { props: { value: 'web-01', label: 'Name', save, readonly: true } })
    expect(field.find('button').attributes('disabled')).toBeDefined()
    await field.find('button').trigger('click')
    expect(field.find('input').exists()).toBe(false)
  })
})
