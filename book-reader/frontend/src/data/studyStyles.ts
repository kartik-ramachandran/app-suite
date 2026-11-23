// The 50 Study Studio learning and visual styles. Mirrors the backend catalog
// (StudyStyleCatalog.cs) — the backend owns the prompts; this file owns the
// gallery metadata so no fetch is needed to draw the picker.

export interface StudyStyleMeta {
  id: string
  label: string
  group: string
  icon: string
  archetype: 'notes' | 'board' | 'sheet' | 'diagram' | 'mindmap' | 'timeline' | 'venn' | 'matrix' | 'cards' | 'worksheet'
  blurb: string
}

export const STUDY_GROUPS = [
  'Notes & summaries',
  'Diagrams & visuals',
  'Practice & recall',
  'Deep understanding',
  'Business & strategy',
  'Software & systems',
] as const

export const STUDY_STYLES: StudyStyleMeta[] = [
  { id: 'handwritten', label: 'Handwritten notes', group: 'Notes & summaries', icon: 'bi-pencil', archetype: 'notes', blurb: 'Notebook-style study notes with highlights and margin cues.' },
  { id: 'notebook', label: 'Notebook page', group: 'Notes & summaries', icon: 'bi-journal-richtext', archetype: 'notes', blurb: 'The viral study-gram look: highlights, doodles, example callouts.' },
  { id: 'sticky-board', label: 'Sticky-note board', group: 'Notes & summaries', icon: 'bi-sticky', archetype: 'board', blurb: 'A wall of colourful post-its — one idea per note.' },
  { id: 'cheat-sheet', label: 'Cheat sheet', group: 'Notes & summaries', icon: 'bi-grid-3x3-gap', archetype: 'sheet', blurb: 'A dense boxed one-pager to print and keep.' },
  { id: 'cornell', label: 'Cornell notes', group: 'Notes & summaries', icon: 'bi-layout-sidebar', archetype: 'notes', blurb: 'The classic cue-column + notes + summary format.' },
  { id: 'study-plan', label: 'Study plan', group: 'Notes & summaries', icon: 'bi-calendar-check', archetype: 'notes', blurb: 'A day-by-day plan to actually learn this.' },

  { id: 'mind-map', label: 'Mind map', group: 'Diagrams & visuals', icon: 'bi-diagram-2', archetype: 'mindmap', blurb: 'The topic radiating into colour-coded branches.' },
  { id: 'concept-map', label: 'Concept map', group: 'Diagrams & visuals', icon: 'bi-diagram-3', archetype: 'diagram', blurb: 'Ideas connected with labelled relationships.' },
  { id: 'flowchart', label: 'Flowchart', group: 'Diagrams & visuals', icon: 'bi-signpost-split', archetype: 'diagram', blurb: 'A process or decision path, step by step.' },
  { id: 'timeline', label: 'Timeline', group: 'Diagrams & visuals', icon: 'bi-clock-history', archetype: 'timeline', blurb: 'Events or stages in order, at a glance.' },
  { id: 'venn', label: 'Venn comparison', group: 'Diagrams & visuals', icon: 'bi-circle-half', archetype: 'venn', blurb: "What's unique to each and what overlaps." },
  { id: 'matrix', label: 'Comparison matrix', group: 'Diagrams & visuals', icon: 'bi-table', archetype: 'matrix', blurb: 'Feature-by-feature table across options.' },
  { id: 'infographic', label: 'Infographic page', group: 'Diagrams & visuals', icon: 'bi-bar-chart', archetype: 'sheet', blurb: 'Big numbers, icon bullets, scannable sections.' },
  { id: 'learning-roadmap', label: 'Learning roadmap', group: 'Diagrams & visuals', icon: 'bi-map', archetype: 'diagram', blurb: 'What to learn, in what order, to master this.' },
  { id: 'cycle', label: 'Cycle diagram', group: 'Diagrams & visuals', icon: 'bi-arrow-repeat', archetype: 'diagram', blurb: 'A repeating loop with clear stages and feedback.' },
  { id: 'hierarchy', label: 'Hierarchy tree', group: 'Diagrams & visuals', icon: 'bi-diagram-3', archetype: 'diagram', blurb: 'Levels, ownership, categories, or reporting structure.' },
  { id: 'pyramid', label: 'Pyramid breakdown', group: 'Diagrams & visuals', icon: 'bi-triangle', archetype: 'diagram', blurb: 'A layered progression from foundation to peak.' },
  { id: 'funnel', label: 'Funnel analysis', group: 'Diagrams & visuals', icon: 'bi-filter', archetype: 'diagram', blurb: 'How a broad input narrows through successive stages.' },
  { id: 'cause-effect', label: 'Cause & effect', group: 'Diagrams & visuals', icon: 'bi-bezier2', archetype: 'diagram', blurb: 'Root causes grouped around one important outcome.' },
  { id: 'decision-tree', label: 'Decision tree', group: 'Diagrams & visuals', icon: 'bi-signpost-split', archetype: 'diagram', blurb: 'Choices, conditions, and outcomes in branching form.' },
  { id: 'relationship-map', label: 'Relationship map', group: 'Diagrams & visuals', icon: 'bi-share', archetype: 'diagram', blurb: 'A central idea and the forces connected to it.' },
  { id: 'process-steps', label: 'Process steps', group: 'Diagrams & visuals', icon: 'bi-arrow-right-square', archetype: 'timeline', blurb: 'A polished sequence of phases, actions, and handoffs.' },

  { id: 'flashcards', label: 'Flashcards', group: 'Practice & recall', icon: 'bi-collection', archetype: 'cards', blurb: 'Flip cards for active recall.' },
  { id: 'quiz', label: 'Practice quiz', group: 'Practice & recall', icon: 'bi-patch-question', archetype: 'worksheet', blurb: 'Multiple-choice test with explanations.' },
  { id: 'fill-blanks', label: 'Fill in the blanks', group: 'Practice & recall', icon: 'bi-input-cursor-text', archetype: 'worksheet', blurb: 'A worksheet with an answer key.' },
  { id: 'mnemonics', label: 'Mnemonics pack', group: 'Practice & recall', icon: 'bi-magic', archetype: 'sheet', blurb: 'Acronyms, rhymes and memory hooks.' },
  { id: 'recall-drill', label: 'Active recall drill', group: 'Practice & recall', icon: 'bi-arrow-repeat', archetype: 'worksheet', blurb: 'Open questions to quiz yourself — the fastest way to learn.' },
  { id: 'exam-predictor', label: 'Likely exam questions', group: 'Practice & recall', icon: 'bi-file-earmark-check', archetype: 'worksheet', blurb: 'The questions most likely to come up — with full-mark answers.' },
  { id: 'mistakes-traps', label: 'Mistakes & traps', group: 'Practice & recall', icon: 'bi-exclamation-triangle', archetype: 'sheet', blurb: 'The errors everyone makes — and the fix for each.' },

  { id: 'feynman', label: 'Feynman check', group: 'Deep understanding', icon: 'bi-lightbulb', archetype: 'notes', blurb: 'Explained simply — then the gaps that trip people up.' },
  { id: 'worked-examples', label: 'Worked examples', group: 'Deep understanding', icon: 'bi-list-ol', archetype: 'notes', blurb: 'Problems solved step by step, with the reasoning.' },
  { id: 'story', label: 'Story & analogy', group: 'Deep understanding', icon: 'bi-book', archetype: 'notes', blurb: "The concept as a story you won't forget." },
  { id: 'glossary', label: 'Glossary & formula sheet', group: 'Deep understanding', icon: 'bi-journal-bookmark', archetype: 'sheet', blurb: 'Every key term and formula, with when to use it.' },
  { id: 'depth-ladder', label: '5 levels of depth', group: 'Deep understanding', icon: 'bi-bar-chart-steps', archetype: 'notes', blurb: 'The same idea explained from child to expert.' },
  { id: 'socratic', label: 'Socratic dialogue', group: 'Deep understanding', icon: 'bi-chat-quote', archetype: 'notes', blurb: 'Learn by being asked exactly the right questions.' },
  { id: 'case-studies', label: 'Real-world cases', group: 'Deep understanding', icon: 'bi-briefcase', archetype: 'notes', blurb: 'Short cases where this knowledge decides the outcome.' },
  { id: 'debate', label: 'Debate prep', group: 'Deep understanding', icon: 'bi-people', archetype: 'sheet', blurb: 'Both sides of the argument, ready to defend.' },

  { id: 'swot', label: 'SWOT analysis', group: 'Business & strategy', icon: 'bi-grid-2x2-gap', archetype: 'sheet', blurb: 'Strengths, weaknesses, opportunities, and threats.' },
  { id: 'pros-cons', label: 'Pros & cons', group: 'Business & strategy', icon: 'bi-plus-slash-minus', archetype: 'sheet', blurb: 'Trade-offs, risks, and a balanced recommendation.' },
  { id: 'kpi-dashboard', label: 'KPI dashboard', group: 'Business & strategy', icon: 'bi-speedometer2', archetype: 'sheet', blurb: 'Headline metrics, signals, risks, and next actions.' },
  { id: 'stakeholder-map', label: 'Stakeholder map', group: 'Business & strategy', icon: 'bi-bullseye', archetype: 'matrix', blurb: 'People mapped by influence, interest, and engagement.' },

  { id: 'sequence-diagram', label: 'Sequence diagram', group: 'Software & systems', icon: 'bi-arrow-left-right', archetype: 'diagram', blurb: 'Who calls whom, in what order — APIs and interactions.' },
  { id: 'swimlane', label: 'Swimlane workflow', group: 'Software & systems', icon: 'bi-distribute-vertical', archetype: 'diagram', blurb: 'A cross-team process with one lane per role or system.' },
  { id: 'class-diagram', label: 'Class diagram', group: 'Software & systems', icon: 'bi-boxes', archetype: 'diagram', blurb: 'The object-oriented structure of the system.' },
  { id: 'er-diagram', label: 'Database ER diagram', group: 'Software & systems', icon: 'bi-database', archetype: 'diagram', blurb: 'Entities, attributes and relationships of the data model.' },
  { id: 'state-machine', label: 'State machine', group: 'Software & systems', icon: 'bi-toggles', archetype: 'diagram', blurb: 'Every state the system can be in, and what moves it.' },
  { id: 'architecture', label: 'Architecture sketch', group: 'Software & systems', icon: 'bi-hdd-network', archetype: 'diagram', blurb: 'Tiers, services and what flows between them.' },
  { id: 'user-journey', label: 'User journey', group: 'Software & systems', icon: 'bi-emoji-smile', archetype: 'diagram', blurb: 'The experience phase by phase, scored step by step.' },
  { id: 'gantt', label: 'Project plan', group: 'Software & systems', icon: 'bi-calendar-range', archetype: 'diagram', blurb: 'The work as a scheduled Gantt chart.' },
  { id: 'git-flow', label: 'Git branching', group: 'Software & systems', icon: 'bi-git', archetype: 'diagram', blurb: 'The branching and merging strategy, drawn.' },
]

export const studyStyleById = (id: string | null | undefined): StudyStyleMeta | undefined =>
  STUDY_STYLES.find(s => s.id === id)
