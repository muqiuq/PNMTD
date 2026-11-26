<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import ProgressSpinner from 'primevue/progressspinner'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { kbLinkIcon, kbLinkRoute } from '@/api/kb'
import {
  combinationLabel,
  escalationLabel,
  personLabel,
  responsibilitiesApi,
  responsibilityKeys,
  skippedPeople,
  standsInLabel,
  type RedmineCombination,
  type ResponsibilityLink,
} from '@/api/responsibilities'
import ColourDot from '@/components/ColourDot.vue'
import ErrorState from '@/components/ErrorState.vue'
import MarkdownView from '@/components/markdown/MarkdownView.vue'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'
import LinkPicker, { type LinkTarget } from '@/views/kb/LinkPicker.vue'
import ColourPicker from './ColourPicker.vue'
import CombinationDialog from './CombinationDialog.vue'
import PersonPicker from './PersonPicker.vue'
import ResponsibilityHistory from './ResponsibilityHistory.vue'

/**
 * A responsibility's page (docs/responsibilities-plan.md §9.3): its description, its details and people, what it links, and
 * its history. With View only, the same without the pickers and buttons.
 */
const props = defineProps<{ id: string }>()
const route = useRoute()
const router = useRouter()
const session = useSession()
const queryClient = useQueryClient()
const { run, done, ask, notify } = useActions()
// Edit changes it; deleting it is Manage's (docs/mcp-extensions-plan.md §8.1).
const edit = computed(() => session.can('Responsibilities', 'Edit'))
const manage = computed(() => session.can('Responsibilities', 'Manage'))

const responsibility = useQuery({
  queryKey: computed(() => responsibilityKeys.one(props.id)),
  queryFn: () => responsibilitiesApi.get(props.id),
})
const r = computed(() => responsibility.data.value)

const tabItem = (key: string, label: string) => ({
  key,
  label,
  to: { name: 'responsibility', params: { id: props.id }, query: key === 'overview' ? {} : { tab: key } },
})
const tabs = [tabItem('overview', 'Overview'), tabItem('history', 'History')]
const tab = computed(() => (route.query.tab === 'history' ? 'history' : 'overview'))

