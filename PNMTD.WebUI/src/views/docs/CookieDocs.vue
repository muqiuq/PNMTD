<script setup lang="ts">
import { computed } from 'vue'

/**
 * The documentation of Cookie, PNMT's assistant (docs/cookie-plan.md, packages nine and nine point five, revision 7):
 * what it does in this release. The availability plan has a section of its own (AvailabilityDocs).
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
  'contact details account pushover key send a test redmine user availability plan responsible fallback ' +
  'without redmine zabbix unavailable unreachable grace period e-mail instead of a ticket ' +
  'proposal propose auto off level threshold confidence seraph review verdict approve revise reject inbox decisions do it ' +
  "don't noted dismiss decision log rating right wrong lessons summariser mute suppress instruction draft confirm end now " +
  'big brother solution model budget Cookie__BigBrother__BudgetUsd Cookie__SeraphModel drift report duties texts ' +
  'ai every time mail rule event'
const show = computed(() => has(cookieText))
</script>

<template>
  <section v-if="show" class="panel" aria-labelledby="cookie-docs-title">
    <div id="cookie-docs-title" class="panel-header">Cookie</div>
    <div class="panel-body prose">
      <p>
        <strong>Cookie is PNMT's assistant.</strong> It keeps an eye on what PNMT knows (sensors, Zabbix problems,
        Redmine tickets) so that nothing important falls through the cracks. It explains the state, writes notes on
        cases and short personal summaries, and answers in its chat.
        <strong>In this release Cookie also proposes actions</strong>: a Pushover or an e-mail to a person, a Redmine
        ticket, a private note or a public answer on a ticket, a note on a host or sensor, or handing something to a
        person. <strong>Code decides whether a proposal happens</strong>, and Seraph, a second model, reviews whatever
        leaves PNMT or reaches people. Every action only adds: Cookie never deletes, closes or changes anything.
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
          rights and its own. Pick <em>Seraph</em> to ask Seraph instead: it explains its verdicts from the last 14
          days' decisions you may see, and can't act. At most 20 messages an hour; nothing while Cookie is stopped. The
          chat doesn't act: it says what Cookie would do from a case and under which conditions; proposals come only
          from Cookie's work on cases. <strong>Except on an admin's order:</strong> an admin ticks <em>With powers</em>,
          confirms a warning, and Cookie acts on that one message, past its levels, mutes, working hours and limits.
          Seraph still reads every order and stops one it is sure is harmful; a doubt waits in the decisions inbox.
          Every action is logged under the admin's name. Nobody else sees the checkbox.
        </li>
        <li>
          <strong>Without Redmine or Zabbix:</strong> when Redmine isn't configured, can't be written, or has been
          unreachable for half an hour, Cookie proposes no tickets and no notes on tickets, and the ticket tool isn't
          offered to it. What would have been a ticket goes by e-mail to whoever is responsible, and by Pushover too
          when it is urgent; the problem's own duty does the same, and a short e-mail follows when the problem is
          resolved. Without Zabbix, Cookie doesn't look for Zabbix problems. The first successful read brings everything
          back. <em>Admin → Cookie → Overview</em> and <em>Actions</em> show what Cookie counts as available.
        </li>
        <li>
          <strong>How a proposal goes</strong>, each step in Cookie's decision log: the action's level (Off drops it);
          the confidence (below the action's minimum, a person decides); the targets (an active PNMT user with the
          channel's address, the case's own ticket, Cookie's group's rights); mute and suppress, and a stopped case;
          duplicates (the same action on the same target for the same case within a day); then Seraph, who approves,
          sends it back once with a reason, rejects it, or asks a person. Without Seraph, a person decides. At
          <em>Propose</em> it waits in the decisions inbox; at <em>Auto</em> it goes out, through the output limits,
          with Cookie's name as the signature.
        </li>
        <li>
          <strong>The levels</strong> are set per action under Admin → Cookie → Actions: <em>Off</em>,
          <em>Propose</em> (a person approves, after Seraph) or <em>Auto</em> (Seraph's approval is enough), with a
          minimum confidence (0.7 by default). Everything that leaves PNMT or reaches people starts at Propose; a note
          on a host or sensor and a mail rule's event start at Auto.
        </li>
        <li>
          <strong>The decisions inbox</strong> (Monitoring → Decisions) shows Cookie's proposals as
          <em>Cookie's proposal</em>: <em>Do it</em> sends it through the limits; <em>Don't</em> takes a comment that
          says why. What Cookie hands to a person is <em>Noted</em> or <em>Dismiss</em>.
        </li>
        <li>
          <strong>The decision log</strong> (Cookie's window → Decisions) lists every proposal with what came of it
          (done, proposed, approved, declined, rejected, off, refused, duplicate, muted, held back, failed), Seraph's
          verdict and reason, the confidence and the model. Whoever may see an entry's case marks it <em>Right</em> or
          <em>Wrong</em>, with a comment.
        </li>
        <li>
          <strong>Lessons:</strong> once a day, or after 10 new ratings, a summariser turns the ratings into short rules
          that go into every prompt of Cookie and Seraph, at most 30 active. Admins edit, turn off and roll them back
          under Admin → Cookie → Lessons. A lesson that is a rule ("no Pushover for toner warnings") becomes a suppress
          for 30 days, which code enforces. Once a day Seraph also writes a drift report into the log.
        </li>
        <li>
          <strong>Mute and suppress</strong> (Cookie: Instruct) are enforced by code, for at most 30 days:
          <em>mute</em> a host, a sensor, a location or a case (no messages, tickets, notes on tickets or big brother
          calls for it; its cases still open), <em>suppress</em> a channel (Pushover, e-mail, tickets, notes on
          tickets), a sensor type or a kind of case. A <em>note</em> is a free-text instruction that goes into the
          prompts with your name; it can narrow or steer, never widen. In the chat, Cookie fills one in as a draft that
          you confirm with <em>Confirm</em>; Admin → Cookie → Mute, suppress creates them directly. Each has
          <em>End now</em>, and all are in the audit log.
        </li>
        <li>
          <strong>The big brother</strong> is two strong models for problems nobody fixes. When a case matters and
          nobody has taken care of it for a working day, code offers it to Cookie; someone with Cookie: Instruct can
          also <em>Ask the big brother</em> on a case. One of the two models answers at random, the other on a second
          call or a failure. It returns the likely cause, the steps of a fix for a person, and how to check it; nothing
          is run. The solution shows on the case, and goes as a private note to the case's ticket, or into the decisions
          inbox. It is off until an admin turns it on (Actions), and has a budget of its own
          (<code>Cookie__BigBrother__BudgetUsd</code>).
        </li>
        <li>
          <strong>The duties' texts:</strong> with <em>Cookie writes the duties' texts</em> on (Actions), Cookie writes
          each reminder and escalation from the case, and Seraph reviews it; code still decides when, to whom and
          through which limits. The template is used whenever Cookie is stopped, paused, slow or rejected.
        </li>
        <li>
          <strong>Mail rules set to <em>AI every time</em></strong> go to Cookie while it is enabled: a matching mail
          waits for its next round, Cookie decides OK or fail, Seraph reviews it, and the rule's code goes to its
          sensor. What Cookie can't decide becomes the inbox's <em>The AI could not decide</em>. While Cookie is stopped
          or paused, such mails wait, visible in the mail log.
        </li>
        <li>
          <strong>Rights</strong>, in the area <em>Cookie</em>: <em>Use</em> opens the window, the chat, comments and
          ratings; <em>Instruct</em> adds stopping a case and letting Cookie go on, mute, suppress and notes from the
          chat, and asking the big brother; <em>Manage</em> adds Admin → Cookie, and starting and stopping it. The card
          needs no right.
        </li>
        <li>
          <strong>Admin → Cookie</strong> (Cookie: Manage): Start, Stop and Run now, the last rounds, the budget, what
          Cookie did today, the duties and the outbox; who Cookie is and its prompts, each with its default; the
          instructions for each kind of case; the watched Redmine projects with their escalation groups; the actions'
          levels, the duties and the output limits; mute and suppress; the lessons; the models, Seraph's and the big
          brother's too; and Cookie's group, whose rights it reads with, never the admin flag. Every save is a version
          that can be viewed and rolled back. Cookie itself can't reach any of it. The working hours and holidays are in
          the availability plan's settings.
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
          and the duties go on. The big brother's budget is separate. Their requests are in Admin → AI's request log.
          Seraph's model is <code>Cookie__SeraphModel</code>, or the one under Models.
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
