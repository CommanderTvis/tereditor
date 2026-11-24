---
id: module-plugin-chat
type: module-design
status: active
title: Chat (shared contract and view)
parent: group-plugins-agents
---

# Chat (shared contract and view)

The conversation records and the drawn chat view that an agent plugin's chat mode uses. It has no plugin of its own: each agent plugin
serves the wire contract from its host half and registers its own editor document for the view. The Claude Code plugin is the only
one today; the seams below are what a second agent protocol (the Agent Client Protocol, ACP, is the intended one) plugs into.

## Boundary

- `Tereditor.Plugins.Chat` owns `ChatContract` (items, header, push kinds, the methods and the `chatUpdates` state channel) and `Ack`.
  It depends only on the Plugin API. A plugin lists `ChatContract.Methods` and `ChatContract.Updates` in its own `PluginContract`, so
  every method keeps the plugin's id as its namespace on the wire.
- `Tereditor.Plugins.Chat.UI` owns `ChatView`, the transcript list, composer, pickers and dialogs (internal; visible to the Claude Code
  UI project and the checks), `ChatDocuments` (registering a plugin's chat document, opening chats) and `ChatAgent`.
- A `ChatAgent` is how a plugin describes itself to the view: display and short name, document scheme, icon, the terminal agent kind (so a
  session a terminal already runs is not resumed in a chat), a help link, the end of the "host has no chat handler" message, effort levels,
  `PickersWhenOffered` (the model and mode chips show only once the agent has listed something; ACP sets it), and the permission modes it offers with their words (`Modes`, `DescribeMode`). `ChatMode.Dangerous` marks a mode offered only while the
  session already runs in it.
- Chat rows read inline Markdown with their own scanner (`Markdown.Inline`), not the kit preview. Inline styles compose: code inside bold, italic, strikethrough or link text keeps its monospace face with the weight, slant and decorations around it, and no marker is copied.
- Allowed dependencies: Chat -> Api; Chat.UI -> Chat, Api.UI, Kit, Agent.UI. Plugins depend on Chat (and their UI on Chat.UI); the host
  core, UI, Android, Browser and the checks reach it through them. The host never depends on Chat.UI.
- The launcher (which presets start a chat, the resume menu) stays in the plugin: it knows the agent's command line, the view does not.

## Semantics

The semantics of the records and the view are those of the Claude Code chat (`src/Tereditor.Plugins.ClaudeCode/SPEC.md`, "Chat mode"
sections). A host that cannot do something the contract offers (queue editing, send-now on a queued message, rewind, fork, permission requests) answers that method with
an error and never produces the items that would make the view offer it: without the header flag `CanSendQueuedNow` there is no Send now button, with no message `Uuid` there are no rewind or fork buttons, and
with no permission or question items there is nothing to approve. The view's wording is neutral where it cannot know the agent
("Assistant"); anything else comes from the `ChatAgent`.

## Bash mode

A host that can run a user's shell command sets the header flag `CanRunShell` and serves `chatRunShell` and `chatStopShell`; the composer offers
bash mode only then (a leading `!` is plain text otherwise). `chatRunShell` starts the command and returns at once; the host streams one
`ChatNoticeItem` of category `bash-output` whose `Command` is the command, `Running` says whether it still runs, and whose `Output`,
`Error output` and `Exit code` sections hold what it printed (each cut at `ChatContract.OutputLimit` with a note naming what was left out) and,
for a failure, the exit code (`stopped` when the user ended it). `chatStopShell` ends the running command. Only one command of a chat runs at a time.
The finished cards are not a turn: the host carries them in front of the user's next message.

## Failure card action

A `ChatNoticeItem` of category `failure` may carry an `Action` (`ChatNoticeAction`: a `Label` and a `Command`). The view draws it as a button on the card. Pressing it opens a terminal that runs the command, for example a sign-in the user starts. Android and the browser draw no button, because they have no terminal to open. A card with no `Action` is unchanged.

## Extension UI and one-mode hosts

A host can show what an agent's extensions ask or set. A text question without options (`ChatQuestion.Placeholder`, `Prefill`, `Multiline`) is
a text card; an empty text is a valid answer. `ChatHeader.Statuses` become footer chips and `ChatHeader.Widgets` text blocks above or below the
composer, one per key. When a host lists exactly one permission mode there is nothing to choose, so the view draws no permission picker; two or
more show it.

## Side questions

A host may answer `/btw <question>` itself: it shows a `ChatNoticeItem` of category `side-answer` whose `Label` is the question, `Text`
the answer (Markdown), `Running` true while it is answered and `Level` Error when it failed, in plain words. It is no turn: the agent never
sees it, nothing is added to the conversation, a revival does not bring it back, and the running turn goes on. The view draws it as a tinted
card apart from the conversation, with the question, the answer, a note that it is not added, and Dismiss, which calls `chatDismissSide`
with the item id; the host drops the item and cancels a question still being answered. `ChatContract.SideQuestion` says whether a text is one
(a leading `/btw` alone or followed by whitespace); the view sends such a text as typed, without the editor-selection mention in front.

## Mention completion

`Files` searches the workspace for an `@` query. `CompletePath` (`ChatPathParams`: workspace, chat, the text as typed) lists one directory for a path query (`/`, `~`, `./`, `../`, a drive): directories first, then files, filtered by the last segment, at most 50, resolved against the chat's working directory and the host user's home; a missing directory is an empty list. A host that cannot serve it fails the call and the view uses `Files`. The method is additive, so the host protocol version does not change.

## Pasted text

A text paste of more than 10 lines or more than 1,000 characters goes into the composer as one token, `[Pasted text #N +K lines]`
(`+1 line` for one), and the view keeps the text beside the draft (per chat, client-local, like the local draft copy). N counts the
draft's big pastes from 1 and starts again after a send; K is the paste's line count. A send replaces every token by its text, so the
agent and the transcript get what was pasted (the whole message is trimmed, as before), and history recalls that text. The token is one unit: Backspace after it and Delete
before it remove it whole, and a caret moved into it goes on to its edge (a selection is left alone). A token edited by hand no longer
matches and is plain text. Cmd+Shift+V (Ctrl+Shift+V), and "Paste as plain text" in the box's menu, paste the text itself; "Expand
pasted text" in that menu puts back the token at the caret, or every token when the caret is on none. Bash mode always pastes plainly.
Press the paste shortcut again (Cmd+V, or Ctrl+V on Windows and Linux; Shift+Insert and the menu's Paste count too) within 3 seconds
of a paste that became a token, and the token is replaced by the full text. This only works while the box is as that paste left it: the
caret still sits at the end of the token, nothing was typed or moved, and the clipboard holds the same text. Any other key, edit or
caret move ends the window. While it is open the paste note says "Press Cmd+V again to paste all N lines" and names Cmd+Shift+V; the hint
is not shown on Android, which has no paste key.
The shared draft carries the expanded text, so a host restart or another client shows the pasted text in full; a rebuilt view in the same
process keeps the tokens.

## Image pointers

A pasted, dropped or picked image goes into the composer as one token at the caret, `[Image #N]`, with a space on each side that needs
one. The view keeps the image (bytes, media type, path) beside the draft, per chat and client-local, and the token is the only handle:
an image is sent exactly when its token is in the text. N counts the draft's images from 1 and starts again after a send; a new number
goes past every `[Image #N]` already in the box, so a token recalled from history is never confused with
a new one. Backspace and Delete next to a token remove it whole, and a caret moved into it goes on to its edge. Deleting the token, or
the close mark of the image's preview chip (still drawn, with its thumbnail), drops the image; Undo that brings the token back brings the
image back too, because the view keeps it until the send. A token typed by hand with no image behind it is plain text. A file image is
read off the UI thread and the send waits for it. A PDF is still a chip. In bash mode an image is refused with a note.
The text keeps the pointer: the agent gets `[Image #1]` in the prompt and the image as a content block (Claude Code `image` block,
ACP `image` block, Pi `images` entry), and the attachment is named "Image #N", so the transcript bubble shows the pointer in the
text and draws no second chip for it.

Up or Down recall of a sent message brings back its text and its images: its `[Image #N]` tokens resolve to the images it was sent
with, under the same numbers, and the send carries them again. The draft's own images wait beside it and come back on Esc or on Down past
the newest entry, so a draft's image never answers a recalled token. Images live in memory only: they are not written to the project or to
host state (the persisted recent prompts keep text alone), so after a restart a recalled token is plain text. Kept images stay under 64 MB
across all chats, the oldest dropped first. A pointer whose image is gone recalls as plain text, and the composer's note says so.

## Shared draft

The composer's unsent text is host state, so one user on several clients (a desktop and a phone) sees and edits the same draft. It is
text only: no carets, selections or typing indicators are shared, and two people are never assumed to type at once. Attachments are not
shared: they may be bytes held in the client's memory (`ChatAttachment.Data`, up to 8 MB), so each client keeps its own and the draft
record's `Attachments` stays null (reserved for path-only attachments later).

- Record. `ChatDraft(Text, Revision)` with `Attachments` (reserved, null) and `Origin`. `Revision` is a monotonic long per chat that
  never goes back, also across a clear and a host restart; revision 0 is "the host has never stored a draft for this chat".
- Reading. The `chatSnapshot` list starts with a `ChatDraftPush` (kind `draft`: workspace, chat, draft), also for a chat whose child is not running; later changes
  arrive on `chatUpdates` as the same push. A host that does not serve the draft answers `chatSetDraft` with an error and never pushes one;
  the view then keeps its draft client-locally (a static per-process map) and stops syncing.
- Writing. `chatSetDraft(ChatSetDraftParams: workspace, chat, BaseRevision, Text, ClientId)` returns the new `ChatDraft`, whose `Origin`
  is the `ClientId`. Today the host is last-writer-wins on the whole text: it ignores `BaseRevision` for ordering, assigns the next
  revision and broadcasts, unless the text equals the current one, when it changes nothing and returns the current record. Text over
  100,000 characters is refused. A client ignores a push whose `Origin` is its own id (the reply already told it the revision) and a
  push whose revision is not newer than the highest it knows.
- Reserved extension (not implemented). Host-ordered edit operations replace last-writer-wins without changing clients that already send
  `BaseRevision` and read `Revision`: `ChatSetDraftParams` gains an optional `Edit` (`replace range [Start, End) with Text, against
  BaseRevision`); the host transforms it over the edits applied since `BaseRevision`, applies it, and the push carries the edit with the
  new revision. A client that sends whole `Text` keeps working because the host treats it as one replace of the whole range.
- Persistence. The Claude Code host keeps the drafts in the plugin's user state (see the Claude Code SPEC), bounded and saved after a short
  debounce; a client reload, another device or a host restart shows the draft again. It is cleared when the message is sent and when the
  chat's workspace is removed.
- Send. `ChatSendParams` carries `DraftRevision` and `ClientId`. The host claims the revision atomically: a send whose `DraftRevision` is
  at or below the revision a previous send already consumed is refused with `ChatContract.AlreadySent` and sends nothing; otherwise it
  sends what the client typed (also from a stale revision), and then clears the draft and pushes an empty draft with a new revision. A
  send that fails releases the claim and keeps the draft. A send without `DraftRevision` (a launcher prompt, a queued edit) neither
  checks nor clears.
- Client apply rule. The client sends its text about 250 ms after the last keystroke, on focus loss, on detaching and before a send, with
  the async request, never on the UI thread's I/O. A remote draft replaces the input only when the input equals the text the client last
  sent or applied and nothing is in flight; the caret keeps its logical place (at the end stays at the end). Unsent or in-flight local text
  wins for that moment and is sent again as the next revision. While the input has focus and the user typed within the last 400 ms the
  replacement waits (the text box exposes no composition state cheaply), then applies only if the input is still clean. History recall
  (Up/Down) and its stash make the input differ from the last sent text, so a remote draft never replaces a recalled prompt.
  Input history is one state machine (`InputHistory`, per chat, kept across tab switches): Up recalls only from the first line and Down only
  from the last; on a draft or an edited recalled entry the first Up only moves the caret to the start, a second recalls; the draft (text and
  selection) is stashed and comes back on Down past the newest entry or on Esc; editing a recalled entry never touches the stash; sending clears it.
  "First" and "last" line are the text layout's visual lines, so soft-wrapped lines count. When not browsing, Up on the first line with the caret
  after the start moves the caret to the start (then recalls, if there is history), and Down on the last line with the caret before the end moves
  the caret to the end; elsewhere Up and Down are plain caret movement, one line.
  The view
  shows a quiet note under the input when a send is refused as already sent.

## Serving another agent protocol (ACP)

The ACP plugin (`src/Tereditor.Plugins.Acp/SPEC.md`, Early support) implements this mapping; the gaps listed below are the ones it lives with.

An ACP agent needs no view change: its plugin supplies a `ChatAgent` and a host half that serves `ChatContract` over the ACP connection
(stdio JSON-RPC to the agent process). The mapping:

| ACP | ChatContract |
| --- | --- |
| `initialize`, `session/new`, `session/load` | `Open` (new, resume by `ResumeSessionId`); `Sessions` lists loadable sessions; capabilities decide which items and methods exist |
| `session/prompt` turn | `Send`; `ChatStatus` Running for the turn, Idle on its stop reason (`end_turn`), a Failure notice for `refusal` or an error; `session/cancel` is `Interrupt` |
| `agent_message_chunk` | a `ChatAssistantItem` with `Streaming` growing in place by id, then final |
| `agent_thought_chunk` | `ChatThinkingItem` |
| `user_message_chunk` (replay on load) | `ChatUserItem` |
| `tool_call` / `tool_call_update` | `ChatToolItem` keyed by `toolCallId`: `kind` and `title` give `Name` and `Summary`, `status` maps to `ChatToolState`, content and locations to `Output` and `FilePath`; `ParentId` comes from Claude Code's adapter meta (`_meta.claudeCode.parentToolUseId`; see the ACP plugin spec) and is null otherwise |
| `session/request_permission` | `ChatPermissionItem` (options become the answers, `RequestId` the JSON-RPC id), answered with `Answer`; the call stays open until answered |
| `plan` | an assistant item or a notice; the contract has no plan record |
| `available_commands_update` | `ChatHeader.Commands` |
| session modes and `current_mode_update` | `ChatHeader.PermissionModes` and `PermissionMode`, set by `SetPermissionMode`; the words come from the agent's `ChatAgent.DescribeMode` or from the mode names the agent sends |
| session models and the model config option | `ChatHeader.Models` and `Model`, set by `SetModel` |
| other config options | `ChatHeader.Options` (select or boolean, with the agent's labels and descriptions), set by `SetOption`; the method is not in `ChatContract.Methods`, only a host that offers options serves it |
| `usage_update` | `ChatHeader.Usage` |

The ACP client methods the agent calls (`fs/read_text_file`, `fs/write_text_file`, `terminal/*`) are served by the host half from the
workspace, never by the view.

### ChatContract gaps for ACP

- Permission options: `ChatPermissionItem.Options` lists `{id, label, kind}` as the agent sent them. When it is not empty the card shows exactly
  those answers and `ChatAnswerParams.OptionId` names the chosen one; `Allow` only repeats whether its kind is an allow. Claude Code leaves
  `Options` empty and keeps its own three answers. `ToolKind`, `Locations` and `Diffs` carry what the call is. ACP has no rejection reason, so such a card has
  no reason box.
- Plans: no record for an agent's task plan with entry status; today it is text.
- Content blocks: user and tool content are text, images and documents (`ChatAttachment`); ACP resource links, embedded resources and
  audio have no record. Tool diffs (`Diff` content) reach a tool card only as `FilePath` and `-`/`+` lines of text; a permission card has the old and new text.
- Tool kinds: tool cards pick their layout by Claude Code tool name (Bash, Edit, Read, Grep, ExitPlanMode...). An ACP tool has a `kind`
  (read, edit, execute, search, fetch...) but its own title; the card falls back to the generic layout until the kind drives it.
- tereditor's own MCP tools (server `plugin_tereditor_tereditor`, `tereditor` or `plugin:tereditor:tereditor`) read "tereditor [LSP navigate]" with a short line from the call's input (operation and query, first path, search query, title, id, description); the long `workspace` argument is never shown, in the header or the expanded IN row.
- Header fields that only Claude Code has (`Chrome`, `ChatQuestionItem`'s AskUserQuestion shape, rewind and fork by message uuid,
  `StopTask`) are simply never set by another host; ACP has no equivalent of rewind or fork except `session/fork` where offered.
- Session list: `ChatSessionInfo` has id, title and time, which fits `session/list`; paging and `cwd` filtering are not modeled.
- Model and mode discovery assume the header carries the lists; ACP's session config options (with categories and values) would need
  the host half to flatten them into models, effort and modes.
- Authentication (`authenticate`, agent auth methods) has no contract method; a host half would surface it as a Failure notice.
- A chat tab registers its document with `HostsCompanions`, so the app embeds companion panes (the `visualize` live view, for one) beside the conversation as it does for a terminal; the chat view itself knows nothing of them.

## Checks

The view is covered by `--claude-chat-ui` and `--claude-chat-e2e` (two views of one host for the shared draft), the records and the Claude Code host by `--claude-chat-host`, and the
project and dependency rules by `--conformance` (the boundary table).
