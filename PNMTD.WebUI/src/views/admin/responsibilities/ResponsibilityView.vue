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
  personLabel,
  responsibilitiesApi,
  responsibilityKeys,
  standsInLabel,
  type ResponsibilityLink,
} from '@/api/responsibilities'
import ErrorState from '@/components/ErrorState.vue'
import MarkdownView from '@/components/markdown/MarkdownView.vue'
import PageHeader from '@/components/PageHeader.vue'
import TabNav from '@/components/TabNav.vue'
import { useActions } from '@/composables/useActions'
import { useSession } from '@/stores/session'
import LinkPicker, { type LinkTarget } from '@/views/kb/LinkPicker.vue'
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
const form = ref({ name: '', summary: '', responsibleId: null as string | null, deputyId: null as string | null })
const errors = ref<Record<string, string | undefined>>({})
watch(
  r,
  (value) => {
    if (value)
      form.value = {
        name: value.name,
        summary: value.summary ?? '',
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

// --- delete -------------------------------------------------------------------------------------------------------------
async function remove() {
  const value = r.value!
  const links = value.links.length
  const what = links === 0 ? 'It links nothing.' : `${links} ${links === 1 ? 'link is' : 'links are'} removed with it.`
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
        <template #actions>
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
              v-if="manage && r.description.trim()"
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
              <RouterLink v-if="manage" :to="{ name: 'responsibility-edit', params: { id } }">Write one</RouterLink>
            </p>
          </div>
        </section>

        <div class="side">
          <section class="panel" aria-labelledby="details-title">
            <div id="details-title" class="panel-header">Details</div>
            <form v-if="manage" class="panel-body form" novalidate @submit.prevent="save">
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
                    v-if="manage"
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
              <LinkPicker
                v-if="manage"
                class="picker"
                :kinds="['Page', 'Location', 'Host']"
                :exclude="r.links"
                :search="responsibilitiesApi.targets"
                @pick="addLink"
              />
            </div>
          </section>
        </div>
      </div>

      <ResponsibilityHistory v-else :id="id" :manage="manage" />
    </template>
  </div>
</template>

<style scoped>
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
