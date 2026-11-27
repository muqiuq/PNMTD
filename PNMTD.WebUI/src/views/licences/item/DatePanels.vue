<script setup lang="ts">
import Tag from 'primevue/tag'
import { computed, reactive } from 'vue'
import { daysUntil, expiryLabel, type LicenseDetail, type LicenseRenewal } from '@/api/licenses'
import InlineField from '@/components/InlineField.vue'
import { useNow } from '@/composables/useNow'
import { formatDay } from '@/format'
import DayField from '../dialogs/DayField.vue'
import { useItemSave } from './useItemSave'

/**
 * The item's *Term*, *End of life* and *Review* panels (docs/licences-plan.md §3.3, §5.6, §5.7, §8.7): the start, the expiry as the
 * kind words it, the term, the renewal, the warning and notice days with the cancellation deadline; the template's end of life or
 * the item's own; the last review, its rhythm and when it is due. Each row saved where it is shown.
 */
const props = defineProps<{ item: LicenseDetail; readonly: boolean }>()
const { save } = useItemSave(() => props.item)
const now = useNow()
const number = (v: unknown) => (v === null || v === undefined || v === '' ? null : Number(v))
const text = (v: unknown) => (v === null || v === undefined ? null : String(v).trim() || null)
const day = (value: string | null | undefined) => (value ? formatDay(value) : null)

const renewalOptions: { label: string; value: LicenseRenewal }[] = [
  { label: 'None: it ends', value: 'None' },
  { label: 'By hand (Renew…)', value: 'Manual' },
  { label: 'Automatically, unless cancelled', value: 'Automatic' },
]
const automatic = computed(() => props.item.renewal === 'Automatic')
const deadlineDays = computed(() => (props.item.cancelBy ? daysUntil(props.item.cancelBy, now.value) : null))

const drafts = reactive({
  startsOn: null as string | null | undefined,
  expiresOn: null as string | null | undefined,
  endOfLifeOn: null as string | null | undefined,
  reviewedOn: null as string | null | undefined,
})
</script>

