<script setup lang="ts">
import { computed } from 'vue'

/**
 * The documentation of Cookie, PNMT's assistant (docs/cookie-plan.md, package nine and revision 7): what it does in this
 * release. The availability plan has a section of its own (AvailabilityDocs).
 */
const props = defineProps<{ search: string }>()
const has = (...texts: string[]) => {
  const q = props.search.trim().toLowerCase()
  return !q || texts.some((t) => t.toLowerCase().includes(q))
}
const cookieText =
  'cookie assistant ai case book cases deadlines tickets first answer update duties reminders escalation outbox held back limits ' +
  'dashboard card summary window trace chat rights use instruct manage admin start stop run privacy placeholders person email phone ' +
  'budget Cookie__Enabled Cookie__BudgetUsd Redmine__WriteApiKey working hours prompts comment stop go on stopped ' +
  'contact details account pushover key send a test redmine user availability plan responsible fallback'
const show = computed(() => has(cookieText))
</script>

<template>
  <section v-if="show" class="panel" aria-labelledby="cookie-docs-title">
    <div id="cookie-docs-title" class="panel-header">Cookie</div>
    <div class="panel-body prose">
      <p>
        <strong>Cookie is PNMT's assistant.</strong> It keeps an eye on what PNMT knows (sensors, Zabbix problems,
        Redmine tickets) so that nothing important falls through the cracks.
        <strong>In this release Cookie watches:</strong> it explains the state, writes notes on cases and short personal
        summaries, and answers in its chat. Everything it writes stays inside PNMT: no text from the AI leaves PNMT, by
        mail, Pushover or Redmine.
      </p>
      <p>
        <strong>The bookkeeping runs in code, without AI</strong>, whenever <code>Cookie__Enabled=true</code>, whether
        Cookie itself is started or not. The case book opens a case for each host with bad sensors or Zabbix problems,
        and one for each ticket in a watched Redmine project, and closes it when all is well again. Code keeps the
        deadlines: a first answer by staff within 4 working hours, an update every 14 days, a ticket for a problem
        nobody resolves in 2 days, and a closing note when it is resolved. Code also computes the overall state shown
        while Cookie isn't running.
      </p>
      <ul>
        <li>
          <strong>The dashboard's card</strong>, for everyone: the overall state (All fine, Info, Warning, Alert) with
          its reason and the cases behind it, Cookie's own state (Stopped, Sleeping, Working, Paused), and your personal
          summary, what needs you first. It only names what you may see. The same card heads Cookie's window.
        </li>
        <li>
          <strong>Cookie's window</strong> (Overview → Cookie): the cases with their timelines, deadlines and duties;
          the trace of Cookie's rounds, like Copilot's; and the chat. You see the notes and replies on the cases you may
          see; the full trace, with Cookie's thinking and what its tools returned, is for Cookie: Manage, or for whoever
          has every right Cookie's group has.
        </li>
        <li>
          <strong>Comments on a case:</strong> with Cookie: Use, write a comment on a case you may see. It goes into the
          case's timeline with your name, and Cookie reads it in its next round, screened and with names replaced.
        </li>
        <li>
          <strong>Stopping a case</strong> (Cookie: Instruct): <em>Stop</em> on a case says
          <em>Don't go on with this</em>, with a comment that says why. Cookie leaves the case alone, its due duties
          close as stopped and nothing new falls due, and it raises the overall state to Info at most. The case stays
          open and marked <em>stopped</em>, with who, when and why. It goes on with <em>Go on</em>, when the case
          closes, or, for a problem, when something new fails on the host. PNMT's notification rules still fire.
          Comments, stops and Go on are in the audit log.
        </li>
        <li>
          <strong>The chat:</strong> a message wakes Cookie, and it answers in the same thread with the lower of your
          rights and its own. At most 20 messages an hour; nothing while Cookie is stopped.
        </li>
        <li>
          <strong>Rights</strong>, in the area <em>Cookie</em>: <em>Use</em> opens the window, the chat and comments;
          <em>Instruct</em> adds stopping a case and letting Cookie go on (temporary instructions come with a later
          release); <em>Manage</em> adds Admin → Cookie, and starting and stopping it. The card needs no right.
        </li>
        <li>
          <strong>Admin → Cookie</strong> (Cookie: Manage): Start, Stop and Run now, the last rounds, the budget, the
          duties and the outbox; who Cookie is and its prompts, each with its default; the instructions for each kind of
          case; the watched Redmine projects with their escalation groups; the duties and the output limits; the models;
          and Cookie's group, whose rights it reads with, never the admin flag. Every save is a version that can be
          viewed and rolled back. Cookie itself can't reach any of it. The working hours and holidays are in the
          availability plan's settings.
        </li>
        <li>
          <strong>Who gets what</strong> follows the availability plan: a ticket's reminder goes to its assignee (found
          by the Redmine user on their PNMT user), or to the project's escalation group when nobody is assigned or the
          assignee doesn't work that day; a missed deadline also reaches whoever is responsible that day. Important
          messages go by e-mail, urgent ones by Pushover too, and only in working hours.
        </li>
        <li>
          <strong>Your contact details</strong> are on <em>Account</em>: your e-mail address and Pushover user key, each
          with <em>Send a test</em>. Admins set them under Admin → Users too, and only admins set the Redmine user. The
          notification rules keep their own recipients.
        </li>
        <li>
          <strong>Cookie is stopped until an admin starts it</strong>; after a restart it resumes. Stopped, the case
          book and the duties keep running.
        </li>
        <li>
          <strong>Every duty starts off</strong>: reminders, escalations, the ticket after 2 days and the closing note
          send nothing until an admin switches them on (Admin → Cookie → Actions). The output limits hold back what goes
          over them; an admin can send it from the outbox.
          <strong>Tickets and notes in Redmine need <code>Redmine__WriteApiKey</code></strong
          >; without it, reminders go by e-mail and Pushover only.
        </li>
        <li>
          <strong>Privacy:</strong> before anything goes to a model, people's names, e-mail addresses and phone numbers
          become placeholders (<code>PERSON_1</code>, <code>EMAIL_1</code>, <code>PHONE_1</code>), and are put back
          before anything is shown. Host names and IP addresses stay as they are. Knowledge base pages marked as
          sensitive never reach Cookie.
        </li>
        <li>
          <strong>Cookie has a budget of its own</strong>: <code>Cookie__BudgetUsd</code> (30 USD a month by default),
          or the one set under Admin → Cookie → Models. At the budget Cookie pauses until the next month; the case book
          and the duties go on. Its requests are in Admin → AI's request log.
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
