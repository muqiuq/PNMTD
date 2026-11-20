<script setup lang="ts">
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Column from 'primevue/column'
import Dialog from 'primevue/dialog'
import Drawer from 'primevue/drawer'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { api, keys, type S } from '@/api/resources'
import CopyField from '@/components/CopyField.vue'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import RightsTable from '@/components/RightsTable.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import { formatAbsolute } from '@/format'
import { useSession } from '@/stores/session'
import RemoveKeysDialog from './sshkeys/RemoveKeysDialog.vue'
import UserLoungeSwitches from './UserLoungeSwitches.vue'
import UserProfileForm from './UserProfileForm.vue'
import type { RemovalSource } from './sshkeys/sshKeyFormat'
import UserKeys from './sshkeys/UserKeys.vue'

/**
 * Users (§4.9, §5.6): admin or their groups (docs/audit-redmine-zabbix-plan.md §5.5), 2FA, last sign-in, locked or
 * not, their SSH keys. A user's drawer shows their contact details and place in the availability plan (docs/cookie-plan.md
 * §8.1), their effective rights and the group each comes from. A row opens its drawer: edit, reset
 * the password (a temporary one, shown once), reset 2FA, unlock, delete; their keys with where each is authorized, and
 * Remove all from every server… (docs/linux-sensor-plan.md §6.4). Deactivating a user who has keys asks whether to retire
 * them and remove them now. The server refuses to leave no active admin (409 last_admin) and says so.
 */
const session = useSession()
const queryClient = useQueryClient()
const { run, ask, notify, done } = useActions()
const groups = useQuery({ queryKey: keys.groups, queryFn: api.groups.list })
const catalog = useQuery({ queryKey: keys.rights, queryFn: api.rights.catalog, staleTime: Infinity })
const groupOptions = computed(() => (groups.data.value ?? []).map((g) => ({ value: g.id, label: g.name })))
const access = (u: S['UserItem']) => (u.isAdmin ? 'Admin' : u.groups.map((g) => g.name).join(', ') || 'no groups')

const table = useServerTable<S['UserItem']>({
  key: keys.users,
  fetch: (q) => api.users.list(q),
  defaultSort: 'username',
  syncUrl: true,
})

const now = () => Date.now()
const locked = (u: S['UserItem']) => !!u.lockedUntil && Date.parse(u.lockedUntil) > now()

// --- the drawer ---------------------------------------------------------------------------------
const selected = ref<S['UserItem'] | null>(null)
const open = computed({ get: () => selected.value !== null, set: (v) => !v && (selected.value = null) })
const edit = ref<{ displayName: string; isAdmin: boolean; isActive: boolean; groupIds: string[] }>({
  displayName: '',
  isAdmin: false,
  isActive: true,
  groupIds: [],
})
const editErrors = ref<Record<string, string | undefined>>({})
watch(selected, (u) => {
  if (u)
    edit.value = {
      displayName: u.displayName,
      isAdmin: u.isAdmin,
      isActive: u.isActive,
      groupIds: u.groups.map((g) => g.id),
    }
  editErrors.value = {}
})
// The selected user's effective rights, with where each comes from.
const selectedAccess = useQuery({
  queryKey: computed(() => [...keys.users, selected.value?.id, 'access']),
  queryFn: () => api.users.access(selected.value!.id),
  enabled: computed(() => !!selected.value && !selected.value.isAdmin),
})
const effective = computed(() =>
  Object.fromEntries((selectedAccess.data.value?.rights ?? []).map((r) => [r.area, r.level])),
)
const sources = computed(() =>
  Object.fromEntries((selectedAccess.data.value?.rights ?? []).map((r) => [r.area, r.from])),
)
const isSelf = computed(() => selected.value?.id === session.user?.id)

async function refresh(id?: string) {
  await queryClient.invalidateQueries({ queryKey: keys.users })
  if (id) {
    const page = await api.users.list({ pageSize: 500 })
    selected.value = page.items.find((u) => u.id === id) ?? null
  }
}

