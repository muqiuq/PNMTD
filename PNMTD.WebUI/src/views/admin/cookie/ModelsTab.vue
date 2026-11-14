<script setup lang="ts">
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import SelectButton from 'primevue/selectbutton'
import { computed } from 'vue'
import ErrorState from '@/components/ErrorState.vue'
import SectionActions from './SectionActions.vue'
import SettingsHistory from './SettingsHistory.vue'
import { textOrNull, useCookieDefaults, useSection } from './useSection'

/**
 * Models (docs/cookie-plan.md §9.3, §14): Cookie's model and providers, which override the .env defaults when set; the
 * reasoning effort for triage and for working on a case; how long Cookie sleeps; the limits per round; the budget.
 */
const s = useSection('models', {
  prepare: (v) => ({ ...v, model: textOrNull(v.model), providers: textOrNull(v.providers) }),
})
const defaults = useCookieDefaults()
const env = computed(() => defaults.data.value?.env)
const budget = (usd: number) => `${usd.toFixed(2)} USD`
const efforts = ['minimal', 'low', 'medium', 'high'].map((e) => ({ label: e, value: e }))
/** An emptied field is null again, the .env default, as it came. */
function setText(field: 'model' | 'providers', value: string | undefined) {
  if (s.draft.value) s.draft.value[field] = value ? value : null
}
const fields = [
  'model',
  'providers',
  'triageEffort',
  'workEffort',
  'sleepMinutes',
  'maxCasesPerRound',
  'maxToolCalls',
  'budgetUsd',
]
</script>

