Find concrete, actionable follow-up tasks in the page below.

<evidence>
{{evidence}}
</evidence>

Today's date is {{today}}.

Respond with JSON containing "tasks", a list of at most 8 items. For each task:
- "title": a short imperative phrase, under 80 characters.
- "description": one sentence of context from the page.
- "priority": one of "low", "medium", "high", "urgent". Use "medium" unless the page indicates urgency.
- "due_date": a date in YYYY-MM-DD form only if the page states or clearly implies one, otherwise "".
- "evidence": a short exact quote from the page that supports the task.

Only include tasks that the page supports. If there are none, return an empty list.
