<script setup lang="ts">
import { computed } from 'vue'
import { useSession } from '@/stores/session'

/**
 * The documentation of licences & services (docs/licences-plan.md): what an item is, its sensors and notices, the costs, the
 * documents, the rights, and the switch. Only while Licenses__Enabled is on (§13.2): off, the feature isn't documented in the app.
 */
const props = defineProps<{ search: string }>()
const session = useSession()
const text =
  'licences licence licenses license services service hardware item items template templates category categories costs cost ' +
  'charges charge spent forecast per month per year renewal renew automatic renewal notice period cancellation deadline cancel by ' +
  'expiry expires end of life eol warranty lease seats over-assigned owner owners notices e-mail account notifications review ' +
  'reviewed documents invoice contract key keys product key licence key show keys linked sensors support contact customer number sla ' +
  'cost centre location provider vendor manufacturer worth a look upcoming overview csv export licenses_list license_get ' +
  'licenses_costs 245 246 445 446 447 448 449 licenses__enabled switch off on'
const show = computed(() => {
  if (!session.info?.licenses) return false
  const q = props.search.trim().toLowerCase()
  return !q || text.includes(q)
})
</script>

<template>
  <section v-if="show" class="panel" aria-labelledby="licences-docs-title">
    <div id="licences-docs-title" class="panel-header">Licences &amp; services</div>
    <div class="panel-body prose">
      <p>
        <strong>Licences &amp; services keeps what IT pays for</strong> (Knowledge → Licences &amp; services): licences,
        services (cloud and SaaS, hosting, internet lines, phones, support contracts, domains, consulting) and hardware,
        with their hosts, dates, seats, costs and documents. Each is an <em>item</em>, numbered LIC-12, SVC-31 or HW-40.
      </p>
      <ul>
        <li>
          <strong>Its state</strong> is the item's own: OK (245), renewed (246, once), about to expire or its
          cancellation deadline near (445), expired (446), its end of life near (447) or passed (448), more seats used
          than bought (449). Reviews due fail nothing; they are listed, and told to the owner.
        </li>
        <li>
          <strong>On a host, an item is a sensor</strong> that shows its state; the host's notification rules tell the
          host's people. Unassigning removes the sensor. Its name and host follow the item; it can't be deleted on its
          own.
        </li>
        <li>
          <strong>An item on no host has no sensor.</strong> Its owner is told by e-mail when it goes bad, a few days
          before the date (<code>Licenses__RemindDays</code>), when it renews by itself and when a review falls due.
          Items nobody owns are told to whoever with <em>Edit</em> chooses so on <em>Account → Notifications</em>. An
          item nobody hears about is <em>worth a look</em>.
        </li>
        <li>
          <strong>Linked sensors</strong> (an internet line's ping) show the provider's support contact, customer number
          and SLA on their page, where an outage is handled.
        </li>
        <li>
          <strong>Costs:</strong> PNMT records each billing period's charge itself (estimated for prices paid by use);
          invoices correct them. The forecast is today's prices ahead, marking what depends on a renewal by hand. Totals
          are per currency, never converted. <em>Overview</em> and <em>Costs</em> group them by category, provider, cost
          centre, location, host or owner.
        </li>
        <li>
          <strong>Keys</strong> are encrypted and shown on request (<em>Details</em>, audited).
          <strong>Documents</strong> (invoices, contracts, licence and key files) are downloaded only through their
          item, with <em>Details</em>.
        </li>
        <li>
          <strong>Rights</strong> (<em>Licences &amp; services</em>): View sees everything but keys and documents, the
          costs included; Details shows keys and downloads documents; Edit changes items; Manage keeps the templates,
          categories and names, and deletes.
        </li>
        <li>
          <strong>Over MCP and for Cookie:</strong> <code>licenses_list</code>, <code>license_get</code> and
          <code>licenses_costs</code> read them, never the keys or documents.
        </li>
        <li>
          <strong>The switch:</strong> <code>Licenses__Enabled</code> in <code>.env</code>, off by default. Off, nothing
          of it is usable or visible; its data stays, and only the item sensors are removed, to be made again when it is
          turned on. A rule or a board element made for one single item sensor goes with it.
        </li>
      </ul>
    </div>
  </section>
</template>