// Offboarding (docs/linux-sensor-plan.md §6.4): their keys retired and removed from every server.
const removing = ref<RemovalSource | null>(null)
const offboard = (u: S['UserItem']) => (removing.value = { kind: 'user', userId: u.id, name: u.username })

async function saveUser() {
  const u = selected.value!
  const deactivated = u.isActive && !edit.value.isActive
  try {
    await api.users.update(u.id, edit.value)
    const groupsChanged =
      [...edit.value.groupIds].sort().join() !==
      u.groups
        .map((g) => g.id)
        .sort()
        .join()
    notify.success(
      `${u.username} saved`,
      edit.value.isAdmin !== u.isAdmin || edit.value.isActive !== u.isActive
        ? 'Their sessions end.'
        : groupsChanged
          ? 'Their rights change from their next request.'
          : undefined,
    )
    await refresh(u.id)
    if (
      deactivated &&
      u.sshKeys > 0 &&
      (await ask(
        `Retire ${u.username}'s SSH keys?`,
        `They have ${u.sshKeys} registered ${u.sshKeys === 1 ? 'key' : 'keys'}. Retire them and remove them from every server now? The next step shows each place first.`,
        'Retire and remove…',
      ))
    )
      offboard(u)
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      editErrors.value = { displayName: e.field('displayName'), groupIds: e.field('groupIds') }
    } else {
      notify.error(e, 'Not saved')
    }
  }
}

// A temporary password is shown once, in a dialog of its own.
const shown = ref<{ username: string; password: string; expiresAt: string } | null>(null)
const shownOpen = computed({ get: () => shown.value !== null, set: (v) => !v && (shown.value = null) })

async function resetPassword() {
  const u = selected.value!
  if (
    !(await ask(
      `Reset the password of ${u.username}?`,
      'They get a temporary password (shown once), their sessions end and a lockout is lifted.',
      'Reset password',
    ))
  )
    return
  const result = await run(() => api.users.resetPassword(u.id), { failure: 'Not reset', invalidate: [keys.users] })
  if (result) {
    shown.value = { username: u.username, password: result.password, expiresAt: result.expiresAt }
    await refresh(u.id)
  }
}

async function resetTotp() {
  const u = selected.value!
  if (
    !(await ask(
      `Turn off 2FA for ${u.username}?`,
      'For a lost authenticator: their sessions end, and the next sign-in offers the setup again.',
      'Turn off 2FA',
    ))
  )
    return
  if (
    await done(() => api.users.resetTotp(u.id), {
      success: `2FA of ${u.username} turned off`,
      invalidate: [keys.users],
    })
  )
    await refresh(u.id)
}

async function unlock() {
  const u = selected.value!
  if (await done(() => api.users.unlock(u.id), { success: `${u.username} unlocked`, invalidate: [keys.users] }))
    await refresh(u.id)
}

async function remove() {
  const u = selected.value!
  if (!(await ask(`Delete ${u.username}?`, 'They can no longer sign in. Their entries in the auth log stay.'))) return
  if (await done(() => api.users.remove(u.id), { success: `${u.username} deleted`, invalidate: [keys.users] }))
    selected.value = null
}

// --- a new user ---------------------------------------------------------------------------------
const creating = ref(false)
const draft = ref<{ username: string; displayName: string; isAdmin: boolean; groupIds: string[] }>({
  username: '',
  displayName: '',
  isAdmin: false,
  groupIds: [],
})
const createErrors = ref<Record<string, string | undefined>>({})
async function create() {
  createErrors.value = { username: draft.value.username.trim() ? undefined : 'A username is needed.' }
  if (createErrors.value.username) return
  try {
    const result = await api.users.create({
      username: draft.value.username.trim(),
      displayName: draft.value.displayName.trim() || null,
      isAdmin: draft.value.isAdmin,
      groupIds: draft.value.groupIds,
    })
    creating.value = false
    draft.value = { username: '', displayName: '', isAdmin: false, groupIds: [] }
    shown.value = {
      username: result.user.username,
      password: result.temporaryPassword.password,
      expiresAt: result.temporaryPassword.expiresAt,
    }
    await queryClient.invalidateQueries({ queryKey: keys.users })
  } catch (e) {
    if (e instanceof ApiError && (Object.keys(e.errors).length || e.code === 'duplicate_key')) {
      createErrors.value = {
        username: e.field('username') ?? (e.field('groupIds') ? undefined : e.message),
        displayName: e.field('displayName'),
        groupIds: e.field('groupIds'),
      }
    } else {
      notify.error(e, 'Not created')
    }
  }
}
</script>

