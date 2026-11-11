<script setup lang="ts">
import { computed } from 'vue'

/** The states of §5.3: SensorState's five, and Degraded for health (uplink down, a task overdue). */
export type Status = 'Ok' | 'Failing' | 'NoData' | 'Ignored' | 'Disabled' | 'Degraded'

const props = withDefaults(
  defineProps<{
    status: Status
    /** Overrides the state's own word ("3 failing"). */
    label?: string
    /** Only the icon, with the word as its accessible name and tooltip. */
    iconOnly?: boolean
  }>(),
  { label: undefined, iconOnly: false },
)

const looks: Record<Status, { icon: string; text: string; tone: string }> = {
  Ok: { icon: 'pi pi-check-circle', text: 'OK', tone: 'ok' },
  Failing: { icon: 'pi pi-times-circle', text: 'Failing', tone: 'fail' },
  NoData: { icon: 'pi pi-circle', text: 'No data', tone: 'none' },
  Ignored: { icon: 'pi pi-eye-slash', text: 'Ignored', tone: 'none' },
  Disabled: { icon: 'pi pi-power-off', text: 'Disabled', tone: 'none' },
  Degraded: { icon: 'pi pi-exclamation-triangle', text: 'Degraded', tone: 'warn' },
}

const look = computed(() => looks[props.status] ?? looks.NoData)
const text = computed(() => props.label ?? look.value.text)
</script>

<template>
  <span
    class="status-badge"
    :class="[`tone-${look.tone}`, { 'icon-only': iconOnly, 'no-data': status === 'NoData' }]"
    :data-status="status"
    :title="iconOnly ? text : undefined"
    :aria-label="iconOnly ? text : undefined"
    :role="iconOnly ? 'img' : undefined"
  >
    <i :class="look.icon" aria-hidden="true" />
    <span v-if="!iconOnly" class="text">{{ text }}</span>
  </span>
</template>

<style scoped>
.status-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.3rem;
  font-weight: 500;
  white-space: nowrap;
  line-height: 1;
}

.status-badge i {
  font-size: 0.95rem;
}

.no-data i {
  /* A dashed circle: never reported (§5.3). */
  border: 1.5px dashed currentColor;
  border-radius: 50%;
  width: 0.9rem;
  height: 0.9rem;
}

.no-data i::before {
  content: none;
}

.tone-ok {
  color: var(--pnmt-ok);
}

.tone-fail {
  color: var(--pnmt-fail);
}

.tone-warn {
  color: var(--pnmt-warn);
}

.tone-none {
  color: var(--pnmt-muted);
}

.tone-none .text {
  font-weight: 400;
}
</style>