<template>
  <div class="tab">
    <ErrorState
      v-if="s.settings.isError.value && !s.draft.value"
      :error="s.settings.error.value"
      what="the settings"
      @retry="s.settings.refetch()"
    />
    <template v-else-if="s.draft.value">
      <section class="panel" aria-labelledby="models-title">
        <div id="models-title" class="panel-header">Cookie's model</div>
        <form class="panel-body form" novalidate @submit.prevent="s.save()">
          <p class="muted small intro">Cookie reads these when it runs. An empty field takes its default from .env.</p>
          <div class="form-grid">
            <div class="field">
              <label for="models-model">Model</label>
              <InputText
                id="models-model"
                :model-value="s.draft.value.model ?? ''"
                class="mono"
                :placeholder="env?.model ?? 'Cookie__Model'"
                :invalid="!!s.error('model')"
                @update:model-value="(v) => setText('model', v)"
              />
              <small v-if="s.error('model')" class="field-error">{{ s.error('model') }}</small>
              <small v-else class="field-hint"
                >The model's id at the provider. Empty: from .env (Cookie__Model)<template v-if="env"
                  >, <span class="mono">{{ env.model }}</span></template
                >.</small
              >
            </div>
            <div class="field">
              <label for="models-providers">Providers</label>
              <InputText
                id="models-providers"
                :model-value="s.draft.value.providers ?? ''"
                class="mono"
                :placeholder="env?.providers ?? 'Cookie__Providers'"
                :invalid="!!s.error('providers')"
                @update:model-value="(v) => setText('providers', v)"
              />
              <small v-if="s.error('providers')" class="field-error">{{ s.error('providers') }}</small>
              <small v-else class="field-hint"
                >In the order to try, comma-separated. Empty: from .env (Cookie__Providers)<template v-if="env"
                  >, <span class="mono">{{ env.providers }}</span></template
                >.</small
              >
            </div>
            <div class="field">
              <label id="models-triage">Effort for triage</label>
              <SelectButton
                v-model="s.draft.value.triageEffort"
                :options="efforts"
                option-label="label"
                option-value="value"
                :allow-empty="false"
                size="small"
                aria-labelledby="models-triage"
              />
              <small v-if="s.error('triageEffort')" class="field-error">{{ s.error('triageEffort') }}</small>
              <small v-else class="field-hint">How hard the model thinks when it decides what needs attention.</small>
            </div>
            <div class="field">
              <label id="models-work">Effort for a case</label>
              <SelectButton
                v-model="s.draft.value.workEffort"
                :options="efforts"
                option-label="label"
                option-value="value"
                :allow-empty="false"
                size="small"
                aria-labelledby="models-work"
              />
              <small v-if="s.error('workEffort')" class="field-error">{{ s.error('workEffort') }}</small>
              <small v-else class="field-hint">How hard it thinks when it works on a case.</small>
            </div>
            <div class="field">
              <label for="models-sleep">Sleep between rounds</label>
              <InputNumber
                v-model="s.draft.value.sleepMinutes"
                input-id="models-sleep"
                :min="1"
                :max="120"
                suffix=" min"
                :invalid="!!s.error('sleepMinutes')"
              />
              <small v-if="s.error('sleepMinutes')" class="field-error">{{ s.error('sleepMinutes') }}</small>
              <small v-else class="field-hint">When nothing happens. A bad sensor wakes Cookie at once.</small>
            </div>
            <div class="field">
              <label for="models-cases">Cases per round</label>
              <InputNumber
                v-model="s.draft.value.maxCasesPerRound"
                input-id="models-cases"
                :min="1"
                :max="20"
                :invalid="!!s.error('maxCasesPerRound')"
              />
              <small v-if="s.error('maxCasesPerRound')" class="field-error">{{ s.error('maxCasesPerRound') }}</small>
              <small v-else class="field-hint">At most this many get Cookie's attention in one round.</small>
            </div>
            <div class="field">
              <label for="models-tools">Tool calls per round</label>
              <InputNumber
                v-model="s.draft.value.maxToolCalls"
                input-id="models-tools"
                :min="0"
                :max="60"
                :invalid="!!s.error('maxToolCalls')"
              />
              <small v-if="s.error('maxToolCalls')" class="field-error">{{ s.error('maxToolCalls') }}</small>
              <small v-else class="field-hint">How much Cookie may look up in one round.</small>
            </div>
            <div class="field">
              <label for="models-budget">Budget a month</label>
              <InputNumber
                v-model="s.draft.value.budgetUsd"
                input-id="models-budget"
                mode="currency"
                currency="USD"
                locale="en-US"
                :min="0"
                :max="10000"
                :placeholder="env ? budget(env.budgetUsd) : 'Cookie__BudgetUsd'"
                :invalid="!!s.error('budgetUsd')"
              />
              <small v-if="s.error('budgetUsd')" class="field-error">{{ s.error('budgetUsd') }}</small>
              <small v-else class="field-hint"
                >At the budget, Cookie pauses until the next month. Empty: from .env (Cookie__BudgetUsd)<template
                  v-if="env"
                  >, {{ budget(env.budgetUsd) }}</template
                >.</small
              >
            </div>
          </div>
          <dl v-if="env" class="facts env">
            <dt>From .env only</dt>
            <dd>
              Privacy <span class="mono">{{ env.privacy }}</span> (Cookie__Privacy) · zero data retention
              {{ env.zdr ? 'required' : 'not required' }} (Cookie__Zdr) · quantisation at least
              <span class="mono">{{ env.minQuantization }}</span> (Cookie__MinQuantization)
            </dd>
          </dl>
          <SectionActions
            :busy="s.busy.value"
            :dirty="s.dirty.value"
            :errors="s.errors.value"
            :placed="(f) => fields.includes(f)"
            @save="s.save()"
            @reset="s.reset()"
          />
        </form>
      </section>
      <SettingsHistory section="models" title="the models" @rolled-back="s.reload()" />
    </template>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  max-width: 960px;
}

.intro {
  margin: 0;
}

.env {
  font-size: 0.857rem;
}

.mono :deep(input),
input.mono {
  font-family: var(--pnmt-mono);
}

.small {
  font-size: 0.857rem;
}
</style>
