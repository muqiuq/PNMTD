<script setup lang="ts">
import Button from 'primevue/button'
import { useConfirm } from 'primevue/useconfirm'
import { onBeforeUnmount, onMounted, useId } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { usePreferences } from '@/stores/preferences'

/**
 * The editing page (docs/locations-knowledge-plan.md §5.9): the whole window is the editor's. Never a
 * dialog or a side panel.
 *
 * - The drawer folds to its rail while the page is open, and comes back as it was: a hold on the
 *   preferences store, not a change of the preference.
 * - The header stays at the top: back (which cancels), the title, the status, the editor's switch and
 *   Preview (slot `mode`), who else is editing (slot `others`), Cancel and Save (Ctrl/⌘ S).
 * - The details are a row under it, folded to one line (`detailsSummary`), opened with a click.
 * - The editor (the default slot) fills the height under them and scrolls inside.
 * - Leaving with unsaved changes asks: Cancel and back, the router, and closing the tab.
 *
 * MarkdownEditor puts itself into it; a view uses MarkdownEditor, not this directly.
 */
const props = withDefaults(
  defineProps<{
    /** Back's label: *KB-42*, the location's name. */
    backLabel?: string
    saving?: boolean
    canSave?: boolean
    /** Unsaved changes: leaving asks first. */
    dirty?: boolean
    /** The folded details row's line (*summary · 2 tags · 1 link · note*); no row without it. */
    detailsSummary?: string
    saveLabel?: string
  }>(),
  { backLabel: 'Back', saving: false, canSave: true, dirty: false, detailsSummary: undefined, saveLabel: 'Save' },
)
const detailsOpen = defineModel<boolean>('detailsOpen', { default: false })
const emit = defineEmits<{
  save: []
  /** Cancel or back, after the person agreed to drop their changes. The view navigates away. */
  cancel: []
  /** The changes are dropped (Cancel, or leaving, confirmed): the editor clears its draft. */
  discard: []
}>()

const preferences = usePreferences()
const confirm = useConfirm()
const detailsId = useId()
let releaseRail: (() => void) | null = null
let leaveAllowed = false

function ask(header: string, message: string, accept: string): Promise<boolean> {
  return new Promise((resolve) =>
    confirm.require({
      header,
      message,
      icon: 'pi pi-exclamation-triangle',
      acceptProps: { label: accept, severity: 'danger' },
      rejectProps: { label: 'Keep editing', severity: 'secondary', text: true },
      accept: () => resolve(true),
      reject: () => resolve(false),
      onHide: () => resolve(false),
    }),
  )
}

async function cancel() {
  if (props.dirty && !(await ask('Discard your changes?', 'What you changed here is not saved.', 'Discard'))) {
    return
  }
  if (props.dirty) {
    emit('discard')
  }
  leaveAllowed = true
  emit('cancel')
}

function save() {
  if (props.canSave && !props.saving) {
    emit('save')
  }
}

onBeforeRouteLeave(async () => {
  if (leaveAllowed || !props.dirty) {
    leaveAllowed = false
    return true
  }
  const leave = await ask('Leave without saving?', 'What you changed here is not saved.', 'Leave')
  if (leave) {
    emit('discard')
  }
  return leave
})

function onKey(event: KeyboardEvent) {
  if ((event.metaKey || event.ctrlKey) && !event.altKey && event.key.toLowerCase() === 's') {
    event.preventDefault()
    save()
  }
}

function onBeforeUnload(event: BeforeUnloadEvent) {
  if (props.dirty) {
    event.preventDefault()
    event.returnValue = ''
  }
}

onMounted(() => {
  releaseRail = preferences.holdRail()
  window.addEventListener('keydown', onKey, true)
  window.addEventListener('beforeunload', onBeforeUnload)
})

onBeforeUnmount(() => {
  releaseRail?.()
  window.removeEventListener('keydown', onKey, true)
  window.removeEventListener('beforeunload', onBeforeUnload)
})

defineExpose({ cancel })
</script>

<template>
  <div class="editing-page">
    <header class="editing-header">
      <div class="start">
        <Button
          class="back"
          icon="pi pi-angle-left"
          :label="backLabel"
          text
          severity="secondary"
          :aria-label="`Back to ${backLabel}, without saving`"
          @click="cancel"
        />
        <div class="title"><slot name="title" /></div>
        <div v-if="$slots.status" class="status"><slot name="status" /></div>
      </div>
      <div class="end">
        <div v-if="$slots.mode" class="mode"><slot name="mode" /></div>
        <div v-if="$slots.others" class="others"><slot name="others" /></div>
        <slot name="actions" />
        <Button label="Cancel" severity="secondary" text @click="cancel" />
        <Button
          v-tooltip.bottom="'Ctrl/⌘ S'"
          :label="saveLabel"
          icon="pi pi-check"
          :loading="saving"
          :disabled="!canSave"
          @click="save"
        />
      </div>
    </header>
    <div v-if="detailsSummary !== undefined" class="details" :class="{ open: detailsOpen }">
      <button
        type="button"
        class="details-toggle"
        :aria-expanded="detailsOpen"
        :aria-controls="detailsId"
        @click="detailsOpen = !detailsOpen"
      >
        <i :class="detailsOpen ? 'pi pi-angle-down' : 'pi pi-angle-right'" aria-hidden="true" />
        <span class="label">Details</span>
        <span v-if="!detailsOpen && detailsSummary" class="summary ellipsis">{{ detailsSummary }}</span>
      </button>
      <div v-show="detailsOpen" :id="detailsId" class="details-body"><slot name="details" /></div>
    </div>
    <div v-if="$slots.notices" class="notices"><slot name="notices" /></div>
    <div class="editing-body"><slot /></div>
  </div>
</template>

<style scoped>
/* The window under the app bar, and no more: the editor scrolls inside (§5.9). */
.editing-page {
  display: flex;
  flex-direction: column;
  height: calc(100dvh - var(--pnmt-header-height));
  min-height: 20rem;
  background: var(--pnmt-panel);
}

.editing-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2) var(--pnmt-space-3);
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  border-bottom: 1px solid var(--pnmt-border);
}

.start,
.end {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.start {
  flex: 1 1 24rem;
}

.end {
  flex: 0 1 auto;
  flex-wrap: wrap;
  justify-content: flex-end;
}

.back {
  flex: 0 0 auto;
}

.title {
  flex: 1 1 auto;
  min-width: 0;
}

.status,
.mode,
.others {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.others {
  color: var(--pnmt-muted);
  font-size: 0.9286rem;
}

.details {
  border-bottom: 1px solid var(--pnmt-border);
}

.details-toggle {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  width: 100%;
  min-width: 0;
  padding: var(--pnmt-space-1) var(--pnmt-space-4);
  border: none;
  background: none;
  color: var(--pnmt-text);
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.details-toggle:hover {
  background: var(--p-content-hover-background);
}

.details-toggle .label {
  font-weight: 500;
}

.details-toggle .summary {
  color: var(--pnmt-muted);
}

.details-body {
  padding: var(--pnmt-space-2) var(--pnmt-space-4) var(--pnmt-space-3);
  max-height: 40vh;
  overflow: auto;
}

.notices {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
  padding: var(--pnmt-space-2) var(--pnmt-space-3) 0;
}

.editing-body {
  flex: 1 1 auto;
  min-height: 0;
  position: relative;
}

@media (max-width: 599px) {
  .editing-header {
    padding: var(--pnmt-space-1) var(--pnmt-space-2);
  }

  .start,
  .end {
    flex-basis: 100%;
  }
}
</style>