<template>
  <div class="page">
    <PageHeader title="Users">
      <template #actions>
        <Button label="New user" icon="pi pi-user-plus" size="small" @click="creating = true" />
      </template>
    </PageHeader>
    <FilterBar v-model:search="table.search.value" placeholder="Search username or name" />
    <ListTable
      :table="table"
      what="users"
      selectable
      :row-class="(u) => (u.isActive ? undefined : 'row-muted')"
      @select="(u) => (selected = u)"
    >
      <Column field="username" header="User" sortable>
        <template #body="{ data }">
          <span class="user">
            <span class="mono strong">{{ data.username }}</span>
            <span class="muted">{{ data.displayName }}</span>
          </span>
        </template>
      </Column>
      <Column field="admin" header="Access" sortable style="width: 14rem">
        <template #body="{ data }">
          <Tag v-if="data.isAdmin" value="Admin" severity="info" />
          <span v-else-if="data.groups.length" class="groups">{{ access(data) }}</span>
          <span v-else class="muted">no groups</span>
        </template>
      </Column>
      <Column header="2FA" style="width: 6rem">
        <template #body="{ data }">
          <span v-if="data.isTotpEnabled" class="on"><i class="pi pi-shield" /> on</span>
          <span v-else class="off">off</span>
        </template>
      </Column>
      <Column header="State" style="width: 12rem">
        <template #body="{ data }">
          <Tag v-if="!data.isActive" value="inactive" severity="secondary" />
          <Tag v-else-if="locked(data)" value="locked" severity="danger" />
          <Tag v-else-if="data.mustChangePassword" value="password to change" severity="warn" />
          <span v-else class="muted">active</span>
        </template>
      </Column>
      <Column header="SSH keys" style="width: 6rem">
        <template #body="{ data }">
          <span v-if="data.sshKeys">{{ data.sshKeys }}</span>
          <span v-else class="muted">–</span>
        </template>
      </Column>
      <Column field="lastlogin" header="Last sign-in" sortable style="width: 9rem">
        <template #body="{ data }"><RelativeTime :value="data.lastLoginAt" /></template>
      </Column>
      <!-- The last request of the web UI (docs/mcp-plan.md §3.4); the read-only MCP token stays on until a week after it. -->
      <Column header="Last online" style="width: 9rem">
        <template #body="{ data }"><RelativeTime :value="data.lastSeenAt" /></template>
      </Column>
      <template #mobile="{ item }">
        <div class="mobile-line">
          <span class="mono strong">{{ item.username }}</span>
          <Tag :value="access(item)" :severity="item.isAdmin ? 'info' : 'secondary'" />
          <i v-if="item.isTotpEnabled" class="pi pi-shield on" aria-label="2FA on" />
          <RelativeTime :value="item.lastLoginAt" class="muted right" />
        </div>
      </template>
    </ListTable>

    <Drawer
      v-model:visible="open"
      position="right"
      :header="selected?.username"
      :style="{ width: 'min(30rem, 100vw)' }"
    >
      <template v-if="selected">
        <dl class="facts">
          <dt>Created</dt>
          <dd>{{ formatAbsolute(selected.createdAt) }}</dd>
          <dt>Last sign-in</dt>
          <dd><RelativeTime :value="selected.lastLoginAt" /></dd>
          <dt>Last online</dt>
          <dd><RelativeTime :value="selected.lastSeenAt" /></dd>
          <dt>Password</dt>
          <dd>
            changed <RelativeTime :value="selected.passwordChangedAt" />
            <template v-if="selected.temporaryPasswordExpiresAt">
              · temporary until {{ formatAbsolute(selected.temporaryPasswordExpiresAt) }}</template
            >
          </dd>
          <dt>2FA</dt>
          <dd>{{ selected.isTotpEnabled ? 'on' : 'off' }}</dd>
          <dt>Failed sign-ins</dt>
          <dd>
            {{ selected.failedLoginCount }}
            <span v-if="locked(selected)" class="fail"> · locked until {{ formatAbsolute(selected.lockedUntil) }}</span>
          </dd>
        </dl>
        <form class="form edit" novalidate @submit.prevent="saveUser">
          <div class="field">
            <label for="u-name">Name</label>
            <InputText id="u-name" v-model="edit.displayName" :invalid="!!editErrors.displayName" />
            <small v-if="editErrors.displayName" class="field-error">{{ editErrors.displayName }}</small>
          </div>
          <div class="switch-row">
            <ToggleSwitch v-model="edit.isAdmin" input-id="u-admin" />
            <label for="u-admin">Admin: every right, and users and groups</label>
          </div>
          <div class="field">
            <label for="u-groups">Groups</label>
            <MultiSelect
              v-model="edit.groupIds"
              input-id="u-groups"
              :options="groupOptions"
              option-label="label"
              option-value="value"
              placeholder="No groups"
              display="chip"
              :invalid="!!editErrors.groupIds"
            />
            <small v-if="editErrors.groupIds" class="field-error">{{ editErrors.groupIds }}</small>
            <small v-else class="field-hint">Their rights are the highest level any of their groups gives.</small>
          </div>
          <div class="switch-row">
            <ToggleSwitch v-model="edit.isActive" input-id="u-active" />
            <label for="u-active">Active (may sign in)</label>
          </div>
          <Message v-if="isSelf" severity="info" :closable="false"
            >This is you: your password and 2FA are under Account and Security.</Message
          >
          <div class="form-actions"><Button type="submit" label="Save" size="small" /></div>
        </form>
        <div class="actions">
          <Button
            v-if="!isSelf"
            label="Reset password…"
            icon="pi pi-key"
            size="small"
            severity="secondary"
            outlined
            @click="resetPassword"
          />
          <Button
            v-if="!isSelf && selected.isTotpEnabled"
            label="Turn off 2FA…"
            icon="pi pi-shield"
            size="small"
            severity="secondary"
            outlined
            @click="resetTotp"
          />
          <Button
            v-if="locked(selected)"
            label="Unlock"
            icon="pi pi-lock-open"
            size="small"
            severity="secondary"
            outlined
            @click="unlock"
          />
          <RouterLink
            :to="{ name: 'audit-log', query: { userId: selected.id, user: selected.username } }"
            class="log-link"
            >Audit log ›</RouterLink
          >
          <Button
            v-if="!isSelf"
            label="Delete…"
            icon="pi pi-trash"
            size="small"
            severity="danger"
            text
            class="delete"
            @click="remove"
          />
        </div>
        <section class="profile" aria-labelledby="profile-title">
          <h3 id="profile-title">Contact and availability plan</h3>
          <UserProfileForm :key="selected.id" :user-id="selected.id" :username="selected.username" />
        </section>
        <section class="profile" aria-labelledby="user-lounge-title">
          <h3 id="user-lounge-title">Lounge</h3>
          <UserLoungeSwitches :key="selected.id" :user-id="selected.id" :username="selected.username" />
        </section>
        <section class="access">
          <h3>Rights</h3>
          <p v-if="selected.isAdmin" class="muted">Every right (admin).</p>
          <RightsTable
            v-else-if="catalog.data.value && selectedAccess.data.value"
            :model-value="effective"
            :catalog="catalog.data.value"
            :sources="sources"
            readonly
          />
        </section>
        <section class="ssh-keys">
          <h3>SSH keys</h3>
          <UserKeys :user-id="selected.id" @offboard="offboard(selected)" />
        </section>
      </template>
    </Drawer>
    <RemoveKeysDialog v-model:source="removing" />

    <Dialog v-model:visible="creating" header="New user" modal :style="{ width: '26rem' }">
      <form id="new-user" class="form" novalidate @submit.prevent="create">
        <div class="field">
          <label for="nu-username">Username</label>
          <InputText
            id="nu-username"
            v-model="draft.username"
            autofocus
            autocapitalize="none"
            :invalid="!!createErrors.username"
          />
          <small v-if="createErrors.username" class="field-error">{{ createErrors.username }}</small>
        </div>
        <div class="field">
          <label for="nu-name">Name</label>
          <InputText id="nu-name" v-model="draft.displayName" :invalid="!!createErrors.displayName" />
        </div>
        <div class="switch-row">
          <ToggleSwitch v-model="draft.isAdmin" input-id="nu-admin" />
          <label for="nu-admin">Admin: every right, and users and groups</label>
        </div>
        <div class="field">
          <label for="nu-groups">Groups</label>
          <MultiSelect
            v-model="draft.groupIds"
            input-id="nu-groups"
            :options="groupOptions"
            option-label="label"
            option-value="value"
            placeholder="No groups"
            display="chip"
            :invalid="!!createErrors.groupIds"
          />
          <small v-if="createErrors.groupIds" class="field-error">{{ createErrors.groupIds }}</small>
          <small v-else-if="!draft.isAdmin && !draft.groupIds.length" class="field-hint"
            >Without a group they can only manage their own account.</small
          >
        </div>
        <small class="field-hint"
          >A temporary password is shown once, after creating; it must be changed at the first sign-in.</small
        >
      </form>
      <template #footer>
        <Button label="Cancel" severity="secondary" text @click="creating = false" />
        <Button type="submit" form="new-user" label="Create user" />
      </template>
    </Dialog>

    <Dialog v-model:visible="shownOpen" header="Temporary password" modal :closable="true" :style="{ width: '28rem' }">
      <template v-if="shown">
        <p class="text">
          The temporary password of <strong>{{ shown.username }}</strong>, shown only now. It works until
          {{ formatAbsolute(shown.expiresAt) }} and must be changed at the first sign-in.
        </p>
        <CopyField :value="shown.password" label="Temporary password" />
      </template>
      <template #footer>
        <Button label="Done" @click="shown = null" />
      </template>
    </Dialog>
  </div>