// --- details ------------------------------------------------------------------------------------------------------------
const form = ref({
  name: '',
  summary: '',
  color: null as string | null,
  responsibleId: null as string | null,
  deputyId: null as string | null,
})
const errors = ref<Record<string, string | undefined>>({})
watch(
  r,
  (value) => {
    if (value)
      form.value = {
        name: value.name,
        summary: value.summary ?? '',
        color: value.color ?? null,
        responsibleId: value.responsible?.id ?? null,
        deputyId: value.deputy?.id ?? null,
      }
  },
  { immediate: true },
)
const dirty = computed(() => {
  const value = r.value
  return (
    !!value &&
    (value.name !== form.value.name ||
      (value.summary ?? '') !== form.value.summary ||
      (value.color ?? null) !== form.value.color ||
      (value.responsible?.id ?? null) !== form.value.responsibleId ||
      (value.deputy?.id ?? null) !== form.value.deputyId)
  )
})
const saving = ref(false)
async function save() {
  errors.value = {}
  saving.value = true
  try {
    await responsibilitiesApi.update(props.id, {
      name: form.value.name.trim(),
      summary: form.value.summary.trim() || null,
      color: form.value.color,
      responsibleId: form.value.responsibleId,
      deputyId: form.value.deputyId,
    })
    notify.success(`${form.value.name.trim()} saved`)
    await queryClient.invalidateQueries({ queryKey: responsibilityKeys.all })
  } catch (e) {
    if (e instanceof ApiError && (Object.keys(e.errors).length || e.code === 'duplicate_name')) {
      errors.value = {
        name: e.field('name') ?? (e.code === 'duplicate_name' ? e.message : undefined),
        summary: e.field('summary'),
        color: e.field('color'),
        responsibleId: e.field('responsibleId'),
        deputyId: e.field('deputyId'),
      }
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    saving.value = false
  }
}

// --- links --------------------------------------------------------------------------------------------------------------
const addLink = (target: LinkTarget) =>
  run(() => responsibilitiesApi.link(props.id, target.kind, target.id), {
    success: `${target.name} linked`,
    failure: 'Not linked',
    invalidate: [responsibilityKeys.all],
  })
const removeLink = (link: ResponsibilityLink) =>
  run(() => responsibilitiesApi.unlink(props.id, link.linkId), {
    success: `${link.name} unlinked`,
    failure: 'Not unlinked',
    invalidate: [responsibilityKeys.all],
  })

// --- Redmine combinations (§6) -----------------------------------------------------------------------------------------
const editingCombination = ref(false)
const combination = ref<RedmineCombination | null>(null)
function openCombination(c: RedmineCombination | null) {
  combination.value = c
  editingCombination.value = true
}
async function removeCombination(c: RedmineCombination) {
  if (!(await ask(`Remove ${combinationLabel(c)}?`, `It is removed from ${r.value!.name}.`, 'Remove'))) return
  await run(() => responsibilitiesApi.removeCombination(props.id, c.id), {
    success: `${combinationLabel(c)} removed`,
    failure: 'Not removed',
    invalidate: [responsibilityKeys.all],
  })
}

// --- delete -------------------------------------------------------------------------------------------------------------
const count = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`
async function remove() {
  const value = r.value!
  const parts = [
    value.links.length ? count(value.links.length, 'link', 'links') : null,
    value.redmine.length ? count(value.redmine.length, 'Redmine combination', 'Redmine combinations') : null,
  ].filter(Boolean)
  const what = parts.length === 0 ? 'It links nothing.' : `Removed with it: ${parts.join(' and ')}.`
  if (!(await ask(`Delete ${value.name}?`, `${what} The audit log keeps what it was.`, 'Delete responsibility'))) return
  if (
    await done(() => responsibilitiesApi.remove(props.id), {
      success: `${value.name} deleted`,
      invalidate: [responsibilityKeys.all],
    })
  ) {
    await router.push({ name: 'responsibilities' })
  }
}
</script>

<template>
  <div class="page">
    <ErrorState
      v-if="responsibility.isError.value"
      :error="responsibility.error.value"
      what="the responsibility"
      @retry="responsibility.refetch()"
    />
    <div v-else-if="!r" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <template v-else>
      <PageHeader
        :title="r.name"
        :crumbs="[{ label: 'Admin' }, { label: 'Responsibilities', to: { name: 'responsibilities' } }]"
      >
        <template v-if="r.color" #status><ColourDot :colour="r.color" size="0.9rem" /></template>
        <template #actions>
          <Button
            as="router-link"
            :to="{ name: 'responsibilities', params: { tab: 'overview' }, query: { focus: `responsibility:${id}` } }"
            label="Show on the overview"
            icon="pi pi-sitemap"
            size="small"
            severity="secondary"
            text
            data-test="show-on-overview"
          />
          <Button
            v-if="manage"
            label="Delete…"
            icon="pi pi-trash"
            size="small"
            severity="danger"
            text
            data-test="delete"
            @click="remove"
          />
        </template>
      </PageHeader>
      <p v-if="r.summary" class="summary">{{ r.summary }}</p>
      <TabNav :items="tabs" :active="tab" label="Responsibility" />

      <div v-if="tab === 'overview'" class="columns">
        <section class="panel" aria-labelledby="description-title">
          <div class="panel-header with-action">
            <span id="description-title">Description</span>
            <Button
              v-if="edit && r.description.trim()"
              as="router-link"
              :to="{ name: 'responsibility-edit', params: { id } }"
              label="Edit"
              icon="pi pi-pencil"
              size="small"
              text
              data-test="edit-description"
            />
          </div>
          <div class="panel-body">
            <MarkdownView v-if="r.description.trim()" :source="r.description" :files="r.files" />
            <p v-else class="muted none">
              No description yet.
              <RouterLink v-if="edit" :to="{ name: 'responsibility-edit', params: { id } }">Write one</RouterLink>
            </p>
          </div>
        </section>

        <div class="side">
          <section class="panel" aria-labelledby="details-title">
            <div id="details-title" class="panel-header">Details</div>
            <form v-if="edit" class="panel-body form" novalidate @submit.prevent="save">
              <div class="field">
                <label for="r-name">Name</label>
                <InputText id="r-name" v-model="form.name" :invalid="!!errors.name" />
                <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
              </div>
              <div class="field">
                <label for="r-summary">Short description</label>
                <InputText id="r-summary" v-model="form.summary" :invalid="!!errors.summary" />
                <small v-if="errors.summary" class="field-error">{{ errors.summary }}</small>
              </div>
              <div class="field">
                <label for="r-colour">Colour</label>
                <ColourPicker v-model="form.color" input-id="r-colour" :invalid="!!errors.color" />
                <small v-if="errors.color" class="field-error">{{ errors.color }}</small>
              </div>
              <div class="field">
                <label for="r-responsible">Responsible</label>
                <PersonPicker
                  v-model="form.responsibleId"
                  input-id="r-responsible"
                  :current="r.responsible"
                  :invalid="!!errors.responsibleId"
                />
                <small v-if="errors.responsibleId" class="field-error">{{ errors.responsibleId }}</small>
              </div>
              <div class="field">
                <label for="r-deputy">Deputy</label>
                <PersonPicker
                  v-model="form.deputyId"
                  input-id="r-deputy"
                  :current="r.deputy"
                  :invalid="!!errors.deputyId"
                />
                <small v-if="errors.deputyId" class="field-error">{{ errors.deputyId }}</small>
              </div>
              <div class="form-actions">
                <Button
                  type="submit"
                  label="Save"
                  icon="pi pi-check"
                  size="small"
                  :disabled="!dirty"
                  :loading="saving"
                />
              </div>
            </form>
            <div v-else class="panel-body">
              <dl class="facts">
                <dt>Responsible</dt>
                <dd>{{ personLabel(r.responsible) }}</dd>
                <dt>Deputy</dt>
                <dd>{{ personLabel(r.deputy) }}</dd>
              </dl>
            </div>
            <p v-if="r.globalStandsIn" class="stands-in" data-test="stands-in">{{ standsInLabel(r.global) }}</p>
            <p class="escalation" data-test="escalation-order">
              <span class="muted">Escalation order</span> {{ escalationLabel(r.escalation) }}
              <span v-for="s in skippedPeople(r.responsible, r.deputy, r.global)" :key="s" class="muted">
                · {{ s }}</span
              >
              <br /><span class="muted small">Whom to ask, in this order. Nothing is sent.</span>
            </p>
          </section>

          <section class="panel" aria-labelledby="linked-title">
            <div id="linked-title" class="panel-header">Linked</div>
            <div class="panel-body">
              <p v-if="!r.links.length" class="muted none">Not linked to a page, location or host.</p>
              <ul v-else class="list">
                <li v-for="l in r.links" :key="l.linkId">
                  <i :class="kbLinkIcon[l.kind]" class="muted" aria-hidden="true" />
                  <span class="link-text">
                    <RouterLink v-if="l.exists" :to="kbLinkRoute(l)">{{ l.name }}</RouterLink>
                    <span v-else class="muted">{{ l.name }}</span>
                    <span v-if="l.detail || l.archived" class="muted detail"
                      >{{ l.detail }}{{ l.archived ? ' · archived' : '' }}</span
                    >
                  </span>
                  <Button
                    v-if="edit"
                    icon="pi pi-times"
                    text
                    rounded
                    size="small"
                    severity="secondary"
                    :aria-label="`Unlink ${l.name}`"
                    @click="removeLink(l)"
                  />
                </li>
              </ul>
              <RouterLink
                v-if="r.links.length"
                :to="{ name: 'responsibilities', params: { tab: 'links' }, query: { r: id } }"
                class="all-links"
                data-test="show-in-links"
                >Show in the Links tab</RouterLink
              >
              <LinkPicker
                v-if="edit"
                class="picker"
                :kinds="['Page', 'Location', 'Host']"
                :exclude="r.links"
                :search="responsibilitiesApi.targets"
                @pick="addLink"
              />
            </div>
          </section>

          <section v-if="r.redmineConfigured" class="panel" aria-labelledby="redmine-title">
            <div class="panel-header with-action">
              <span id="redmine-title">Redmine</span>
              <Button
                v-if="edit"
                label="Add…"
                icon="pi pi-plus"
                size="small"
                text
                data-test="add-combination"
                @click="openCombination(null)"
              />
            </div>
            <div class="panel-body">
              <p v-if="!r.redmine.length" class="muted none">No Redmine project or tracker listed.</p>
              <ul v-else class="list">
                <li v-for="c in r.redmine" :key="c.id">
                  <i class="pi pi-ticket muted" aria-hidden="true" />
                  <span class="link-text">
                    <a v-if="edit" href="#" @click.prevent="openCombination(c)">{{ combinationLabel(c) }}</a>
                    <span v-else>{{ combinationLabel(c) }}</span>
                    <span class="muted detail">
                      <span v-if="!c.user" class="warn">Nobody (user removed)</span>
                      <span v-else :class="{ warn: !c.user.active }">{{ personLabel(c.user) }}</span>
                      <template v-if="c.note"> · {{ c.note }}</template>
                    </span>
                    <span v-if="c.warning" class="warn detail"
                      ><i class="pi pi-exclamation-triangle" aria-hidden="true" /> {{ c.warning }}</span
                    >
                  </span>
                  <Button
                    v-if="edit"
                    icon="pi pi-times"
                    text
                    rounded
                    size="small"
                    severity="secondary"
                    :aria-label="`Remove ${combinationLabel(c)}`"
                    @click="removeCombination(c)"
                  />
                </li>
              </ul>
            </div>
          </section>
        </div>
      </div>

      <ResponsibilityHistory v-else :id="id" :edit="edit" />
      <CombinationDialog
        v-model:visible="editingCombination"
        :combination="combination"
        :responsibility-id="id"
        fixed
      />
    </template>
  </div>
</template>

<style scoped>
.warn {
  color: var(--pnmt-warn);
}

.with-action {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.summary {
  margin: 0 0 var(--pnmt-space-3);
  font-size: 1.05rem;
}

.columns {
  display: grid;
  grid-template-columns: minmax(0, 2fr) minmax(18rem, 1fr);
  gap: var(--pnmt-space-4);
  align-items: start;
}

.side {
  display: grid;
  gap: var(--pnmt-space-4);
}

.none {
  margin: 0 0 var(--pnmt-space-2);
}

.stands-in {
  margin: 0;
  padding: 0 var(--pnmt-space-4) var(--pnmt-space-3);
  color: var(--pnmt-warn);
  font-size: 0.857rem;
}

.escalation {
  margin: 0;
  padding: 0 var(--pnmt-space-4) var(--pnmt-space-3);
  font-size: 0.929rem;
}

.escalation .small {
  font-size: 0.786rem;
}

.list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.list li {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.link-text {
  display: flex;
  flex-direction: column;
  min-width: 0;
  flex: 1;
}

.detail {
  font-size: 0.857rem;
  overflow-wrap: anywhere;
}

.picker {
  margin-top: var(--pnmt-space-2);
}

.all-links {
  display: inline-block;
  margin-top: var(--pnmt-space-2);
  font-size: 0.857rem;
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-6, 2rem);
}

@media (max-width: 899px) {
  .columns {
    grid-template-columns: 1fr;
  }
}
</style>
