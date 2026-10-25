Review the workspace notes below for this request: {{question}}

<evidence>
{{evidence}}
</evidence>

Identify unresolved questions, open issues, and next steps that the notes reveal, and propose tasks to address them.

Respond with JSON:
- "summary": two or three sentences describing what you found, citing source ids in square brackets such as [S1].
- "tasks": at most 8 items. For each:
  - "title": a short imperative phrase, under 80 characters.
  - "description": one or two sentences explaining what to investigate or do.
  - "priority": one of "low", "medium", "high", "urgent".
  - "source": the single source id (for example "S2") the task comes from. It must be an id from the evidence.
  - "rationale": the reason, in a few words, quoting the note where possible.

If the notes contain nothing actionable, return an empty task list and explain why in the summary.
