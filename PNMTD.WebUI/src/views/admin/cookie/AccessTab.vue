<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'
import { useSession } from '@/stores/session'
import { accessVerdictLabel } from '@/views/cookie/access'
import { pickerOptions } from '@/views/locations/locationTree'
import { useLocations } from '@/views/locations/useLocations'
import AccessKnownPanel from './AccessKnownPanel.vue'
import AccessRoutersPanel from './AccessRoutersPanel.vue'
import {
  accessWarnings,
  contactKey,
  contactOf,
  normalizeAccess,
  prepareAccess,
  type KindKey,
  type TargetOption,
  type VerdictKey,
} from './accessAdmin'
import { withUnknown, type Option } from './cookieAdmin'
import SectionActions from './SectionActions.vue'
import SettingsHistory from './SettingsHistory.vue'
import { useAccessRouters, useGroupOptions, useHostOptions, useServerOptions, useUserOptions } from './usePickers'
import { useSection } from './useSection'

/**
 * Access (docs/cookie-access-plan.md §8): Cookie watches who logs in to servers with PNMT's agent, MikroTik routers and
 * PNMT itself. Code compares each new login with the target's history; what is new goes to Cookie as an access case, and
 * the contacts set here are told as its verdict's row says. The section is versioned like every other; the routers'
 * switches and the known access take effect at once. The warnings at the top say what keeps the watch from working.
 */
const s = useSection('access', { normalize: normalizeAccess, prepare: prepareAccess })
const session = useSession()
const state = useQuery({
  queryKey: [...keys.cookie, 'admin', 'access', 'state'],
  queryFn: api.cookie.accessState,
  refetchInterval: 60_000,
})
const warnings = computed(() => accessWarnings(state.data.value, s.draft.value))

// --- the pickers ----------------------------------------------------------------------------------------------------
const users = useUserOptions()
const groups = useGroupOptions()
const hosts = useHostOptions()
const servers = useServerOptions()
const routers = useAccessRouters()
const locations = session.can('Locations', 'View') ? useLocations() : null
const locationOptions = computed<Option[]>(() =>
  withUnknown(
    pickerOptions(locations?.data.value?.items ?? []).map((l) => ({ label: l.label, value: l.value })),
    s.draft.value?.serverLocations ?? [],
    () => 'A removed location',
  ),
)
const hostOptions = (chosen: readonly string[]) => withUnknown(hosts.options.value, chosen, hosts.unknown)
const sensorOptions = computed<Option[]>(() =>
  withUnknown(
    [...servers.options.value, ...routers.options.value].sort((a, b) => a.label.localeCompare(b.label)),
    (s.draft.value?.excluded ?? []).map((e) => e.sensorId),
    () => 'A removed server or router',
  ),
)
const targets = computed<TargetOption[]>(() => [
  { label: 'PNMT', value: 'pnmt', kind: 'Pnmt' },
  ...servers.options.value.map((o) => ({ ...o, kind: 'Server' as const })),
  ...routers.options.value.map((o) => ({ ...o, kind: 'Router' as const })),
])

// --- the kinds (§4.6) -----------------------------------------------------------------------------------------------
const kindGroups: { title: string; kinds: { key: KindKey; label: string }[] }[] = [
  {
    title: 'Servers',
    kinds: [
      { key: 'console', label: 'Console' },
      { key: 'rdp', label: 'RDP' },
      { key: 'ssh', label: 'SSH' },
      { key: 'winRm', label: 'WinRM' },
      { key: 'adminNetwork', label: "Admins' network logons (SMB, RPC, WMI)" },
      { key: 'elevation', label: 'sudo and su: a new target, success after failures' },
      { key: 'explicit', label: 'Explicit credentials' },
      { key: 'batch', label: 'Batch' },
      { key: 'unlock', label: 'Unlock' },
      { key: 'cached', label: 'Cached' },
      { key: 'sftp', label: 'SFTP' },
      { key: 'rdpConnection', label: 'RDP connection without a logon' },
    ],
  },
  {
    title: 'Routers',
    kinds: [
      { key: 'routerWinbox', label: 'WinBox' },
      { key: 'routerWeb', label: 'WebFig' },
      { key: 'routerApi', label: 'API' },
      { key: 'routerSsh', label: 'SSH' },
      { key: 'routerTelnet', label: 'Telnet' },
      { key: 'routerFtp', label: 'FTP' },
      { key: 'routerConsole', label: 'Console' },
    ],
  },
  {
    title: 'PNMT',
    kinds: [
      { key: 'pnmtWeb', label: 'Sign-ins to the web UI' },
      { key: 'pnmtMcp', label: 'MCP: a token switched on, or used from a new network' },
    ],
  },
]

