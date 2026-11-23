namespace StudyManager.Api.Services;

/// <summary>
/// The 50 learning/visual styles offered by Study Studio, Inkling, and the
/// "✨ Visualize" selection flow. Each style maps to one of a small set of
/// renderer archetypes; the Instructions steer the model to return structured
/// JSON matching that archetype's schema, and the frontend renders the visuals
/// — the model never draws anything. Keep in sync with frontend/src/data/studyStyles.ts.
/// </summary>
public sealed record StudyStyle(
    string Id,
    string Label,
    string Group,
    string Icon,          // bootstrap-icons class
    string Archetype,     // notes | board | sheet | diagram | mindmap | timeline | venn | matrix | cards | worksheet
    string Blurb,
    string Instructions);

public static class StudyStyleCatalog
{
    public const string GroupNotes = "Notes & summaries";
    public const string GroupVisual = "Diagrams & visuals";
    public const string GroupRecall = "Practice & recall";
    public const string GroupDepth = "Deep understanding";
    public const string GroupBusiness = "Business & strategy";
    public const string GroupSoftware = "Software & systems";

    public static readonly IReadOnlyList<StudyStyle> All = new List<StudyStyle>
    {
        // ── Notes & summaries ────────────────────────────────────────────────
        new("handwritten", "Handwritten notes", GroupNotes, "bi-pencil", "notes",
            "Notebook-style study notes with highlights and margin cues.",
            "Write warm, personal study notes as a top student would in a paper notebook. Short punchy lines, not paragraphs. " +
            "Use 4-7 sections. In each section give 3-6 items; prefix the single most important item with '**' so it can be highlighted. " +
            "Add a one-line 'cue' per section — the margin keyword a student would scribble. End with a short summary."),
        new("notebook", "Notebook page", GroupNotes, "bi-journal-richtext", "notes",
            "The viral study-gram look: highlights, doodles, example callouts.",
            "Create an aesthetic notebook page like viral studygram notes. 3-6 sections with SHORT numbered headings " +
            "(e.g. '1. Weak AI (Narrow AI)'). cue = exactly ONE relevant emoji for the section (the emoji only, it becomes a doodle). " +
            "Each section has 1-3 ultra-short bullet items (max 10 words each); add a line starting 'Example: ' where a concrete " +
            "example helps; prefix the single most crucial item on the page with '**'. summary = one short good-to-know remark " +
            "(don't start it with 'Note:' — the page adds that)."),
        new("sticky-board", "Sticky-note board", GroupNotes, "bi-sticky", "board",
            "A wall of colourful post-its — one idea per note.",
            "Distil the material into 9-16 sticky notes. Each note is ONE self-contained idea in at most 18 words. " +
            "Vary colours by kind: yellow=core idea, pink=warning/pitfall, blue=definition, green=example, orange=exam tip."),
        new("cheat-sheet", "Cheat sheet", GroupNotes, "bi-grid-3x3-gap", "sheet",
            "A dense boxed one-pager to print and keep.",
            "Create a dense, print-ready cheat sheet: 6-10 titled boxes, each with 3-6 ultra-compact items (fragments, not sentences). " +
            "Include where useful a box of formulas/syntax, a box of common mistakes, and a box of memory hooks."),
        new("cornell", "Cornell notes", GroupNotes, "bi-layout-sidebar", "notes",
            "The classic cue-column + notes + summary format.",
            "Produce Cornell-method notes: 4-8 sections, each with a short 'cue' (question or keyword for the left column) and " +
            "3-5 note items answering it. Finish with a 2-3 sentence summary a student would write at the bottom of the page."),
        new("study-plan", "Study plan", GroupNotes, "bi-calendar-check", "notes",
            "A day-by-day plan to actually learn this.",
            "Build a realistic day-by-day study plan (default 7 days unless the material clearly needs fewer/more; max 14). " +
            "Each section is one day: cue = the day label with its theme, items = concrete 20-40 minute tasks mixing learning, " +
            "practice and review, with earlier topics resurfacing on later days (spaced repetition). End with an exam-eve summary."),

        // ── Diagrams & visuals ───────────────────────────────────────────────
        new("mind-map", "Mind map", GroupVisual, "bi-diagram-2", "mindmap",
            "The topic radiating into colour-coded branches.",
            "Design a mind map. root = the core concept in 2-4 words. 4-6 main branches, each with 2-5 child nodes (max 4 words " +
            "each); a child may optionally carry up to 3 short sub-items (max 3 words each) for detail worth remembering. " +
            "Every label is punchy — keywords, not sentences."),
        new("concept-map", "Concept map", GroupVisual, "bi-diagram-3", "diagram",
            "Ideas connected with labelled relationships.",
            "Design a concept map. Output mermaid 'flowchart LR' syntax where nodes are concepts and every edge has a linking " +
            "phrase label like -->|causes| or -->|is a type of|. 8-14 nodes. No prose outside the mermaid code. CRITICAL: node and edge labels may contain ONLY letters, numbers and spaces — never colons, commas, quotes, ampersands, parentheses or other punctuation (write 'Iron Man 2' style labels)."),
        new("flowchart", "Flowchart", GroupVisual, "bi-signpost-split", "diagram",
            "A process or decision path, step by step.",
            "Turn the material into a process/decision flowchart. Output mermaid 'flowchart TD' syntax with decision diamonds " +
            "for branch points and clear yes/no edge labels. 8-16 nodes. No prose outside the mermaid code. CRITICAL: node and edge labels may contain ONLY letters, numbers and spaces — never colons, commas, quotes, ampersands, parentheses or other punctuation (write 'Iron Man 2' style labels)."),
        new("timeline", "Timeline", GroupVisual, "bi-clock-history", "timeline",
            "Events or stages in order, at a glance.",
            "Lay the material out as a timeline of 6-12 entries in chronological or process order. Each event has a short date/stage " +
            "label, a title, and one crisp sentence of detail."),
        new("venn", "Venn comparison", GroupVisual, "bi-circle-half", "venn",
            "What's unique to each and what overlaps.",
            "Compare the two most important concepts in the material (pick them yourself if not stated). Give 3-6 traits unique " +
            "to A, 3-6 unique to B, and 3-5 shared traits. Keep each trait under 10 words."),
        new("matrix", "Comparison matrix", GroupVisual, "bi-table", "matrix",
            "Feature-by-feature table across options.",
            "Build a comparison matrix: 2-5 things being compared as columns, 5-10 comparison dimensions as rows. Cells are short " +
            "fragments; make differences pop."),
        new("infographic", "Infographic page", GroupVisual, "bi-bar-chart", "sheet",
            "Big numbers, icon bullets, scannable sections.",
            "Design an infographic-style one-pager: start with 3-5 headline stats (a big value + tiny label each) in a 'stats' box " +
            "titled 'Key numbers' with items formatted 'VALUE — label', then 4-6 scannable sections of 3-4 icon-worthy points each."),
        new("learning-roadmap", "Learning roadmap", GroupVisual, "bi-map", "diagram",
            "What to learn, in what order, to master this.",
            "Design a learning roadmap for mastering this material. Output mermaid 'flowchart TD' syntax: nodes are the concepts/skills " +
            "in dependency order from prerequisites down to the target mastery, with stage-marker nodes like 'Foundations' and " +
            "'Getting serious'. 10-16 nodes. No prose outside the mermaid code. CRITICAL: node and edge labels may contain ONLY letters, numbers and spaces — never colons, commas, quotes, ampersands, parentheses or other punctuation (write 'Iron Man 2' style labels)."),
        new("cycle", "Cycle diagram", GroupVisual, "bi-arrow-repeat", "diagram",
            "A repeating loop with clear stages and feedback.",
            "Turn the material into a repeating cycle using mermaid 'flowchart LR'. Use 4-8 short stage nodes connected in order, " +
            "then connect the final stage back to the first with a labelled feedback or renewal arrow. Add at most two supporting " +
            "nodes only when they clarify an input or output. No prose outside the mermaid code. CRITICAL: labels may contain ONLY " +
            "letters, numbers and spaces — no punctuation."),
        new("hierarchy", "Hierarchy tree", GroupVisual, "bi-diagram-3", "diagram",
            "Levels, ownership, categories, or reporting structure.",
            "Organise the material as a hierarchy using mermaid 'flowchart TD'. Start with one root, then 3-6 second-level branches " +
            "and 2-5 children beneath each where the source supports them. Use 10-22 nodes total and connect only parent to child. " +
            "No prose outside the mermaid code. CRITICAL: labels may contain ONLY letters, numbers and spaces — no punctuation."),
        new("pyramid", "Pyramid breakdown", GroupVisual, "bi-triangle", "diagram",
            "A layered progression from foundation to peak.",
            "Build a layered pyramid using mermaid 'flowchart BT'. Use 4-6 levels from the broad foundation at the bottom to the " +
            "narrow outcome or highest-order idea at the top. Each level is one short node; optional supporting nodes may sit beside " +
            "a level when essential. Connect every level upward in sequence. No prose outside the mermaid code. CRITICAL: labels may " +
            "contain ONLY letters, numbers and spaces — no punctuation."),
        new("funnel", "Funnel analysis", GroupVisual, "bi-filter", "diagram",
            "How a broad input narrows through successive stages.",
            "Model the material as a narrowing funnel using mermaid 'flowchart TD'. Begin with the broadest pool, input, or audience " +
            "and move through 5-8 filtering stages to one final result. Label arrows with the selection rule or loss between stages " +
            "when useful. No prose outside the mermaid code. CRITICAL: labels may contain ONLY letters, numbers and spaces — no punctuation."),
        new("cause-effect", "Cause & effect", GroupVisual, "bi-bezier2", "diagram",
            "Root causes grouped around one important outcome.",
            "Create a cause-and-effect map using mermaid 'flowchart LR'. Put one clearly named outcome on the right. Group 8-16 root " +
            "causes into 3-6 category subgraphs on the left and connect every cause or category toward the outcome. Show intermediate " +
            "effects only when they improve the logic. No prose outside the mermaid code. CRITICAL: labels may contain ONLY letters, " +
            "numbers and spaces — no punctuation."),
        new("decision-tree", "Decision tree", GroupVisual, "bi-signpost-split", "diagram",
            "Choices, conditions, and outcomes in branching form.",
            "Build a decision tree using mermaid 'flowchart TD'. Start with one decision, use diamond nodes for 3-7 conditions, label " +
            "branches Yes No or with short choices, and end every route in a concrete action or outcome. Use 10-20 nodes total. " +
            "No prose outside the mermaid code. CRITICAL: labels may contain ONLY letters, numbers and spaces — no punctuation."),
        new("relationship-map", "Relationship map", GroupVisual, "bi-share", "diagram",
            "A central idea and the forces connected to it.",
            "Create a relationship map using mermaid 'flowchart LR'. Put the main concept in the centre, surround it with 6-12 actors, " +
            "forces, resources, or related ideas, and use short labelled arrows to show direction and meaning. Add links between outer " +
            "nodes only when the relationship is important. No prose outside the mermaid code. CRITICAL: labels may contain ONLY " +
            "letters, numbers and spaces — no punctuation."),
        new("process-steps", "Process steps", GroupVisual, "bi-arrow-right-square", "timeline",
            "A polished sequence of phases, actions, and handoffs.",
            "Turn the material into 6-12 polished process steps in strict order. label = a short phase marker such as Step 01 or " +
            "Review; heading = the action in 2-6 words; detail = one crisp sentence covering the owner, handoff, or completion " +
            "condition. The final step must state the finished outcome."),

        // ── Practice & recall ────────────────────────────────────────────────
        new("flashcards", "Flashcards", GroupRecall, "bi-collection", "cards",
            "Flip cards for active recall.",
            "Write 10-16 flashcards covering the material. Front: a precise question or term (max 15 words). Back: the tight, " +
            "complete answer (max 40 words). Order from foundational to advanced."),
        new("quiz", "Practice quiz", GroupRecall, "bi-patch-question", "worksheet",
            "Multiple-choice test with explanations.",
            "Write a 8-12 question multiple-choice quiz. Each question has 4 options (A-D) with exactly one correct answer, and a " +
            "1-2 sentence explanation of WHY the answer is right and the tempting distractor is wrong. Mix difficulty."),
        new("fill-blanks", "Fill in the blanks", GroupRecall, "bi-input-cursor-text", "worksheet",
            "A worksheet with an answer key.",
            "Create 10-14 fill-in-the-blank sentences from the material, each with exactly one blank written as _____ . The blank " +
            "should be a key term, not a trivial word. Provide the answer for each (and a short why when helpful). No options arrays."),
        new("feynman", "Feynman check", GroupDepth, "bi-lightbulb", "notes",
            "Explained simply — then the gaps that trip people up.",
            "Apply the Feynman technique: first sections explain the material in genuinely simple language (as to a smart " +
            "12-year-old, no jargon without instantly defining it). Then a section 'Where people get stuck' listing the misconceptions " +
            "and hand-wavy spots, and a section 'Test yourself' with prompts to re-explain aloud. Summary = the whole thing in 2 sentences."),
        new("mnemonics", "Mnemonics pack", GroupRecall, "bi-magic", "sheet",
            "Acronyms, rhymes and memory hooks.",
            "Create memory aids for the hardest-to-remember parts: boxes for Acronyms, Rhymes/phrases, Number/letter hooks, and a " +
            "mini Memory palace (a short vivid journey linking the items). Each item states the aid AND what it encodes."),
        new("recall-drill", "Active recall drill", GroupRecall, "bi-arrow-repeat", "worksheet",
            "Open questions to quiz yourself — the fastest way to learn.",
            "Write 10-14 OPEN questions (no multiple choice — options must be empty arrays) for self-testing, ordered easy to hard. " +
            "answer = the model answer in 1-3 tight sentences. explain = a memory hook, or why this question matters."),
        new("exam-predictor", "Likely exam questions", GroupRecall, "bi-file-earmark-check", "worksheet",
            "The questions most likely to come up — with full-mark answers.",
            "Predict the 8-12 most likely exam questions on this material: mix definitions, applications, compare/contrast and one " +
            "essay-style question. options empty unless a question is naturally multiple-choice. answer = a model answer that would " +
            "score full marks. explain = what the examiner is really checking."),
        new("mistakes-traps", "Mistakes & traps", GroupRecall, "bi-exclamation-triangle", "sheet",
            "The errors everyone makes — and the fix for each.",
            "Catalogue the common errors: boxes like 'Misconceptions', 'Easily confused pairs', 'Process slip-ups', 'Exam traps'. " +
            "Every item is one line in the form 'Wrong: … → Right: …' so the correction sticks."),

        // ── Deep understanding ───────────────────────────────────────────────
        new("worked-examples", "Worked examples", GroupDepth, "bi-list-ol", "notes",
            "Problems solved step by step, with the reasoning.",
            "Produce 2-4 fully worked examples: each section is one problem (cue = the problem statement), items are the numbered " +
            "solution steps — every step states WHAT is done and WHY. Prefix the step where students usually slip with '**'. " +
            "End with a summary of the general method."),
        new("story", "Story & analogy", GroupDepth, "bi-book", "notes",
            "The concept as a story you won't forget.",
            "Teach the material as a short vivid story or extended real-world analogy. Sections are chapters/beats; keep the " +
            "narrative concrete and map every story element explicitly back to the real concept (cue = the concept it maps to). " +
            "End with a summary that translates the story back to plain technical truth."),
        new("glossary", "Glossary & formula sheet", GroupDepth, "bi-journal-bookmark", "sheet",
            "Every key term and formula, with when to use it.",
            "Extract every key term, formula, or rule into boxes grouped by theme. Each item is 'term — meaning in one line, plus " +
            "when/why you'd use it'. Formulas include what each symbol stands for."),
        new("depth-ladder", "5 levels of depth", GroupDepth, "bi-bar-chart-steps", "notes",
            "The same idea explained from child to expert.",
            "Explain the material at exactly 5 increasing depths — sections titled 'Level 1 — To a child' through 'Level 5 — To an " +
            "expert' (cue = the audience). Each level ADDS precision and vocabulary (define new terms inline) rather than repeating. " +
            "Summary = what fundamentally changes as you go deeper."),
        new("socratic", "Socratic dialogue", GroupDepth, "bi-chat-quote", "notes",
            "Learn by being asked exactly the right questions.",
            "Teach through a Socratic dialogue: each section is one exchange — cue = the probing question a great tutor asks, items = " +
            "the reasoning steps a student works through aloud to reach the answer (a wrong-but-instructive first guess may open, " +
            "prefixed '**'). Summary = the insight the questioning was steering toward."),
        new("case-studies", "Real-world cases", GroupDepth, "bi-briefcase", "notes",
            "Short cases where this knowledge decides the outcome.",
            "Create 3-4 short real-world case studies where this material decides the outcome: cue = the setting, heading = the case, " +
            "items = what happened, how the concept applies step by step, and the outcome/lesson. Summary = the pattern across all cases."),
        new("debate", "Debate prep", GroupDepth, "bi-people", "sheet",
            "Both sides of the argument, ready to defend.",
            "Pick the sharpest contested question inside this material and prepare both sides: boxes 'The question', 'Strongest case " +
            "FOR', 'Strongest case AGAINST', 'Rebuttals', 'Where the evidence lands'. Items are single punchy arguments grounded in " +
            "the material's evidence."),

        // ── Business & strategy ──────────────────────────────────────────────
        new("swot", "SWOT analysis", GroupBusiness, "bi-grid-2x2-gap", "sheet",
            "Strengths, weaknesses, opportunities, and threats.",
            "Create exactly four boxes titled Strengths, Weaknesses, Opportunities, and Threats. Put 4-7 specific evidence-based " +
            "items in each box. Strengths and weaknesses are internal; opportunities and threats are external. Keep every item under " +
            "16 words and end with one fifth box titled Strategic implication containing 2-4 recommended moves."),
        new("pros-cons", "Pros & cons", GroupBusiness, "bi-plus-slash-minus", "sheet",
            "Trade-offs, risks, and a balanced recommendation.",
            "Evaluate the central choice in the source with boxes titled Decision, Advantages, Drawbacks, Trade offs, Risks, and " +
            "Recommendation. Give 4-7 specific points in the advantages and drawbacks boxes, distinguish reversible from irreversible " +
            "risks, and make the recommendation conditional and evidence-based rather than generic."),
        new("kpi-dashboard", "KPI dashboard", GroupBusiness, "bi-speedometer2", "sheet",
            "Headline metrics, signals, risks, and next actions.",
            "Build an executive KPI dashboard. The first box MUST be titled 'Key numbers' and contain 4-8 items formatted exactly " +
            "'VALUE — metric label' using real values from the source where available and clearly marked targets otherwise. Add boxes " +
            "titled Signals, Risks, Drivers, and Next actions with 3-6 short items each. Never invent factual measurements."),
        new("stakeholder-map", "Stakeholder map", GroupBusiness, "bi-bullseye", "matrix",
            "People mapped by influence, interest, and engagement.",
            "Create a stakeholder influence-interest matrix. columns must be exactly ['High influence','Low influence']; rows must be " +
            "exactly two rows labelled 'High interest' and 'Low interest'. Each cell lists the relevant stakeholders followed by a " +
            "short engagement approach, separated with semicolons. Use only stakeholders supported by the source; if roles are implied, " +
            "name the role rather than inventing a person."),

        // ── Software & systems ───────────────────────────────────────────────
        new("sequence-diagram", "Sequence diagram", GroupSoftware, "bi-arrow-left-right", "diagram",
            "Who calls whom, in what order — APIs and interactions.",
            "Model the interactions as a mermaid 'sequenceDiagram': participants are the actors/systems/components; messages in order " +
            "using ->> for calls and -->> for responses, with an occasional 'Note over' for a key clarification. 8-18 messages. " +
            "Participant names are single words (letters/numbers only); message text is short and contains no commas or semicolons."),
        new("swimlane", "Swimlane workflow", GroupSoftware, "bi-distribute-vertical", "diagram",
            "A cross-team process with one lane per role or system.",
            "Model the workflow as swimlanes using mermaid 'flowchart TB': one subgraph per role/team/system lane " +
            "(subgraph LaneName ... end), each step as a node inside its owner's lane, arrows crossing lanes for handoffs, and " +
            "decision diamonds where paths branch. 3-5 lanes, 8-16 steps. CRITICAL: node and lane labels may contain ONLY letters, " +
            "numbers and spaces — no punctuation."),
        new("class-diagram", "Class diagram", GroupSoftware, "bi-boxes", "diagram",
            "The object-oriented structure of the system.",
            "Model the structure as a mermaid 'classDiagram': 5-10 classes with 2-5 key attributes/methods each, relationships with " +
            "correct arrows (<|-- inheritance, *-- composition, o-- aggregation, --> association) and cardinalities where meaningful. " +
            "Class and member names are simple identifiers — letters and numbers only."),
        new("er-diagram", "Database ER diagram", GroupSoftware, "bi-database", "diagram",
            "Entities, attributes and relationships of the data model.",
            "Design the data model as a mermaid 'erDiagram': 4-8 entities with 3-6 typed attributes each (one 'type name' per line), " +
            "relationships written as A ||--o{ B : label with correct cardinality symbols. Entity/attribute names use letters, numbers " +
            "and underscores only; relationship labels are a single word."),
        new("state-machine", "State machine", GroupSoftware, "bi-toggles", "diagram",
            "Every state the system can be in, and what moves it.",
            "Model the behaviour as a mermaid 'stateDiagram-v2': [*] for start/end, 6-14 states with simple names " +
            "(letters/numbers/underscores), transitions labelled with the triggering event after a colon. Keep event labels short, " +
            "no commas."),
        new("architecture", "Architecture sketch", GroupSoftware, "bi-hdd-network", "diagram",
            "Tiers, services and what flows between them.",
            "Sketch the system architecture as mermaid 'flowchart TB': subgraphs for tiers/zones (e.g. Client, Backend, Data, Third " +
            "party), nodes for services/components, edges labelled with the protocol or data that flows (|HTTPS| style labels). " +
            "8-16 nodes. CRITICAL: all labels may contain ONLY letters, numbers and spaces — no punctuation."),
        new("user-journey", "User journey", GroupSoftware, "bi-emoji-smile", "diagram",
            "The experience phase by phase, scored step by step.",
            "Map the experience as a mermaid 'journey' diagram: a 'title' line, 3-5 'section' phases, each with 2-5 tasks in the exact " +
            "form 'Task name: score: Actor' where score is 1-5 satisfaction. Task names are short and contain no commas."),
        new("gantt", "Project plan", GroupSoftware, "bi-calendar-range", "diagram",
            "The work as a scheduled Gantt chart.",
            "Plan the work as a mermaid 'gantt' chart: 'dateFormat YYYY-MM-DD', 3-5 sections, 8-14 tasks with realistic durations " +
            "(forms like 'Task name :t1, 2026-08-01, 5d' or 'Task name :t2, after t1, 3d'). Derive a sensible schedule from the " +
            "material. Task names contain no commas or colons."),
        new("git-flow", "Git branching", GroupSoftware, "bi-git", "diagram",
            "The branching and merging strategy, drawn.",
            "Show the branching strategy as a mermaid 'gitGraph': commit/branch/checkout/merge operations demonstrating the workflow " +
            "the material implies (main, develop, feature, release, hotfix as appropriate). 10-18 operations. Branch names use " +
            "lowercase letters and hyphens only; commit ids/tags are short with no spaces."),
    };

    public static StudyStyle? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(s => s.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
}