<template>
  <section class="panel" aria-labelledby="term-title">
    <div id="term-title" class="panel-header">Term</div>
    <div class="panel-body">
      <dl class="facts">
        <dt>Started on</dt>
        <dd>
          <InlineField
            :value="day(item.startsOn)"
            type="custom"
            label="Started on"
            field="startsOn"
            :readonly="readonly"
            :save="() => save({ startsOn: drafts.startsOn ?? null })"
            @edit="drafts.startsOn = item.startsOn"
          >
            <template #editor><DayField v-model="drafts.startsOn" input-id="li-starts" /></template>
          </InlineField>
        </dd>
        <dt>{{ expiryLabel(item.kind) }}</dt>
        <dd>
          <InlineField
            :value="day(item.expiresOn)"
            type="custom"
            :label="expiryLabel(item.kind)"
            field="expiresOn"
            placeholder="doesn't expire"
            :readonly="readonly"
            :save="() => save({ expiresOn: drafts.expiresOn ?? null })"
            @edit="drafts.expiresOn = item.expiresOn"
          >
            <template #editor><DayField v-model="drafts.expiresOn" input-id="li-expires" clearable /></template>
          </InlineField>
        </dd>
        <dt>Term</dt>
        <dd>
          <InlineField
            :value="item.termMonths"
            type="number"
            label="Term"
            field="termMonths"
            placeholder="none"
            :readonly="readonly"
            :save="(v) => save({ termMonths: number(v) })"
          />
          <small class="field-hint">months</small>
        </dd>
        <dt>Renewal</dt>
        <dd>
          <InlineField
            :value="item.renewal"
            type="enum"
            :options="renewalOptions"
            label="Renewal"
            field="renewal"
            :readonly="readonly"
            :save="(v) => save({ renewal: v as LicenseRenewal })"
          />
        </dd>
        <dt>Warn</dt>
        <dd>
          <InlineField
            :value="item.warnDays"
            type="number"
            label="Warn"
            field="warnDays"
            :readonly="readonly"
            :save="(v) => save({ warnDays: number(v) })"
          />
          <small class="field-hint">{{
            automatic ? 'days before the cancellation deadline' : 'days before it expires (0: once expired)'
          }}</small>
        </dd>
        <template v-if="automatic">
          <dt>Notice</dt>
          <dd>
            <InlineField
              :value="item.noticeDays"
              type="number"
              label="Notice"
              field="noticeDays"
              placeholder="none"
              :readonly="readonly"
              :save="(v) => save({ noticeDays: number(v) })"
            />
            <small class="field-hint">days before the expiry</small>
          </dd>
          <dt>Cancel by</dt>
          <dd data-test="cancel-by">
            <template v-if="item.cancelBy">
              {{ formatDay(item.cancelBy) }}
              <span class="muted" :class="{ late: deadlineDays !== null && deadlineDays < 0 }">{{
                deadlineDays === null
                  ? ''
                  : deadlineDays < 0
                    ? '· passed for this term'
                    : deadlineDays === 0
                      ? '· today'
                      : `· in ${deadlineDays} days`
              }}</span>
            </template>
            <span v-else class="muted">no notice period</span>
          </dd>
          <dt>Warn before it renews</dt>
          <dd>
            <InlineField
              :value="item.warnBeforeRenewal"
              type="bool"
              label="Warn before it renews"
              field="warnBeforeRenewal"
              :readonly="readonly"
              :save="(v) => save({ warnBeforeRenewal: Boolean(v) })"
            />
          </dd>
        </template>
        <template v-if="item.status !== 'Active' && item.statusChangedAt">
          <dt>{{ item.status === 'Cancelled' ? 'Cancelled' : 'Retired' }}</dt>
          <dd>
            {{ formatDay(new Date(item.statusChangedAt))
            }}<template v-if="item.statusChangedBy"> by {{ item.statusChangedBy }}</template>
          </dd>
        </template>
      </dl>
    </div>
  </section>

  <section class="panel" aria-labelledby="eol-title">
    <div id="eol-title" class="panel-header">End of life</div>
    <div class="panel-body">
      <dl class="facts">
        <template v-if="item.templateId">
          <dt>From the template</dt>
          <dd>
            <InlineField
              :value="item.endOfLifeFromTemplate"
              type="bool"
              label="From the template"
              field="endOfLifeFromTemplate"
              :readonly="readonly"
              :save="(v) => save({ endOfLifeFromTemplate: Boolean(v) })"
            />
          </dd>
        </template>
        <template v-if="item.endOfLifeFromTemplate">
          <dt>End of life</dt>
          <dd data-test="eol-from-template">
            <template v-if="item.endOfLifeOn"
              >from the template ({{ formatDay(item.endOfLifeOn)
              }}<template v-if="item.endOfLifeNote">, {{ item.endOfLifeNote }}</template
              >)</template
            >
            <span v-else class="muted">the template sets none</span>
          </dd>
        </template>
        <template v-else>
          <dt>End of life</dt>
          <dd>
            <InlineField
              :value="day(item.ownEndOfLifeOn)"
              type="custom"
              label="End of life"
              field="endOfLifeOn"
              placeholder="none"
              :readonly="readonly"
              :save="() => save({ endOfLifeOn: drafts.endOfLifeOn ?? null })"
              @edit="drafts.endOfLifeOn = item.ownEndOfLifeOn"
            >
              <template #editor><DayField v-model="drafts.endOfLifeOn" input-id="li-eol" clearable /></template>
            </InlineField>
          </dd>
          <dt>What it is</dt>
          <dd>
            <InlineField
              :value="item.ownEndOfLifeNote"
              label="What it is"
              field="endOfLifeNote"
              placeholder="end of extended support"
              :readonly="readonly"
              :save="(v) => save({ endOfLifeNote: text(v) })"
            />
          </dd>
        </template>
        <dt>Warn</dt>
        <dd>
          <InlineField
            :value="item.eolWarnDays"
            type="number"
            label="Warn before the end of life"
            field="eolWarnDays"
            :readonly="readonly"
            :save="(v) => save({ eolWarnDays: number(v) })"
          />
          <small class="field-hint">days before</small>
        </dd>
      </dl>
    </div>
  </section>

  <section class="panel" aria-labelledby="review-title">
    <div id="review-title" class="panel-header">Review</div>
    <div class="panel-body">
      <dl class="facts">
        <dt>Last reviewed</dt>
        <dd>
          <InlineField
            :value="day(item.reviewedOn)"
            type="custom"
            label="Last reviewed"
            field="reviewedOn"
            placeholder="never"
            :readonly="readonly"
            :save="() => save({ reviewedOn: drafts.reviewedOn ?? null })"
            @edit="drafts.reviewedOn = item.reviewedOn"
          >
            <template #editor><DayField v-model="drafts.reviewedOn" input-id="li-reviewed" clearable /></template>
          </InlineField>
          <div v-if="item.reviewedOn && (item.reviewedBy || item.reviewNote)" class="muted small">
            <template v-if="item.reviewedBy">by {{ item.reviewedBy }}</template
            ><template v-if="item.reviewNote">{{ item.reviewedBy ? ': ' : '' }}{{ item.reviewNote }}</template>
          </div>
        </dd>
        <dt>Every</dt>
        <dd>
          <InlineField
            :value="item.reviewMonths"
            type="number"
            label="Review every"
            field="reviewMonths"
            placeholder="no rhythm"
            :readonly="readonly"
            :save="(v) => save({ reviewMonths: number(v) })"
          />
          <small v-if="item.reviewMonths" class="field-hint">months</small>
        </dd>
        <template v-if="item.reviewDueOn">
          <dt>Due on</dt>
          <dd class="inline" data-test="review-due">
            {{ formatDay(item.reviewDueOn) }}
            <Tag v-if="item.reviewDue" value="review due" severity="warn" />
          </dd>
        </template>
      </dl>
    </div>
  </section>
</template>

<style scoped>
.inline {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  flex-wrap: wrap;
}

.late {
  color: var(--pnmt-warn);
}

.small {
  font-size: 0.857rem;
}
</style>
