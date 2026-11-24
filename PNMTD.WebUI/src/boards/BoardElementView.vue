<script setup lang="ts">
import { computed, type Component } from 'vue'
import CertificatesElement from './elements/CertificatesElement.vue'
import ClockElement from './elements/ClockElement.vue'
import FailingSensors from './elements/FailingSensors.vue'
import HostBars from './elements/HostBars.vue'
import ImageElement from './elements/ImageElement.vue'
import LoungeElement from './elements/LoungeElement.vue'
import NoteElement from './elements/NoteElement.vue'
import NumberElement from './elements/NumberElement.vue'
import OnDuty from './elements/OnDuty.vue'
import RecentChanges from './elements/RecentChanges.vue'
import RedmineOverview from './elements/RedmineOverview.vue'
import SensorMessage from './elements/SensorMessage.vue'
import SensorSummary from './elements/SensorSummary.vue'
import StatusTiles from './elements/StatusTiles.vue'
import TicketList from './elements/TicketList.vue'
import UplinkElement from './elements/UplinkElement.vue'
import ZabbixCount from './elements/ZabbixCount.vue'
import ZabbixHistory from './elements/ZabbixHistory.vue'
import { typeNames } from './names'
import type { BoardTime } from './time'
import type { BoardElement, BoardElementData, BoardElementType } from './types'

/**
 * An element on a board (docs/boards-plan.md §7.3): its frame and title, its type's component, and its state: a line for what can't be
 * read or isn't set up, the last data dimmed where there is some, and how old a snapshot's data is.
 */
const props = defineProps<{
  element: BoardElement
  data: BoardElementData | null
  fit: boolean
  time: BoardTime
  now: number
  selectable?: boolean
  selected?: boolean
}>()

const components: Record<BoardElementType, Component> = {
  SensorSummary,
  FailingSensors,
  StatusTiles,
  HostBars,
  RecentChanges,
  Uplink: UplinkElement,
  RedmineOverview,
  RedmineNewest: TicketList,
  RedmineReceived: NumberElement,
  RedmineSolved: NumberElement,
  ZabbixCount,
  ZabbixHistory,
  Lounge: LoungeElement,
  Clock: ClockElement,
  Note: NoteElement,
  OnDuty,
  Certificates: CertificatesElement,
  SensorMessage,
  Image: ImageElement,
}

/** A title of its own, else the type's; a sensor's message is titled by its sensor (docs/board-messages-plan.md §9.3). */
const title = computed(() => {
  if (props.element.title) return props.element.title
  const sensor = props.data?.sensorMessage
  if (props.element.type === 'SensorMessage' && sensor?.host) return `${sensor.host} / ${sensor.name}`
  return typeNames[props.element.type]
})
const state = computed(() => props.data?.state ?? 'Ok')
/** A snapshot older than five minutes says so: "as of 13:58". One that can't be read says that in its message. */
const asOf = computed(() => {
  const at = props.data?.asOf
  return at && props.now - new Date(at).getTime() > 300_000 ? `as of ${props.time.when(at)}` : null
})
const height = computed(() => (props.element.height == null ? 'auto' : String(props.element.height)))
/** A share of the column's height only when the board fits the window; a page that scrolls gives every element its own. */
const style = computed(() =>
  props.element.height == null || !props.fit ? undefined : { flex: `${props.element.height} 1 0` },
)
/** Some data is drawn even when the source can't be read now: the last one, dimmed. */
const drawn = computed(() => props.data && state.value !== 'NotConfigured' && state.value !== 'Withheld')
</script>

<template>
  <section
    class="board-element"
    :data-type="element.type"
    :data-state="state"
    :data-height="height"
    :data-selectable="selectable ? 'true' : undefined"
    :data-selected="selected ? 'true' : undefined"
    :style="style"
    :aria-label="title"
  >
    <header v-if="element.showTitle" class="board-element-title">
      <span class="board-title-text">{{ title }}</span>
      <span v-if="asOf" class="board-asof">{{ asOf }}</span>
    </header>
    <div class="board-element-body">
      <p v-if="data?.message && state !== 'Ok'" class="board-state" :data-state="state">{{ data.message }}</p>
      <p v-if="!data" class="board-state">Loading…</p>
      <component
        :is="components[element.type]"
        v-else-if="drawn"
        :element="element"
        :data="data"
        :fit="fit"
        :time="time"
        :now="now"
      />
    </div>
  </section>
</template>
