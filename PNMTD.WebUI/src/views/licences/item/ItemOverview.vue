<script setup lang="ts">
import Button from 'primevue/button'
import { computed, ref } from 'vue'
import { money, type LicenseDetail } from '@/api/licenses'
import { useNotify } from '@/composables/useNotify'
import { useNow } from '@/composables/useNow'
import VerdictTag from '../VerdictTag.vue'
import DatePanels from './DatePanels.vue'
import { headline, nobodyLine, termBar } from './item'
import KeysPanel from './KeysPanel.vue'
import MoneyPanels from './MoneyPanels.vue'
import NotePanel from './NotePanel.vue'
import NotifyingPanel from './NotifyingPanel.vue'
import OwnerPicker from './OwnerPicker.vue'
import ProductPanels from './ProductPanels.vue'
import TermBar from './TermBar.vue'
import { useItemSave } from './useItemSave'

/**
 * The item's *Overview* (docs/licences-plan.md §8.7): the headline with the days counted live, the term as a bar, the findings, who
 * isn't told (§5.2) with *Set an owner*, the costs in one line linking to the *Costs* tab; then its panels, each row saved where it
 * is shown, read-only without Edit. The keys are shown for licences, and for the others once one is set.
 */
const props = defineProps<{ item: LicenseDetail; edit: boolean; details: boolean }>()
const now = useNow()
const notify = useNotify()
const { save } = useItemSave(() => props.item)

const line = computed(() => headline(props.item, now.value))
const bar = computed(() => (props.item.status === 'Retired' ? null : termBar(props.item, now.value)))
const nobody = computed(() => nobodyLine(props.item.notifying))
const showKeys = computed(() => props.item.type === 'License' || props.item.hasLicenseKey || props.item.hasProductKey)
const costs = computed(() => {
  const i = props.item
  const spent = `spent this year ${money(i.spentThisYear, i.currency)}`
  if (i.billing === 'OneTime')
    return `${i.unitPrice === null ? 'No price' : `${money(i.unitPrice, i.currency)} one-time`} · ${spent}`
  return `${money(i.perMonth, i.currency)} per month · ${money(i.perYear, i.currency)} per year · ${spent}`
})

// Set an owner, in the warning itself.
const choosing = ref(false)
const owner = ref<string | null | undefined>(null)
const saving = ref(false)
async function setOwner() {
  if (!owner.value) return
  saving.value = true
  try {
    await save({ ownerId: owner.value })
    choosing.value = false
    notify.success('Owner set')
  } catch (e) {
    notify.error(e, 'Owner not set')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="overview">
    <section class="panel summary" aria-label="State">
      <div class="headline" data-test="headline">
        <VerdictTag v-if="item.status !== 'Retired'" :code="item.verdictCode" />
        <strong>{{ line }}</strong>
      </div>
      <TermBar v-if="bar" :bar="bar" />
      <ul v-if="item.findings.length" class="findings" data-test="findings">
        <li v-for="f in item.findings" :key="`${f.code}-${f.text}`">
          <VerdictTag :code="f.code" />
          <span>{{ f.text }}</span>
        </li>
      </ul>
      <div v-if="nobody" class="nobody" role="status" data-test="nobody-notified">
        <i class="pi pi-exclamation-triangle" aria-hidden="true" />
        <span>{{ nobody }}</span>
        <template v-if="edit && !item.notifying.hasOwner">
          <form v-if="choosing" class="owner" novalidate @submit.prevent="setOwner">
            <OwnerPicker v-model="owner" input-id="li-set-owner" />
            <Button type="submit" label="Set" size="small" :disabled="!owner" :loading="saving" />
            <Button label="Cancel" size="small" text severity="secondary" @click="choosing = false" />
          </form>
          <Button v-else label="Set an owner" size="small" text data-test="set-owner" @click="choosing = true" />
        </template>
      </div>
      <RouterLink
        :to="{ name: 'licence', params: { number: String(item.number), tab: 'costs' } }"
        class="costs"
        data-test="costs-line"
        >{{ costs }}</RouterLink
      >
    </section>

    <div class="columns">
      <div class="column">
        <ProductPanels :item="item" :readonly="!edit" />
        <KeysPanel v-if="showKeys" :item="item" :details="details" :edit="edit" />
        <NotePanel :item="item" :readonly="!edit" />
      </div>
      <div class="column">
        <MoneyPanels :item="item" :readonly="!edit" />
        <DatePanels :item="item" :readonly="!edit" />
        <NotifyingPanel :item="item" />
      </div>
    </div>
  </div>
</template>

<style scoped>
.overview {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.summary {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  padding: var(--pnmt-space-3);
}

.headline {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
  font-size: 1.05rem;
}

.findings {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: var(--pnmt-space-1);
}

.findings li {
  display: flex;
  align-items: baseline;
  gap: var(--pnmt-space-2);
}

.nobody {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
  color: var(--pnmt-warn);
}

.owner {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  min-width: 16rem;
}

.costs {
  align-self: flex-start;
}

.columns {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--pnmt-space-3);
  align-items: start;
}

.column {
  display: grid;
  gap: var(--pnmt-space-3);
  min-width: 0;
}

@media (max-width: 999px) {
  .columns {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