// --- the verdicts' rows (§6.1) --------------------------------------------------------------------------------------
const verdictRows: { key: VerdictKey; text: string }[] = [
  { key: 'unusual', text: 'New, and plausible: someone should know.' },
  { key: 'suspicious', text: "Something doesn't fit, and nothing explains it: someone should look soon." },
  { key: 'alarming', text: "Someone may be in who shouldn't be: someone should look now. Needs a strong signal." },
]
/** A verdict's row: every one is there once the draft is normalised. */
const rule = (key: VerdictKey): S['AccessVerdictRule'] => s.draft.value!.notices[key]!
const hoursOptions: { label: string; value: S['AccessHours'] }[] = [
  { label: 'Wait for working hours', value: 'Wait' },
  { label: 'Contacts who allow it', value: 'Allowed' },
  { label: 'Every contact', value: 'Everyone' },
]

// --- the contacts (§6.2) --------------------------------------------------------------------------------------------
const chosen = computed(() => new Set((s.draft.value?.contacts ?? []).map(contactKey)))
const addable = computed(() =>
  [
    {
      label: 'Users',
      items: users.options.value.map((o) => ({ label: o.label, value: `user:${o.value}` })),
    },
    {
      label: 'Groups',
      items: groups.options.value.map((o) => ({ label: o.label, value: `group:${o.value}` })),
    },
  ]
    .map((g) => ({ ...g, items: g.items.filter((i) => !chosen.value.has(i.value)) }))
    .filter((g) => g.items.length),
)
const newContact = ref<string | null>(null)
function addContact(key: string | null) {
  const contact = key ? contactOf(key) : null
  if (contact && s.draft.value) s.draft.value.contacts.push(contact)
  newContact.value = null
}
function contactName(c: S['AccessContact']): string {
  if (!users.query.data.value) return '…'
  if (c.userId) return users.options.value.find((o) => o.value === c.userId)?.label ?? users.unknown()
  return groups.options.value.find((o) => o.value === c.groupId)?.label ?? groups.unknown()
}

// --- the lists' rows ------------------------------------------------------------------------------------------------
function addExcluded() {
  s.draft.value?.excluded.push({ account: '', sensorId: null, comment: '' })
}
function addTrusted() {
  s.draft.value?.trusted.push({ name: '', cidr: '' })
}
const remove = <T,>(list: T[], i: number) => list.splice(i, 1)

