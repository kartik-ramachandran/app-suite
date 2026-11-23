// Shared vocabulary for the Gamefield — the pixel-art office floor that any
// agent-bearing page can flip into. Pages map their own run state onto this
// model; the renderer knows nothing else about them.

export type AgentStatus =
  | 'idle'      // at desk, nothing assigned
  | 'walking'   // moving to a workstation (renderer-driven, pages rarely set this)
  | 'working'   // actively executing
  | 'blocked'   // waiting on approval / input
  | 'done'      // finished successfully
  | 'failed'    // errored

/**
 * Something the viewer can do from the floor. The whole point of the field view
 * is that it replaces the panel, so anything the panel let you press has to be
 * pressable here too.
 */
export interface FieldAction {
  id: string
  label: string
  /** Bootstrap icon class, e.g. 'bi-play-fill'. */
  icon?: string
  onClick: () => void
  tone?: 'primary' | 'default' | 'danger'
  disabled?: boolean
}

export interface FieldAgent {
  /** Stable across renders — desk assignment and walk state key off this. */
  id: string
  /** Nameplate on the desk. */
  name: string
  /** Small line under the nameplate, e.g. the tool or model it runs. */
  role?: string
  status: AgentStatus
  /** 0..1, drives the desk progress bar. Omit for indeterminate work. */
  progress?: number
  /** Short line shown in the speech bubble while working. */
  detail?: string
  /** Full text surfaced in the inspector when the avatar is clicked. */
  output?: string
  /** Renders in the corner office and adjudicates — the orchestrator/planner. */
  boss?: boolean
  /** Hex accent for the desk lamp and nameplate. Defaults to a hash of `id`. */
  accent?: string
  /** Buttons offered in the inspector, e.g. approve/skip on a blocked step. */
  actions?: FieldAction[]
}

/** An in-flight hand-off. Drawn as an envelope flying desk to desk. */
export interface FieldMessage {
  id: string
  /** Agent id, or the pseudo-ids 'user' (the door) and 'inbox' (the in-tray). */
  from: string
  to: string
  label?: string
}

export interface FieldEvent {
  id: string
  text: string
  tone?: 'info' | 'good' | 'bad' | 'warn'
  /** Epoch ms; defaults to arrival order. */
  at?: number
}

export interface FieldModel {
  /** Shown on the floor's wall sign. */
  title: string
  agents: FieldAgent[]
  messages?: FieldMessage[]
  events?: FieldEvent[]
  /** True while any work is in flight — drives ambient office motion. */
  running?: boolean
  /** Optional line under the title, e.g. the goal being worked. */
  subtitle?: string
}

export const ACTIVE_STATUSES: AgentStatus[] = ['walking', 'working', 'blocked']

export function isActive(a: FieldAgent): boolean {
  return ACTIVE_STATUSES.includes(a.status)
}
