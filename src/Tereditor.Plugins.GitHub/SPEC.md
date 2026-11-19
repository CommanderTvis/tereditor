---
id: module-plugin-github
type: module-design
status: active
title: GitHub — pull requests and GitHub sign-in, as a builtin repo hosting plugin
parent: group-plugins-source-control
references: [module-plugin-api, submodule-host-plugins, submodule-host-git, submodule-ui-plugins]
tags: [plugins, git]
---

# GitHub — pull requests and GitHub sign-in, as a builtin repo hosting plugin

## Responsibility

`GitHub` (`github`, enabled by default, category Version Control, no dependencies, wire version 1) is
the first provider of the generic repo hosting surface ([Plugins](../Tereditor.Host.Core/Plugins.SPEC.md#repo-hosting-providers-h24-w28),
Plugin API H24 and W28). It owns everything that is GitHub and not git:

- Recognition: a remote URL belongs to it when its host is `github.com` (https, ssh and scp forms; enterprise hosts are
  not recognized).
- The `gh` CLI, the only way it talks to GitHub: `gh auth status`, `gh pr list/create/edit/ready/merge/reopen`,
  `gh repo view` for the allowed merge methods (cached per remote for ten minutes). No tokens are read or stored.
  `TEREDITOR_GH_OFFLINE=1` makes every lookup and mutation report unavailable.
- The mapping from GitHub's values to the provider-neutral `PullRequest` (below), including GitHub's merge state and
  status-check rollup.
- Its UI: the pull request section of the
  Review panel (state, title, checks and link of the branch's pull request, its next-step button, compose, push updates, merge dialog, setup guidance), the compare-link fallback, and the Settings
  page `GitHub` (sign-in status from `gh auth status`, Refresh). Wording stays "PR".

It does not run `git`: pushes, fetches, ahead/behind and the remote branch stay in the host.

## What stays in the host and what moves

Stays: `origin` and remote-URL reading, `GetBranchStatusAsync` git facts (has origin, published, ahead, behind, ahead of
base, on base, HEAD), push (never forced, `confirmBase`), delete-remote-branch (leased `git push origin --delete`),
the Remote segment and its Publish/Push/Sync step, the Open-review guards and push step, the draft proposal (branch
name and commit subjects), request budgets, the lookup cache and routing ([Git](../Tereditor.Host.Core/Git.SPEC.md)).

Moves here: every `gh` call (`PullRequests.cs`, `GitHubAuthentication.cs`, the `gh` parts of `BranchStatus.cs`),
`ReviewDetails` and merge-method discovery, the GitHub Settings page, `BranchShip.cs`' pull request half,
`ReviewPull.cs`, `PrDialogs.cs` and `GitHubStatus`, and the protocol's GitHub RPC (`GitHubContract.cs`,
`GitHubRpc.cs`, `GitHubAdapters.cs`), replaced by plugin methods.

Out of scope and unchanged: the `shipping-a-pr` skill and other agent workflows (they instruct an agent to use `gh`
itself; they are not a host feature), GitHub URLs in docs and in the Claude Code marketplace source field, and issue
references in commit messages.

## Data contract

The host sees only the neutral `PullRequest`; GitHub's own strings never cross the boundary.

| field | values |
| --- | --- |
| `number`, `title`, `url` | the pull request's identity; `url` is https or the result is dropped |
| `state` | `open`, `merged`, `closed` |
| `draft` | bool |
| `checks` | `passing`, `failing`, `pending`, `none` |
| `review` | `approved`, `changes-requested`, `required`, `none` |
| `mergeability` | `clean`, `blocked`, `behind`, `conflicting`, `unknown` (GitHub's merge state, never approvals) |
| `mergeMethods` | subset of `merge`, `squash`, `rebase` the repository allows |
| `sameRepository` | false when the head is a fork's branch; the provider refuses actions on it |
| `unpushed`, `behind` | commit counts against the pull request head; -1 when unknown |

The lookup returns the open pull request of the branch, else the newest in any state, else `null`.

## Browsing issues and pull requests

A second, read-only surface next to the repo hosting provider (host half `GitHubBrowser`, UI half `GitHubPane` and `GitHubTab`):

- Every tab of the `github` document scheme wears the GitHub icon (the scheme's registered icon); a tab restored before the plugin registers gets it as soon as it does, because registering a document scheme redraws the tab strip.
- A side tool `github` is only buttons: Issues, Pull requests, Settings. Each opens a tab of the plugin's document scheme `github`
  (ids `issues`, `pulls`, `settings`, `issue:N`, `pull:N`).
- The list tabs show GitHub's layout: Open and Closed counts, a query box that takes GitHub's own search syntax (the host adds
  `repo:` and `is:issue|pr`), rows with state icon, labels, checks, author, age and comment count. A plain click opens the
  conversation in the same tab; right click or Command/Control click opens it in a tab of its own.
- The conversation shows the description, comments, reviews with their inline comments, commits, labels, assignment, close,
  reopen, merge, rename, milestone, reference, review-request, ready, draft, force-push and branch-deleted events, plus a pull
  request's branches, size, merge state and check runs. Up to 500 timeline events load (five pages of 100); the tab says when more exist.
- Everything is `gh api graphql` and `gh repo view`; no OAuth, no token read. Methods `list`, `detail` and `info` take a workspace
  and run `gh` in its folder. A view stays reactive by polling (setting `pollSeconds`, 15 to 600, default 60, while the tab is shown);
  the host keeps a hash of the last answer per view and answers `Unchanged` without the payload.
- Local cache (stale while revalidate, `GitHubBrowser.Cache.cs`, `GitHubCache`): lists, conversations and generic views are saved as the parsed contract records (the source-generated `GitHubJson`; no reflection) with the revision and fetch time, under `<stateDir>/plugin-state/github/files/cache/`, one small JSON file per gh login, repository (the `origin` remote's owner/name, read locally) and kind, keyed inside by the exact request (query, filters, limit, id). A request without a revision (a tab just opened) is answered at once from the saved copy with `Cached` set (`Refreshing`, `FetchedAt`) while one gh call runs and saves its result; the tab asks again, waits for that same call and gets the fresh answer, or `Unchanged`. If gh fails, the saved copy is returned with `Cached.Reason` (the short gh error) and the tab shows "Showing saved data from … reason" with Retry; with no saved copy the answer is the old problem. The tab shows "Updating…" or that line in a fixed-height strip above the content (`GitHubFreshness`), so nothing moves; it redraws as before (scroll offset, drafts and expanded state live in fields, not in the redrawn tree). The gh login comes from `gh api user` (kept 10 minutes), or offline from the active account in `gh auth status`, so another account never reads this one's copy; nothing is cached when the login or the remote is unknown. Bounds: 200 requests per file (oldest fetch goes first), 30 days, 20 MB over all files (oldest entries first). Files are written atomically, owner-only (0600, directory 0700 on Unix), and a file that does not parse or has another format version is deleted, never migrated. Issue and discussion text is stored; credentials and `gh` auth are not. Job logs and lookups of the sign-in are not cached. No conditional requests (ETag): lists and details are GraphQL, which has none, and the REST views already skip unchanged data by revision.
- Setting `keepCache` (default on) and the Clear cache button, with the size, are on Settings › GitHub and the GitHub settings tab; the host method `cache` reports or clears the size. Off: nothing is read or written.
- Bodies and comments render as Markdown through the Kit's `MarkdownPreview`. Issue and pull request lists have Author, Label and Sort chips that edit the query text.
- Link-like click targets (row titles, the Author / Label / Sort and Actions filter chips, the Open and Closed toggles, log group headers) use the Kit's `HoverLink`: plain text colour at rest; underlined and `Ui.Accent` while the pointer is over the row or the control, or while it has keyboard focus (Enter and Space activate a focused row); the row also shows the hover fill and a hand cursor. Timeline actors, branch names, #N references outside Markdown and the Releases, Deployments and Packages rows are not click targets, so they carry no link styling.
- Assignees: the list and detail queries ask for each assignee's login and avatar URL (`GitHubPerson`; `GitHubItem.Assignees`, `GitHubDetail.People`). Rows and the conversation header draw up to three overlapping round avatars and a +N count, with the login as tooltip. Avatars are fetched by the host, never by the UI: the host method `avatar` takes the address, accepts only public https addresses on githubusercontent.com, asks for the 48 pixel size without volatile query parts (`GitHubAvatarUrl`), refuses anything that is not a PNG, JPEG, GIF or WebP of 256 KB at most, and sends no credentials. Images are saved by `GitHubAvatarStore` under `cache/avatars` in the plugin's state folder (one file per address, owner-only, atomic writes, at most 500 files and 8 MB, least recently used first; not saved while `keepCache` is off), so a saved list shows them at once and after a restart; simultaneous asks share one request, and Clear cache removes them and counts them in its size. The UI decodes each address once off the UI thread and keeps 128 decoded images, so scrolling, refreshing and several rows of one person ask nothing again; a failed address waits a minute. A circle shows the login's initial until its image arrives, is replaced in place at the same size, and keeps the initial when the image fails. Label chips use GitHub's colour with black or white text by contrast.
- Layout shared by every tab (`GitHubPage`): a fixed header (title, search box, filter chips) above the one scroller, so the header stays at the top while the list scrolls. A single conversation has no pinned header. Status text (loading, errors) comes from the Kit's `Ui.Status` and sits in the middle of the tab, both ways.
- More tabs, all from the generic `view` method (rows and Markdown cards): Actions (its own page, below), Run pages (below), Discussions (GraphQL, Category filter, question with answer and replies; needs Discussions turned on), Releases (cards with notes and assets), Deployments (GraphQL, latest status) and Packages (REST per package type; `gh` needs the `read:packages` scope, and the tab says so instead of guessing).
- Contributors has its own method `contributors` (GitHub's `stats/contributors` plus the default branch from `gh api repos/{repo}`; `gh api --include` tells 202 and 204 from data, since all answer with an empty body). The host answers `Computing` for a 202 and the tab asks again after 2, 4, 8 and 16 seconds (about 30 in all) under the text "GitHub is computing these numbers..."; a 204 or an empty list is a plain message. A finished answer is kept per repository for the session, and a known revision answers `Unchanged`. The page follows GitHub's: a header with the Period (last month, 3 months, 6 months, year, all time) and Contributions (commits, additions, deletions) menus, a bordered bar chart of the weekly totals (`GitHubBarChart`, drawn on the drawing context: no gaps, dashed grid, values at the right edge, rotated axis title, week labels) with a range selector below (`GitHubRangeSelector`: area chart of the whole history, two handles and a draggable window; dragging changes the window of every chart, cards are re-ranked on release), then a two-column grid of contributor cards (avatar, login link, rank, "N commits  N ++  N --", weekly chart). Contributors with nothing in the window are left out. The page is one virtualized list (header, chart, then rows of two cards), so only rows in view are controls. Avatars load off the UI thread over http and https only, with no credentials, into a 16 MB least-recently-used cache of decoded pictures; a neutral circle shows while loading and when a load fails.
- Links in rendered Markdown: the Kit's `MarkdownContext.OpenLink` is offered each link first. Links to this repository's issues, pull requests, discussions, runs and releases (`GitHubLinks.Parse`) open the matching tab (same tab from a list tab, else the document is opened or focused); the Markdown view reports no right click or Command click, so there is no new-tab variant. Bare #N becomes an issue link outside code. Other http and https links open in the system browser through the preview; other schemes never open. Images load in the preview off the UI thread (http and https only, 8 MB at most, no credentials sent) and show their alt text when they fail; they are not cached.
- Job logs: a run page (`run:ID`) shows its jobs on the left (status icon, duration) and the selected job's steps on the right (chevron, name, status icon, duration). A click opens or closes a step and loads its log (`gh api repos/{repo}/actions/jobs/{id}/logs`, one request per job, cached by id once the job is done). The host splits the log into steps by the job's step start times (a line belongs to the last step that began at or before its time) and keeps `##[group]` headers, `##[error]` marks and colour codes in the lines. Expand all and Collapse all act on the selected job; failed steps start open. What is open lives in a state object keyed by run, job and step (groups: plus the header line), so polls and redraws keep it. Running jobs show "Not ready yet" per step with the step time so far. Memory bound: at most 5000 lines per step and 20000 per job are kept (the tail of a step wins, the earliest steps lose first), the view says how many were cut, and Open full log on GitHub opens the job page.
- Actions tab (`runs`, `GitHubActionsTab`): GitHub's layout. A sidebar with its own scroll (All workflows, the repository's workflows from `gh api repos/{repo}/actions/workflows` with disabled ones muted, a collapsible Management group that opens GitHub pages, Collapse sidebar), then a pinned header (title, subtitle, New workflow, search) and a pinned table header (count, auto-refresh 30s, 60s or off, and the Workflow, Event, Status, Branch and Actor dropdowns) above the run list. A workflow filter reads `actions/workflows/{id}/runs`. The runs API filters by `branch:`, `event:`, `actor:`, `status:`, `created:` and `head_sha:` only; it has no title or free-text search, so other words are matched by the host against the title, workflow, branch and actor of each page that comes back. Re-run is not offered: it needs the `repo` write scope and the run id does not fit the post contract.
- Virtualization and paging: the run list and each step's log lines are `ItemsControl`s over a `VirtualizingStackPanel`, so only the rows in view (and a small buffer) are controls. Their data is light records (strings and numbers; no controls, no Markdown). The host pages runs (`GitHubViewParams.Page`, 30 per page); the next page loads when the list scrolls within 400 px of its end, with a Loading more row, and at most 500 runs are kept before the list says to refine the search. A poll reads page one only and diffs by run id, so a changed run replaces one row. A log step is one fixed-height viewport (about 480 px) with its own scroll; colour codes are parsed per visible line. There are no avatars or images in these lists, so no image cache is needed.
- Commenting (a click only): issue, pull request and discussion tabs end with a composer (Write and Preview, Comment, and Close or Reopen with an optional comment; Ctrl/Cmd+Enter comments). The host posts with `gh api repos/{repo}/issues/{n}/comments`, `PATCH .../issues/{n}` for the state and the `addDiscussionComment` mutation. A failure keeps the text and shows gh's line; a scope error adds `gh auth refresh -s repo` (`write:discussion` for discussions). A posted comment is added at once and dropped when a refresh returns the same id. Drafts live per tab in `GitHubDrafts`, so a refresh keeps them (focus in the box may reset when a changed view redraws).
- gh cannot serve: reactions, the Projects and Security tabs, conditional ETag requests through `gh api` (each poll is one call; the host answers Unchanged by hashing), avatar images, and package versions.

## Checks of commits for the Git Graph

`RepoHostProvider.CommitChecksAsync` is the one optional member the Git Graph reads, through core's `IPluginHostContext.CommitChecksAsync`
(no plugin dependency; GitHub off means no provider and an empty answer). `GitHubCommitChecks` answers it with one `gh api graphql` call per
50 commits, aliasing `object(oid:)` per commit and reading `statusCheckRollup.contexts` (`totalCount`, `checkRunCountsByState`,
`statusContextCountsByState` and the first 30 nodes for names). Failure counts failures, timeouts and required actions; queued, waiting
and running count as pending; success, neutral and skipped count as passed; cancelled leaves it neutral. Results are kept per commit in memory:
finished ones for ten minutes, running ones for 30 seconds, commits without checks for two minutes. After a rate limit or any failed call
(403, 429, not signed in, no network) it asks nothing for two minutes, doubling up to 30, answers only what it holds and logs once.
`TEREDITOR_GH_OFFLINE=1` and non-GitHub remotes answer nothing.
Each commit's link is its workflow run (`https://github.com/{repo}/actions/runs/{id}`, read from each check run's `checkSuite.workflowRun.databaseId`): the run of the first
check that failed or is still running, else the first run; a commit with no workflow run (only statuses or other apps) links its checks page. The UI half's
`RepoHostUI` registration has an `OpenLink` that opens a run, issue, pull request, discussion or releases link in the plugin's own `github` tab (a run as `run:{id}`) and answers
false for anything else, so Git Graph's CI icon lands on the Actions tab (core routes through `OpenRepoHostLinkAsync`) and falls back to the browser.

## Failure and timeouts

Everything runs on pool threads; the UI thread only applies results after checking the workspace and branch still
match (AGENTS.md). A query has the 8 second budget and kills the child's process group; a mutation has the
5 minute deadline. A missing or unauthenticated `gh`, a timeout, a non-https link or an exception reads as "no pull
request" for lookups (logged against the plugin, never cached) and as a `Problem` of `missing` or `unauthenticated`
for mutations, which the host turns into the compare-link outcome with the setup guidance the UI half draws.

## Settings and migration

The plugin has two settings beyond `enabled`: `pollSeconds` and `keepCache` (above); the old Settings › GitHub page held none. The merge dialog's
remembered method and branch-deletion choice per project (today the UI profile's `MergePreferences[projectRoot]`, the string `method|delete` or `method|keep`) moves to the plugin's state
(H14) on first run; an unreadable old value falls back to the dialog defaults. Profiles without an explicit
`plugins.github.enabled` get the plugin on.

## Switching off

Switching `GitHub` off (plugin enable switch, no dependents to cascade) removes only its segment, Review section,
Settings page and the `gh` calls; the host drops the cached lookups and reports no review. Remote, Publish, Push, Sync,
branch status and Open PR's push step keep working; Open PR then pushes and reports `pushed`. With it off, a
GitHub remote is simply a remote.

## Limits

- Only `github.com`; no Enterprise Server.
- Needs `gh` on the host's PATH; the browser never runs it (the host probes authentication, as before).
- Fix with agent, templates, description sources and Merge locally stay unported.

## Implementation status

Implemented in strangler slices (host seam, plugin projects, UI half, wire): `GitHubProvider` runs every `gh` call through
the host's bounded runner; the Review section is the W28 registration (`RepoHostUI`, over the
window's `IRepoHostSurface`; the section is not a W23 slot because it needs the window surface); the Settings page is a W4
section reading sign-in through the plugin's `signIn` method when shown and every 30 s; the wire is protocol 15 (neutral
`PullRequest` in field 14, provider = plugin id). Category is Version Control. Checks: `--github-browser` (list, counts, query, conversation, unchanged polling, failures, over canned gh output), `--github` (provider, cache, failures, plugin off = plain remote), `--branch-status`, `--review`,
`--settings`, `--shell`. Open: the merge dialog's remembered choice still lives in the UI profile (`MergePreferences`,
`method|delete` or `method|keep`) behind the surface rather than in plugin state (H14); the Settings page does not follow a host
broadcast; no `--plugins` case switches the plugin off through the registry.