const placedFields = [
  'historyDays',
  'learningDays',
  'wasThisYou.validHours',
  'wasThisYou.perPersonDay',
  'limits.perContactDay',
  'limits.perContactHour',
]
const placed = (f: string) => placedFields.includes(f) || /^(contacts|trusted|excluded)\.\d+/.test(f)
const rowError = (field: string) => s.error(field)
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
      <Message
        v-for="w in warnings"
        :key="w.key"
        :severity="w.severity"
        :closable="false"
        size="small"
        class="warning"
        >{{ w.text }}</Message
      >
      <p v-if="state.data.value" class="muted small counts">
        {{ state.data.value.openCases }} open access {{ state.data.value.openCases === 1 ? 'case' : 'cases' }} ·
        {{ state.data.value.reviewsToday }} {{ state.data.value.reviewsToday === 1 ? 'login' : 'logins' }} reviewed
        today · {{ state.data.value.noticesToday }}
        {{ state.data.value.noticesToday === 1 ? 'notice' : 'notices' }} today
      </p>

      <section class="panel" aria-labelledby="access-watch-title">
        <div id="access-watch-title" class="panel-header">The watch</div>
        <div class="panel-body body">
          <div class="switch">
            <ToggleSwitch v-model="s.draft.value.on" input-id="access-on" />
            <div>
              <label for="access-on" class="label">Cookie watches who logs in</label>
              <div class="muted small">
                Code compares each new login with its target's history; what is new goes to Cookie as an access case,
                and the contacts below are told as its verdict says. Off by default. It needs Cookie__Enabled=true, and
                runs while Cookie is stopped too, for code's notice.
              </div>
            </div>
          </div>
          <div class="switch">
            <ToggleSwitch v-model="s.draft.value.codeNotice" input-id="access-code-notice" />
            <div>
              <label for="access-code-notice" class="label">Code's notice when Cookie can't judge</label>
              <div class="muted small">
                A login with a strong signal still unjudged after 10 minutes (Cookie stopped, paused, over budget,
                failing) gets a notice from a template: the facts and the signals, no reason. Alarming with two strong
                signals or more, otherwise suspicious. Medium signals wait for Cookie up to 24 hours.
              </div>
            </div>
          </div>
        </div>
      </section>

      <section class="panel" aria-labelledby="access-targets-title">
        <div id="access-targets-title" class="panel-header">Targets</div>
        <div class="panel-body body">
          <div class="checks">
            <span class="checkbox">
              <Checkbox v-model="s.draft.value.servers" binary input-id="access-servers" />
              <label for="access-servers">Servers with PNMT's agent</label>
            </span>
            <span class="checkbox">
              <Checkbox v-model="s.draft.value.routers" binary input-id="access-routers" />
              <label for="access-routers">MikroTik routers, each whose switch is on</label>
            </span>
            <span class="checkbox">
              <Checkbox v-model="s.draft.value.pnmt" binary input-id="access-pnmt" />
              <label for="access-pnmt">PNMT: its sign-ins and MCP</label>
            </span>
          </div>
          <div v-if="s.draft.value.servers" class="form-grid">
            <div class="field">
              <label for="access-locations">Only the servers of these locations</label>
              <MultiSelect
                v-if="locations"
                v-model="s.draft.value.serverLocations"
                input-id="access-locations"
                :options="locationOptions"
                option-label="label"
                option-value="value"
                filter
                display="chip"
                size="small"
                placeholder="Every location"
              />
              <small v-else class="field-hint">Choosing locations needs Locations: View.</small>
            </div>
            <div class="field">
              <label for="access-hosts">And these hosts</label>
              <MultiSelect
                v-if="hosts.listed.value"
                v-model="s.draft.value.serverHosts"
                input-id="access-hosts"
                :options="hostOptions(s.draft.value.serverHosts)"
                option-label="label"
                option-value="value"
                filter
                display="chip"
                size="small"
                placeholder="Every host"
              />
              <small class="field-hint">With neither chosen, every server is watched.</small>
            </div>
            <div class="field">
              <label for="access-excluded-hosts">Never these hosts</label>
              <MultiSelect
                v-if="hosts.listed.value"
                v-model="s.draft.value.excludedHosts"
                input-id="access-excluded-hosts"
                :options="hostOptions(s.draft.value.excludedHosts)"
                option-label="label"
                option-value="value"
                filter
                display="chip"
                size="small"
                placeholder="None"
              />
            </div>
          </div>
          <div v-if="s.draft.value.routers" class="switch">
            <ToggleSwitch v-model="s.draft.value.watchNewRouters" input-id="access-new-routers" />
            <div>
              <label for="access-new-routers" class="label">Watch new routers</label>
              <div class="muted small">
                A new router's switch starts on; so do the existing routers' when the router watch is first switched on.
                Each router's switch is below and on its Access tab.
              </div>
            </div>
          </div>
        </div>
      </section>

      <section class="panel" aria-labelledby="access-kinds-title">
        <div id="access-kinds-title" class="panel-header">Watched kinds</div>
        <div class="panel-body body">
          <p class="muted small intro">
            The kinds of login that are compared with the history. Backups and monitoring tools repeat the same pattern,
            so the history keeps them quiet after the learning period.
          </p>
          <div class="kinds">
            <fieldset v-for="g in kindGroups" :key="g.title" class="kind-group">
              <legend class="label">{{ g.title }}</legend>
              <span v-for="k in g.kinds" :key="k.key" class="checkbox">
                <Checkbox v-model="s.draft.value.kinds[k.key]" binary :input-id="`access-kind-${k.key}`" />
                <label :for="`access-kind-${k.key}`">{{ k.label }}</label>
              </span>
            </fieldset>
          </div>
        </div>
      </section>

      <section class="panel" aria-labelledby="access-excluded-title">
        <div id="access-excluded-title" class="panel-header">
          Excluded accounts
          <Button
            label="Add an account"
            icon="pi pi-plus"
            size="small"
            severity="secondary"
            text
            class="end"
            @click="addExcluded"
          />
        </div>
        <div class="panel-body body">
          <p class="muted small intro">
            A backup service account, the monitoring user: on one server or router, or everywhere. Their strong signals
            still count.
          </p>
          <p v-if="!s.draft.value.excluded.length" class="muted none">None.</p>
          <div v-for="(e, i) in s.draft.value.excluded" :key="i" class="row">
            <div class="field">
              <label :for="`access-excluded-${i}`">Account</label>
              <InputText
                :id="`access-excluded-${i}`"
                v-model="e.account"
                size="small"
                class="mono"
                maxlength="520"
                placeholder="CONTOSO\svc-backup"
                :invalid="!!rowError(`excluded.${i}.account`)"
              />
              <small v-if="rowError(`excluded.${i}.account`)" class="field-error">{{
                rowError(`excluded.${i}.account`)
              }}</small>
            </div>
            <div class="field">
              <label :for="`access-excluded-${i}-on`">On</label>
              <Select
                v-model="e.sensorId"
                :input-id="`access-excluded-${i}-on`"
                :options="sensorOptions"
                option-label="label"
                option-value="value"
                filter
                show-clear
                size="small"
                placeholder="Every target"
              />
            </div>
            <div class="field grow">
              <label :for="`access-excluded-${i}-comment`">Comment</label>
              <InputText
                :id="`access-excluded-${i}-comment`"
                v-model="e.comment"
                size="small"
                maxlength="300"
                :invalid="!!rowError(`excluded.${i}.comment`)"
              />
              <small v-if="rowError(`excluded.${i}.comment`)" class="field-error">{{
                rowError(`excluded.${i}.comment`)
              }}</small>
            </div>
            <Button
              icon="pi pi-trash"
              size="small"
              severity="secondary"
              text
              class="remove"
              :aria-label="`Remove ${e.account || 'the account'}`"
              @click="remove(s.draft.value.excluded, i)"
            />
          </div>
        </div>
      </section>

      <section class="panel" aria-labelledby="access-links-title">
        <div id="access-links-title" class="panel-header">Whose account it is</div>
        <div class="panel-body body">
          <p class="muted small intro">
            How a server's or router's account is linked to a PNMT user, in this order. A shared account (Administrator,
            root, admin) has no person unless the key says who it was. PNMT's sign-ins need no link.
          </p>
          <span class="checkbox">
            <Checkbox v-model="s.draft.value.links.key" binary input-id="access-link-key" />
            <label for="access-link-key">The SSH key: a key login's key owner (servers only)</label>
          </span>
          <span class="checkbox">
            <Checkbox v-model="s.draft.value.links.serverAccounts" binary input-id="access-link-accounts" />
            <label for="access-link-accounts"
              >The user's server accounts, set on Admin → Users (without case, with or without the domain)</label
            >
          </span>
          <span class="checkbox">
            <Checkbox v-model="s.draft.value.links.username" binary input-id="access-link-username" />
            <label for="access-link-username">The PNMT username, when it equals the account without its domain</label>
          </span>
        </div>
      </section>

      <section class="panel" aria-labelledby="access-notices-title">
        <div id="access-notices-title" class="panel-header">Notices</div>
        <div class="panel-body body">
          <p class="muted small intro">
            How suspicious a login looks decides how loud its notice is. Notify off: the verdict's cases are still
            opened, judged and shown, nobody is told. Was this you? doesn't depend on Notify. For one level only, set
            the three rows alike. Working hours are the availability plan's.
          </p>
          <div class="wide-table">
            <table class="rows">
              <thead>
                <tr>
                  <th scope="col">Verdict</th>
                  <th scope="col">Notify</th>
                  <th scope="col">E-mail</th>
                  <th scope="col">Pushover</th>
                  <th scope="col">Outside working hours</th>
                  <th scope="col">Was this you?</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="v in verdictRows" :key="v.key">
                  <td>
                    <div class="label">{{ accessVerdictLabel(v.key) }}</div>
                    <div class="muted small">{{ v.text }}</div>
                  </td>
                  <td>
                    <ToggleSwitch
                      :model-value="rule(v.key).notify"
                      :aria-label="`${accessVerdictLabel(v.key)}: notify`"
                      @update:model-value="(x: boolean) => (rule(v.key).notify = x)"
                    />
                  </td>
                  <td>
                    <Checkbox
                      :model-value="rule(v.key).email"
                      binary
                      :disabled="!rule(v.key).notify"
                      :aria-label="`${accessVerdictLabel(v.key)}: e-mail`"
                      @update:model-value="(x: boolean) => (rule(v.key).email = x)"
                    />
                  </td>
                  <td>
                    <Checkbox
                      :model-value="rule(v.key).pushover"
                      binary
                      :disabled="!rule(v.key).notify"
                      :aria-label="`${accessVerdictLabel(v.key)}: Pushover`"
                      @update:model-value="(x: boolean) => (rule(v.key).pushover = x)"
                    />
                  </td>
                  <td>
                    <Select
                      :model-value="rule(v.key).hours"
                      :options="hoursOptions"
                      option-label="label"
                      option-value="value"
                      size="small"
                      :disabled="!rule(v.key).notify"
                      :aria-label="`${accessVerdictLabel(v.key)}: outside working hours`"
                      @update:model-value="(x: S['AccessHours']) => (rule(v.key).hours = x)"
                    />
                  </td>
                  <td>
                    <Checkbox
                      :model-value="rule(v.key).ask"
                      binary
                      :aria-label="`${accessVerdictLabel(v.key)}: Was this you?`"
                      @update:model-value="(x: boolean) => (rule(v.key).ask = x)"
                    />
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
          <p class="muted small intro">
            The only place Cookie reaches people outside working hours, on purpose. Alarming is never held back by a
            limit or a suppress.
          </p>
        </div>
      </section>

      <section class="panel" aria-labelledby="access-ask-title">
        <div id="access-ask-title" class="panel-header">Was this you?</div>
        <div class="panel-body body">
          <div class="switch">
            <ToggleSwitch v-model="s.draft.value.wasThisYou.on" input-id="access-ask-on" />
            <div>
              <label for="access-ask-on" class="label">Ask the account's own person</label>
              <div class="muted small">
                By e-mail, at once, once per case: code's text from the login only, with a link to a page without
                sign-in. Opening the link answers nothing; only its buttons do. No, it wasn't me makes the case alarming
                at once. Only for the verdicts whose row asks.
              </div>
            </div>
          </div>
          <template v-if="s.draft.value.wasThisYou.on">
            <div class="checks">
              <span class="checkbox">
                <Checkbox v-model="s.draft.value.wasThisYou.pnmt" binary input-id="access-ask-pnmt" />
                <label for="access-ask-pnmt">PNMT's sign-ins</label>
              </span>
              <span class="checkbox">
                <Checkbox v-model="s.draft.value.wasThisYou.servers" binary input-id="access-ask-servers" />
                <label for="access-ask-servers">Servers' accounts</label>
              </span>
              <span class="checkbox">
                <Checkbox v-model="s.draft.value.wasThisYou.routers" binary input-id="access-ask-routers" />
                <label for="access-ask-routers">Routers' accounts</label>
              </span>
            </div>
            <div class="form-grid">
              <div class="field">
                <label for="access-ask-hours">The link is valid</label>
                <InputNumber
                  v-model="s.draft.value.wasThisYou.validHours"
                  input-id="access-ask-hours"
                  :min="1"
                  :max="168"
                  suffix=" hours"
                  show-buttons
                  size="small"
                  :invalid="!!s.error('wasThisYou.validHours')"
                />
                <small v-if="s.error('wasThisYou.validHours')" class="field-error">{{
                  s.error('wasThisYou.validHours')
                }}</small>
                <small v-else class="field-hint">1 to 168 hours.</small>
              </div>
              <div class="field">
                <label for="access-ask-day">At most, per person a day</label>
                <InputNumber
                  v-model="s.draft.value.wasThisYou.perPersonDay"
                  input-id="access-ask-day"
                  :min="1"
                  :max="20"
                  show-buttons
                  size="small"
                  :invalid="!!s.error('wasThisYou.perPersonDay')"
                />
                <small v-if="s.error('wasThisYou.perPersonDay')" class="field-error">{{
                  s.error('wasThisYou.perPersonDay')
                }}</small>
              </div>
            </div>
          </template>
        </div>
      </section>

      <section class="panel" aria-labelledby="access-contacts-title">
        <div id="access-contacts-title" class="panel-header">
          Contacts
          <Select
            v-model="newContact"
            :options="addable"
            option-label="label"
            option-value="value"
            option-group-label="label"
            option-group-children="items"
            filter
            placeholder="Add a contact…"
            size="small"
            class="end"
            aria-label="Add a contact"
            :disabled="!addable.length"
            @update:model-value="addContact"
          />
        </div>
        <div class="panel-body body">
          <p class="muted small intro">
            Who is told, a user or a group. In working hours every contact is; group members who don't work today by the
            plan are skipped, a user named here never. Outside working hours, as the verdict's row says. A contact needs
            Monitoring: Edit for servers and routers, Audit log: View for PNMT.
          </p>
          <p v-if="!s.draft.value.contacts.length" class="muted none">None: the watch reaches nobody.</p>
          <div v-else class="wide-table">
            <table class="rows">
              <thead>
                <tr>
                  <th scope="col">Contact</th>
                  <th scope="col">Servers</th>
                  <th scope="col">Routers</th>
                  <th scope="col">PNMT</th>
                  <th scope="col">Also outside working hours</th>
                  <th scope="col"><span class="sr-only">Remove</span></th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="(c, i) in s.draft.value.contacts" :key="contactKey(c)">
                  <td>
                    <i :class="c.userId ? 'pi pi-user' : 'pi pi-users'" class="muted" aria-hidden="true" />
                    {{ contactName(c) }}
                    <div v-if="rowError(`contacts.${i}`)" class="field-error">{{ rowError(`contacts.${i}`) }}</div>
                  </td>
                  <td><Checkbox v-model="c.servers" binary :aria-label="`${contactName(c)}: servers`" /></td>
                  <td><Checkbox v-model="c.routers" binary :aria-label="`${contactName(c)}: routers`" /></td>
                  <td><Checkbox v-model="c.pnmt" binary :aria-label="`${contactName(c)}: PNMT`" /></td>
                  <td>
                    <ToggleSwitch
                      v-model="c.outsideHours"
                      :aria-label="`${contactName(c)}: also outside working hours`"
                    />
                  </td>
                  <td class="actions">
                    <Button
                      icon="pi pi-trash"
                      size="small"
                      severity="secondary"
                      text
                      :aria-label="`Remove ${contactName(c)}`"
                      @click="remove(s.draft.value.contacts, i)"
                    />
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
      </section>

      <section class="panel" aria-labelledby="access-trusted-title">
        <div id="access-trusted-title" class="panel-header">
          Trusted networks
          <Button
            label="Add a network"
            icon="pi pi-plus"
            size="small"
            severity="secondary"
            text
            class="end"
            @click="addTrusted"
          />
        </div>
        <div class="panel-body body">
          <p class="muted small intro">
            The office LAN, the VPN, the jump hosts: context for Cookie, never a pass. A stolen password used from the
            office is still a new account on the server.
          </p>
          <p v-if="!s.draft.value.trusted.length" class="muted none">None.</p>
          <div v-for="(t, i) in s.draft.value.trusted" :key="i" class="row">
            <div class="field grow">
              <label :for="`access-trusted-${i}`">Name</label>
              <InputText
                :id="`access-trusted-${i}`"
                v-model="t.name"
                size="small"
                maxlength="100"
                placeholder="Office LAN"
                :invalid="!!rowError(`trusted.${i}.name`)"
              />
              <small v-if="rowError(`trusted.${i}.name`)" class="field-error">{{
                rowError(`trusted.${i}.name`)
              }}</small>
            </div>
            <div class="field grow">
              <label :for="`access-trusted-${i}-cidr`">Network</label>
              <InputText
                :id="`access-trusted-${i}-cidr`"
                v-model="t.cidr"
                size="small"
                class="mono"
                placeholder="10.0.0.0/8"
                :invalid="!!rowError(`trusted.${i}.cidr`)"
              />
              <small v-if="rowError(`trusted.${i}.cidr`)" class="field-error">{{
                rowError(`trusted.${i}.cidr`)
              }}</small>
            </div>
            <Button
              icon="pi pi-trash"
              size="small"
              severity="secondary"
              text
              class="remove"
              :aria-label="`Remove ${t.name || 'the network'}`"
              @click="remove(s.draft.value.trusted, i)"
            />
          </div>
        </div>
      </section>

      <section class="panel" aria-labelledby="access-history-title">
        <div id="access-history-title" class="panel-header">History and limits</div>
        <div class="panel-body form-grid">
          <div class="field">
            <label for="access-history">History</label>
            <InputNumber
              v-model="s.draft.value.historyDays"
              input-id="access-history"
              :min="7"
              :max="180"
              suffix=" days"
              show-buttons
              size="small"
              :invalid="!!s.error('historyDays')"
            />
            <small v-if="s.error('historyDays')" class="field-error">{{ s.error('historyDays') }}</small>
            <small v-else class="field-hint">A login is compared with this many days, at most 180.</small>
          </div>
          <div class="field">
            <label for="access-learning">Learning period</label>
            <InputNumber
              v-model="s.draft.value.learningDays"
              input-id="access-learning"
              :min="0"
              :max="60"
              suffix=" days"
              show-buttons
              size="small"
              :invalid="!!s.error('learningDays')"
            />
            <small v-if="s.error('learningDays')" class="field-error">{{ s.error('learningDays') }}</small>
            <small v-else class="field-hint">A new target's first days: only strong signals count.</small>
          </div>
          <div class="field">
            <label for="access-limit-day">Notices per contact, a day</label>
            <InputNumber
              v-model="s.draft.value.limits.perContactDay"
              input-id="access-limit-day"
              :min="1"
              :max="100"
              show-buttons
              size="small"
              :invalid="!!s.error('limits.perContactDay')"
            />
            <small v-if="s.error('limits.perContactDay')" class="field-error">{{
              s.error('limits.perContactDay')
            }}</small>
          </div>
          <div class="field">
            <label for="access-limit-hour">Notices per contact, an hour</label>
            <InputNumber
              v-model="s.draft.value.limits.perContactHour"
              input-id="access-limit-hour"
              :min="1"
              :max="s.draft.value.limits.perContactDay"
              show-buttons
              size="small"
              :invalid="!!s.error('limits.perContactHour')"
            />
            <small v-if="s.error('limits.perContactHour')" class="field-error">{{
              s.error('limits.perContactHour')
            }}</small>
            <small v-else class="field-hint">Over a limit, e-mail goes out in one digest at the end of the hour.</small>
          </div>
        </div>
      </section>

      <SectionActions
        :busy="s.busy.value"
        :dirty="s.dirty.value"
        :errors="s.errors.value"
        :placed="placed"
        @save="s.save()"
        @reset="s.reset()"
      />
      <SettingsHistory section="access" title="the access watch" @rolled-back="s.reload()" />

      <AccessRoutersPanel />
      <AccessKnownPanel :targets="targets" />
    </template>
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
  max-width: 1100px;
}

