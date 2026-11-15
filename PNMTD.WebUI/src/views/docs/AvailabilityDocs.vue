<script setup lang="ts">
import { computed } from 'vue'

/** The documentation of the availability plan (docs/cookie-plan.md §8.2, §9.4, revision 7). */
const props = defineProps<{ search: string }>()
const text =
  'availability plan responsible fallback global fallback types ANWE HOME VACA UNAV EXTN KOMP vacation holiday on site ' +
  'work travel home office compensation default days off count columns minimum fill fill again default week short code ' +
  'colour working hours holidays closed rights view edit manage people settings today'
const show = computed(() => {
  const q = props.search.trim().toLowerCase()
  return !q || text.toLowerCase().includes(q)
})
</script>

<template>
  <section v-if="show" class="panel" aria-labelledby="availability-docs-title">
    <div id="availability-docs-title" class="panel-header">Availability plan</div>
    <div class="panel-body prose">
      <p>
        <strong>The availability plan says who is responsible, who works, and where</strong>, day by day (Admin →
        Availability plan). It follows the team's Excel plan, and it is what Cookie and its duties go by when they
        decide whom to reach.
      </p>
      <ul>
        <li>
          <strong>The table has a row per day</strong>: the responsible and the fallback, a comment, the count columns,
          and a column per person with the day's type by its code and colour. It scrolls into the past and the future;
          <em>Today</em> jumps back, and today's row is marked. Weekends and closed holidays are greyed and need
          nothing.
        </li>
        <li>
          <strong>Editing:</strong> click a cell and pick a type, or type its code; shift-click or drag to set several
          at once, and Delete empties them. Everyone in the plan edits their own column from today on.
        </li>
        <li>
          <strong>Types</strong> mean what their checkboxes say: vacation, holiday, on site, work travel, home office,
          compensation; several may be checked. A person works on a day whose type is on site, work travel or home
          office and none of vacation, holiday or compensation. One type is the default, one is for days off.
        </li>
        <li>
          <strong>Fill</strong> writes every empty working day of the next 365 from each person's default week, and
          never over what a person set; <em>Fill again</em> also replaces what the fill wrote. It asks which days (from
          today on, never the past), which people and which types to replace, so holidays and illness can stay; and, if
          asked, it also replaces what people set themselves. A nightly task fills the day that comes into reach.
          <em>Fill mine</em> does the same for one's own column.
        </li>
        <li>
          <strong>Red</strong> marks a working day without a responsible, a responsible or fallback who doesn't work
          that day, a count below its minimum, and an empty cell within the next 365 days.
        </li>
        <li>
          <strong>Who is responsible</strong> in working hours: the day's responsible if they work, else the fallback if
          they work, else the global fallback (a user, or a group's members who work that day). Outside working hours,
          Cookie reaches nobody; PNMT's notification rules alert at any hour.
        </li>
        <li>
          <strong>Rights</strong>, in the area <em>Availability</em>: <em>View</em> sees the plan; <em>Edit</em> edits
          every column, the past, the responsible, the fallback and the comments, and fills for everyone;
          <em>Manage</em> adds the <em>People</em> tab (who takes part, their short code, colour, default week and
          column) and the <em>Settings</em> (the types, the count columns, the working hours and holidays, the global
          fallback). Everyone in the plan sees it without a right. Cookie's group needs View for Cookie to read it.
          Every save is in the audit log.
        </li>
      </ul>
    </div>
  </section>
</template>

<style scoped>
.prose p {
  margin: 0 0 var(--pnmt-space-3);
  max-width: 75ch;
}

.prose ul {
  margin: 0;
  padding-left: 1.2rem;
  max-width: 75ch;
}

.prose li + li {
  margin-top: var(--pnmt-space-2);
}
</style>
