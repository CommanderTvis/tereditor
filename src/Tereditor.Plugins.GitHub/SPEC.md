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
  the host keeps a hash of the last answer per view and answers `Unchanged` without the payload. Nothing from GitHub is stored.
- Bodies and comments render as Markdown through the Kit's `MarkdownPreview`. Issue and pull request lists have Author, Label and Sort chips that edit the query text.
- Link-like click targets (row titles, the Author / Label / Sort and Actions filter chips, the Open and Closed toggles, log group headers) use the Kit's `HoverLink`: plain text colour at rest; underlined and `Ui.Accent` while the pointer is over the row or the control, or while it has keyboard focus (Enter and Space activate a focused row); the row also shows the hover fill and a hand cursor. Timeline actors, branch names, #N references outside Markdown and the Releases, Contributors, Deployments and Packages rows are not click targets, so they carry no link styling.
- Assignees: the list and detail queries ask for each assignee's login and avatar URL (`GitHubPerson`; `GitHubItem.Assignees`, `GitHubDetail.People`). Rows and the conversation header draw up to three overlapping round avatars and a +N count, with the login as tooltip. Avatars load like Markdown images (http and https only, 1 MB at most, no credentials, off the UI thread, a small in-memory cache) and show the login's initial until they arrive or when they fail. Label chips use GitHub's colour with black or white text by contrast.
- Layout shared by every tab (`GitHubPage`): a fixed header (title, search box, filter chips) above the one scroller, so the header stays at the top while the list scrolls. A single conversation has no pinned header. Status text (loading, errors) comes from the Kit's `Ui.Status` and sits in the middle of the tab, both ways.
- More tabs, all from the generic `view` method (rows and Markdown cards): Actions (runs with Event, Status, Branch and Actor filters over `gh api`; a run opens to its jobs and steps; in-progress runs refresh by the same polling), Discussions (GraphQL, Category filter, question with answer and replies; needs Discussions turned on), Releases (cards with notes and assets), Contributors (commits on the default branch), Deployments (GraphQL, latest status) and Packages (REST per package type; `gh` needs the `read:packages` scope, and the tab says so instead of guessing).
- Links in rendered Markdown: the Kit's `MarkdownContext.OpenLink` is offered each link first. Links to this repository's issues, pull requests, discussions, runs and releases (`GitHubLinks.Parse`) open the matching tab (same tab from a list tab, else the document is opened or focused); the Markdown view reports no right click or Command click, so there is no new-tab variant. Bare #N becomes an issue link outside code. Other http and https links open in the system browser through the preview; other schemes never open. Images load in the preview off the UI thread (http and https only, 8 MB at most, no credentials sent) and show their alt text when they fail; they are not cached.
- Commenting (a click only): issue, pull request and discussion tabs end with a composer (Write and Preview, Comment, and Close or Reopen with an optional comment; Ctrl/Cmd+Enter comments). The host posts with `gh api repos/{repo}/issues/{n}/comments`, `PATCH .../issues/{n}` for the state and the `addDiscussionComment` mutation. A failure keeps the text and shows gh's line; a scope error adds `gh auth refresh -s repo` (`write:discussion` for discussions). A posted comment is added at once and dropped when a refresh returns the same id. Drafts live per tab in `GitHubDrafts`, so a refresh keeps them (focus in the box may reset when a changed view redraws).
- Job logs: in a run, a job row expands to its log (`gh api repos/{repo}/actions/jobs/{id}/logs`), split by `##[group]` into collapsible groups (those with an error open first, failed jobs expand by themselves), line numbers, dimmed times, colour codes stripped. At most 500 lines per group and 4000 in total are kept (the tail wins); the view says how many were cut and offers Open full log on GitHub. A running job answers that its log is not ready; a finished job's log is cached by id.
- gh cannot serve: reactions, the Projects and Security tabs, conditional ETag requests through `gh api` (each poll is one call; the host answers Unchanged by hashing), avatar images, and package versions.

## Failure and timeouts

Everything runs on pool threads; the UI thread only applies results after checking the workspace and branch still
match (AGENTS.md). A query has the 8 second budget and kills the child's process group; a mutation has the
5 minute deadline. A missing or unauthenticated `gh`, a timeout, a non-https link or an exception reads as "no pull
request" for lookups (logged against the plugin, never cached) and as a `Problem` of `missing` or `unauthenticated`
for mutations, which the host turns into the compare-link outcome with the setup guidance the UI half draws.

## Settings and migration

The plugin has no settings of its own beyond `enabled`; the old Settings › GitHub page held none. The merge dialog's
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