</template>

<style scoped>
.user {
  display: inline-flex;
  gap: var(--pnmt-space-2);
  align-items: baseline;
}

.strong {
  font-weight: 600;
}

.on {
  color: var(--pnmt-ok);
}

.off {
  color: var(--pnmt-warn);
}

.fail {
  color: var(--pnmt-fail);
}

.edit {
  margin-top: var(--pnmt-space-4);
  padding-top: var(--pnmt-space-4);
  border-top: 1px solid var(--pnmt-border);
}

.switch-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.actions {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2);
  align-items: center;
  margin-top: var(--pnmt-space-4);
  padding-top: var(--pnmt-space-4);
  border-top: 1px solid var(--pnmt-border);
}

.groups {
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
  display: block;
}

.profile {
  margin-top: var(--pnmt-space-4);
  padding-top: var(--pnmt-space-4);
  border-top: 1px solid var(--pnmt-border);
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.profile h3 {
  margin: 0;
}

.access {
  margin-top: var(--pnmt-space-4);
  padding-top: var(--pnmt-space-4);
  border-top: 1px solid var(--pnmt-border);
}

.ssh-keys {
  margin-top: var(--pnmt-space-4);
  padding-top: var(--pnmt-space-4);
  border-top: 1px solid var(--pnmt-border);
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.ssh-keys h3 {
  margin: 0;
}

.log-link {
  margin-left: var(--pnmt-space-2);
}

.delete {
  margin-left: auto;
}

.mobile-line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.right {
  margin-left: auto;
}

.text {
  margin-top: 0;
}
</style>
