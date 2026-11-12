import { flushPromises } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/client'
import { mountWithUi } from '@/test/mount'
import Select from 'primevue/select'
import ToggleSwitch from 'primevue/toggleswitch'
import InlineField from './InlineField.vue'

// jsdom has neither matchMedia, which PrimeVue's Select uses when it mounts, nor ResizeObserver,
// which an auto-resizing Textarea uses.
vi.stubGlobal('matchMedia', (query: string) => ({
  matches: false,
  media: query,
  addEventListener: () => {},
  removeEventListener: () => {},
}))
vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
)

const types = [
  { label: 'Simple', value: 'SIMPLE' },
  { label: 'Heartbeat', value: 'HEARTBEAT' },
]

type Props = InstanceType<typeof InlineField>['$props']

function mountField(props: Omit<Props, 'save'>, save: Props['save'] = vi.fn(async () => {})) {
  return mountWithUi(InlineField, { props: { ...props, save } as Props, attachTo: document.body })
}

describe('InlineField', () => {
  it('saves only with Save, and then shows the new value', async () => {
    const save = vi.fn(async () => {})
    const field = mountField({ value: 'web-01', label: 'Name' }, save)

    await field.find('.shown').trigger('click')
    const input = field.find('input')
    await input.setValue('web-02')
    await input.trigger('blur')
    expect(save).not.toHaveBeenCalled()
    expect(field.find('input').exists()).toBe(true)

    await field.find('[aria-label="Save Name"]').trigger('click')
    await flushPromises()
    expect(save).toHaveBeenCalledWith('web-02')
    expect(field.find('input').exists()).toBe(false)
    expect(field.find('.shown').text()).toBe('web-02')
    field.unmount()
  })

  it('saves with Enter', async () => {
    const save = vi.fn(async () => {})
    const field = mountField({ value: 'web-01', label: 'Name' }, save)

    await field.find('.shown').trigger('click')
    await field.find('input').setValue('web-02')
    await field.find('input').trigger('keydown', { key: 'Enter' })
    await flushPromises()

    expect(save).toHaveBeenCalledWith('web-02')
    expect(field.find('.shown').text()).toBe('web-02')
    field.unmount()
  })

  it('discards with Discard or Esc', async () => {
    const save = vi.fn(async () => {})
    const field = mountField({ value: 'web-01', label: 'Name' }, save)

    await field.find('.shown').trigger('click')
    await field.find('input').setValue('typo')
    await field.find('[aria-label="Discard Name"]').trigger('click')
    await flushPromises()
    expect(field.find('input').exists()).toBe(false)

    await field.find('.shown').trigger('click')
    expect((field.find('input').element as HTMLInputElement).value).toBe('web-01')
    await field.find('input').setValue('typo')
    await field.find('input').trigger('keydown', { key: 'Escape' })
    await flushPromises()

    expect(save).not.toHaveBeenCalled()
    expect(field.find('input').exists()).toBe(false)
    expect(field.find('.shown').text()).toBe('web-01')
    field.unmount()
  })

  it('keeps the editor open with the value and the server message when the save fails', async () => {
    const save = vi.fn(async () => {
      throw new ApiError(400, { code: 'validation_failed', errors: { name: ['1 to 128 characters.'] } })
    })
    const field = mountField({ value: 'web-01', label: 'Name', field: 'name' }, save)

    await field.find('.shown').trigger('click')
    await field.find('input').setValue('')
    await field.find('[aria-label="Save Name"]').trigger('click')
    await flushPromises()

    expect(save).toHaveBeenCalledWith('')
    expect((field.find('input').element as HTMLInputElement).value).toBe('')
    expect(field.find('[role="alert"]').text()).toBe('1 to 128 characters.')
    field.unmount()
  })

  it('shows a message about another field the value needs', async () => {
    const save = vi.fn(async () => {
      throw new ApiError(400, {
        code: 'validation_failed',
        errors: { interval: ['A HEARTBEAT sensor needs an interval.'] },
      })
    })
    const field = mountField({ value: 'SIMPLE', label: 'Type', type: 'enum', options: types }, save)

    await field.find('.shown').trigger('click')
    field.findComponent(Select).vm.$emit('update:modelValue', 'HEARTBEAT')
    await field.find('[aria-label="Save Type"]').trigger('click')
    await flushPromises()

    expect(field.findComponent(Select).exists()).toBe(true)
    expect(field.find('[role="alert"]').text()).toBe('A HEARTBEAT sensor needs an interval.')
    field.unmount()
  })

  it('does not save an unchanged value, and parses numbers', async () => {
    const save = vi.fn(async () => {})
    const field = mountField({ value: 60, label: 'Interval', type: 'number' }, save)

    await field.find('.shown').trigger('click')
    await field.find('[aria-label="Save Interval"]').trigger('click')
    await flushPromises()
    expect(save).not.toHaveBeenCalled()
    expect(field.find('input').exists()).toBe(false)

    await field.find('.shown').trigger('click')
    await field.find('input').setValue('120')
    await field.find('[aria-label="Save Interval"]').trigger('click')
    await flushPromises()
    expect(save).toHaveBeenCalledWith(120)
    field.unmount()
  })

  it('saves a multi-line text with Ctrl+Enter, not with Enter', async () => {
    const save = vi.fn(async () => {})
    const field = mountField({ value: 'line 1', label: 'Notes', type: 'textarea' }, save)

    await field.find('.shown').trigger('click')
    await field.find('textarea').setValue('line 1\nline 2')
    await field.find('textarea').trigger('keydown', { key: 'Enter' })
    expect(save).not.toHaveBeenCalled()
    await field.find('textarea').trigger('keydown', { key: 'Enter', ctrlKey: true })
    await flushPromises()
    expect(save).toHaveBeenCalledWith('line 1\nline 2')
    field.unmount()
  })

  it('shows a choice as text; a pick changes the draft, Save stores it', async () => {
    const save = vi.fn(async () => {})
    const field = mountField({ value: 'SIMPLE', label: 'Type', type: 'enum', options: types }, save)
    expect(field.findComponent(Select).exists()).toBe(false)
    expect(field.find('.shown').text()).toBe('Simple')

    await field.find('.shown').trigger('click')
    await flushPromises()
    field.findComponent(Select).vm.$emit('update:modelValue', 'HEARTBEAT')
    await flushPromises()
    expect(save).not.toHaveBeenCalled()
    expect(field.findComponent(Select).exists()).toBe(true)

    await field.find('[aria-label="Save Type"]').trigger('click')
    await flushPromises()
    expect(save).toHaveBeenCalledWith('HEARTBEAT')
    expect(field.findComponent(Select).exists()).toBe(false)
    expect(field.find('.shown').text()).toBe('Heartbeat')
    field.unmount()
  })

  it('discards a picked choice', async () => {
    const save = vi.fn(async () => {})
    const field = mountField({ value: 'SIMPLE', label: 'Type', type: 'enum', options: types }, save)

    await field.find('.shown').trigger('click')
    field.findComponent(Select).vm.$emit('update:modelValue', 'HEARTBEAT')
    await field.find('[aria-label="Discard Type"]').trigger('click')
    await flushPromises()

    expect(save).not.toHaveBeenCalled()
    expect(field.find('.shown').text()).toBe('Simple')
    field.unmount()
  })

  it('shows a switch as Yes or No; a flip changes the draft, Save stores it', async () => {
    const save = vi.fn(async () => {})
    const field = mountField({ value: true, label: 'Enabled', type: 'bool' }, save)
    expect(field.findComponent(ToggleSwitch).exists()).toBe(false)
    expect(field.find('.shown').text()).toBe('Yes')

    await field.find('.shown').trigger('click')
    await flushPromises()
    const toggle = field.findComponent(ToggleSwitch)
    expect(document.activeElement).toBe(toggle.find('input').element)
    toggle.vm.$emit('update:modelValue', false)
    await toggle.find('input').trigger('blur')
    await flushPromises()
    expect(save).not.toHaveBeenCalled()
    expect(field.findComponent(ToggleSwitch).exists()).toBe(true)

    await field.find('[aria-label="Save Enabled"]').trigger('click')
    await flushPromises()
    expect(save).toHaveBeenCalledWith(false)
    expect(field.find('.shown').text()).toBe('No')
    field.unmount()
  })

  it('discards a flipped switch with Esc', async () => {
    const save = vi.fn(async () => {})
    const field = mountField({ value: false, label: 'Enabled', type: 'bool' }, save)

    await field.find('.shown').trigger('click')
    field.findComponent(ToggleSwitch).vm.$emit('update:modelValue', true)
    await field.findComponent(ToggleSwitch).find('input').trigger('keydown', { key: 'Escape' })
    await flushPromises()

    expect(save).not.toHaveBeenCalled()
    expect(field.findComponent(ToggleSwitch).exists()).toBe(false)
    expect(field.find('.shown').text()).toBe('No')
    field.unmount()
  })

  it('is read-only for a role that may not edit, whatever the type', async () => {
    for (const props of [
      { value: 'web-01', label: 'Name' },
      { value: 'SIMPLE', label: 'Type', type: 'enum' as const, options: types },
      { value: true, label: 'Enabled', type: 'bool' as const },
    ]) {
      const save = vi.fn(async () => {})
      const field = mountField({ ...props, readonly: true }, save)
      expect(field.find('.shown').attributes('disabled')).toBeDefined()
      await field.find('.shown').trigger('click')
      expect(field.find('.edit').exists()).toBe(false)
      field.unmount()
    }
  })
})
