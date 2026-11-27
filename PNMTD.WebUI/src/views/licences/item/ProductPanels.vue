<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import { computed, reactive } from 'vue'
import {
  kindLabels,
  kindsOf,
  licenseKeys,
  licensesApi,
  typeLabels,
  vendorLabel,
  type LicenseDetail,
  type LicenseKind,
  type LicenseType,
} from '@/api/licenses'
import InlineField from '@/components/InlineField.vue'
import LocationPicker from '@/views/locations/LocationPicker.vue'
import NameField from '../NameField.vue'
import OwnerPicker from './OwnerPicker.vue'
import { useItemSave } from './useItemSave'

/**
 * The item's *Product* (or *Service*, *Hardware*), *Support*, and *Where and whose* panels (docs/licences-plan.md §8.7): each row
 * saved where it is shown, read-only without Edit. The support is shown for services, and for the others once filled in.
 */
const props = defineProps<{ item: LicenseDetail; readonly: boolean }>()
const { save } = useItemSave(() => props.item)
const text = (v: unknown) => (v === null || v === undefined ? null : String(v).trim() || null)
type Value = string | number | boolean | null

const categories = useQuery({ queryKey: licenseKeys.categories, queryFn: licensesApi.categories, staleTime: 60_000 })
const categoryOptions = computed(() => (categories.data.value ?? []).map((c) => ({ label: c.name, value: c.id })))
const typeOptions = (Object.keys(typeLabels) as LicenseType[]).map((t) => ({ label: typeLabels[t], value: t }))
const kindOptions = computed(() => kindsOf[props.item.type].map((k) => ({ label: kindLabels[k], value: k })))
const title = computed(() => ({ License: 'Product', Service: 'Service', Hardware: 'Hardware' })[props.item.type])
const showSupport = computed(
  () => props.item.type === 'Service' || !!(props.item.supportContact || props.item.supportUrl || props.item.sla),
)
/** Only http(s) links (the server allows no other). */
const link = (url: string | null | undefined) => (url && /^https?:\/\//i.test(url) ? url : null)

// Another type keeps the kind when the type has it, else Other (§3.3: the kind must be one of the type's).
const saveType = (v: Value) => {
  const type = v as LicenseType
  return save({ type, kind: kindsOf[type].includes(props.item.kind) ? props.item.kind : 'Other' })
}

// The drafts of the rows edited with a picker.
const drafts = reactive({
  manufacturer: null as string | null | undefined,
  vendor: null as string | null | undefined,
  costCenter: null as string | null | undefined,
  locationId: null as string | null,
  ownerId: null as string | null | undefined,
})
</script>

<template>
  <section class="panel" aria-labelledby="product-title">
    <div id="product-title" class="panel-header">{{ title }}</div>
    <div class="panel-body">
      <dl class="facts">
        <dt>Name</dt>
        <dd>
          <InlineField
            :value="item.name"
            label="Name"
            field="name"
            :readonly="readonly"
            :save="(v) => save({ name: text(v) })"
          />
        </dd>
        <dt>Type</dt>
        <dd>
          <InlineField
            :value="item.type"
            type="enum"
            :options="typeOptions"
            label="Type"
            field="type"
            :readonly="readonly"
            :save="saveType"
          />
        </dd>
        <dt>Template</dt>
        <dd>
          <RouterLink v-if="item.templateId" :to="{ name: 'licences', params: { tab: 'templates' } }">{{
            item.templateName ?? 'A template'
          }}</RouterLink>
          <span v-else class="muted">none</span>
        </dd>
        <dt>Category</dt>
        <dd>
          <InlineField
            :value="item.categoryId"
            type="enum"
            :options="categoryOptions.length ? categoryOptions : [{ label: item.categoryName, value: item.categoryId }]"
            label="Category"
            field="categoryId"
            :readonly="readonly"
            :save="(v) => save({ categoryId: v as string })"
          />
        </dd>
        <dt>Manufacturer</dt>
        <dd>
          <InlineField
            :value="item.manufacturer"
            type="custom"
            label="Manufacturer"
            field="manufacturer"
            :readonly="readonly"
            :save="() => save({ manufacturer: text(drafts.manufacturer) })"
            @edit="drafts.manufacturer = item.manufacturer"
          >
            <template #editor>
              <NameField v-model="drafts.manufacturer" field="manufacturers" input-id="li-manufacturer" />
            </template>
          </InlineField>
        </dd>
        <dt>{{ item.type === 'Service' ? 'Service' : 'Product' }}</dt>
        <dd>
          <InlineField
            :value="item.productName"
            label="Product"
            field="productName"
            :readonly="readonly"
            :save="(v) => save({ productName: text(v) })"
          />
        </dd>
        <dt>{{ item.type === 'Hardware' ? 'Model' : 'Edition' }}</dt>
        <dd>
          <InlineField
            :value="item.edition"
            :label="item.type === 'Hardware' ? 'Model' : 'Edition'"
            field="edition"
            :readonly="readonly"
            :save="(v) => save({ edition: text(v) })"
          />
        </dd>
        <dt>{{ vendorLabel(item.type) }}</dt>
        <dd>
          <InlineField
            :value="item.vendor"
            type="custom"
            :label="vendorLabel(item.type)"
            field="vendor"
            :readonly="readonly"
            :save="() => save({ vendor: text(drafts.vendor) })"
            @edit="drafts.vendor = item.vendor"
          >
            <template #editor>
              <NameField v-model="drafts.vendor" field="vendors" input-id="li-vendor" />
            </template>
          </InlineField>
        </dd>
        <dt>Kind</dt>
        <dd>
          <InlineField
            :value="item.kind"
            type="enum"
            :options="kindOptions"
            label="Kind"
            field="kind"
            :readonly="readonly"
            :save="(v) => save({ kind: v as LicenseKind })"
          />
        </dd>
        <dt>Licensed to</dt>
        <dd>
          <InlineField
            :value="item.licensedTo"
            label="Licensed to"
            field="licensedTo"
            :readonly="readonly"
            :save="(v) => save({ licensedTo: text(v) })"
          />
        </dd>
        <dt>{{ item.type === 'Hardware' ? 'Serial number' : 'Reference' }}</dt>
        <dd>
          <InlineField
            :value="item.reference"
            label="Reference"
            field="reference"
            :placeholder="item.type === 'Hardware' ? 'the serial number' : 'the order or contract number'"
            :readonly="readonly"
            :save="(v) => save({ reference: text(v) })"
          />
        </dd>
        <dt>Customer number</dt>
        <dd>
          <InlineField
            :value="item.customerNumber"
            label="Customer number"
            field="customerNumber"
            :readonly="readonly"
            :save="(v) => save({ customerNumber: text(v) })"
          />
        </dd>
        <dt>Portal</dt>
        <dd class="with-link">
          <InlineField
            :value="item.portalUrl"
            label="Portal"
            field="portalUrl"
            placeholder="where it is managed"
            :readonly="readonly"
            :save="(v) => save({ portalUrl: text(v) })"
          />
          <a
            v-if="link(item.portalUrl)"
            :href="link(item.portalUrl) ?? undefined"
            target="_blank"
            rel="noopener noreferrer"
            aria-label="Open the portal"
            ><i class="pi pi-external-link" aria-hidden="true"
          /></a>
        </dd>
      </dl>
    </div>
  </section>

  <section v-if="showSupport" class="panel" aria-labelledby="support-title">
    <div id="support-title" class="panel-header">Support</div>
    <div class="panel-body">
      <dl class="facts">
        <dt>Support contact</dt>
        <dd>
          <InlineField
            :value="item.supportContact"
            type="textarea"
            label="Support contact"
            field="supportContact"
            placeholder="phone, e-mail, hours"
            :readonly="readonly"
            :save="(v) => save({ supportContact: text(v) }, 'supportContact')"
          />
        </dd>
        <dt>Support URL</dt>
        <dd class="with-link">
          <InlineField
            :value="item.supportUrl"
            label="Support URL"
            field="supportUrl"
            :readonly="readonly"
            :save="(v) => save({ supportUrl: text(v) })"
          />
          <a
            v-if="link(item.supportUrl)"
            :href="link(item.supportUrl) ?? undefined"
            target="_blank"
            rel="noopener noreferrer"
            aria-label="Open the support page"
            ><i class="pi pi-external-link" aria-hidden="true"
          /></a>
        </dd>
        <dt>SLA</dt>
        <dd>
          <InlineField
            :value="item.sla"
            label="SLA"
            field="sla"
            placeholder="24/7, 4 h response"
            :readonly="readonly"
            :save="(v) => save({ sla: text(v) })"
          />
        </dd>
      </dl>
    </div>
  </section>

  <section class="panel" aria-labelledby="where-title">
    <div id="where-title" class="panel-header">Where and whose</div>
    <div class="panel-body">
      <dl class="facts">
        <dt>Cost centre</dt>
        <dd>
          <InlineField
            :value="item.costCenter"
            type="custom"
            label="Cost centre"
            field="costCenter"
            :readonly="readonly"
            :save="() => save({ costCenter: text(drafts.costCenter) })"
            @edit="drafts.costCenter = item.costCenter"
          >
            <template #editor>
              <NameField v-model="drafts.costCenter" field="costCenters" input-id="li-cost-center" />
            </template>
          </InlineField>
        </dd>
        <dt>Location</dt>
        <dd>
          <InlineField
            :value="item.locationName"
            type="custom"
            label="Location"
            field="locationId"
            placeholder="none"
            :readonly="readonly"
            :save="() => save({ locationId: drafts.locationId })"
            @edit="drafts.locationId = item.locationId"
          >
            <template #editor>
              <LocationPicker v-model="drafts.locationId" input-id="li-location" size="small" />
            </template>
          </InlineField>
        </dd>
        <dt>Owner</dt>
        <dd>
          <InlineField
            :value="item.ownerName"
            type="custom"
            label="Owner"
            field="ownerId"
            placeholder="nobody"
            :readonly="readonly"
            :save="() => save({ ownerId: drafts.ownerId ?? null })"
            @edit="drafts.ownerId = item.ownerId"
          >
            <template #editor>
              <OwnerPicker
                v-model="drafts.ownerId"
                input-id="li-owner"
                :current="item.ownerId ? { id: item.ownerId, name: item.ownerName } : null"
              />
            </template>
          </InlineField>
        </dd>
      </dl>
    </div>
  </section>
</template>

<style scoped>
.with-link {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}
</style>