.warning {
  margin: 0;
}

.counts {
  margin: 0;
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.intro,
.none {
  margin: 0;
}

.switch {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-3);
}

/* The switch keeps its size beside a long text. */
.switch > :first-child {
  flex: none;
}

.label {
  font-weight: 500;
}

.checks {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2) var(--pnmt-space-4);
}

.checkbox {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.kinds {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(16rem, 1fr));
  gap: var(--pnmt-space-3) var(--pnmt-space-4);
}

.kind-group {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
  border: none;
  margin: 0;
  padding: 0;
}

.kind-group legend {
  padding: 0;
  margin-bottom: var(--pnmt-space-2);
}

.row {
  display: flex;
  align-items: flex-start;
  flex-wrap: wrap;
  gap: var(--pnmt-space-2) var(--pnmt-space-3);
}

.row .grow {
  flex: 1 1 14rem;
}

.remove {
  margin-top: 1.6rem;
}

.end {
  margin-left: auto;
}

.wide-table {
  overflow-x: auto;
}

.rows {
  border-collapse: collapse;
  width: 100%;
}

.rows th {
  text-align: left;
  font-size: 0.857rem;
  font-weight: 600;
  color: var(--pnmt-muted);
  padding: var(--pnmt-space-1) var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
  white-space: nowrap;
}

.rows td {
  padding: var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
  vertical-align: top;
}

.actions {
  text-align: right;
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
}

.small {
  font-size: 0.857rem;
}
</style>
