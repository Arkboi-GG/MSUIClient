# GI-60: followers after an anchor port

Candidate Core patch: `gi60-hold-after-port.patch`. It is not installed, compiled,
or live-tested. It requires owner-controlled Core rollout and follow-up tests.

The current AGENTS.md standing rule 3 says followers hold when the driven body
flies, ports, or is hopped away from. Existing POSSESS_LAW sections 4.3 and 7.2,
Core comments, and the older law check still describe following a port. This
candidate follows the current supplied standing rule. Those detailed documents
and regression checks must be reconciled with the rollout; do not report the
current source-only law check as evidence that this behavior works.

Live reproduction: greg's Nbwlkgnome at (-6325,512), SuperUI Nbprihuman linked and
nearby; `.go xyz -6540 374 397 0`. Server.log 2026-09-08 03:19:50 reports
`party-catchup` and a 258-yard catch-up teleport. Frame 031951 shows the priest
already in the cave. The separate `.namego Nbprihuman` command was only sent at
03:20:08, after the automatic relocation and first enemy kill. It did not cause
the reproduced violation.

The patch changes distant same-anchor ports into the existing world-hold state
and replaces cross-map delayed teleport with hold plus follow-leg termination.
Ordinary same-map distance catch-up, explicit GM placement, near-teleport
acknowledgments, and explicit player orders keep their existing paths.

Required regression matrix before approval: same-anchor near port beyond the
threshold, cross-map port, taxi flight/landing, distant possession switch, normal
walking lag, anchor return in range, explicit relink, explicit move/attack orders,
unlinked follower, and unattended main while another party member is driven.
Verify both world positions and an ended follow generator; a roster icon alone
is insufficient. Run both client/Core possession checks after reconciliation.

Copied source normalized SHA256:
`4946555f9a89855e2f6e78d602cdea87e5d5d1ee3ef15638c218e005507b512c`.
Built and installed Core timestamps both correspond to 2026-09-07 23:21:23.
