A step in the SlothBoss agent's plan failed. Decide how to continue toward the goal with the remaining work.

User goal: {{goal}}

Completed steps and their actual results:
{{completed}}

Failed step: {{failed}}
Error reported by the application: {{error}}

Steps that were still planned:
{{remaining}}

Tools you may use (use only these ids):
{{tools}}

Rules:
- Do not repeat the failed step with the same options; choose a different approach or skip it.
- Plan at most {{max_steps}} remaining steps. Keep report.create at the end.
- If nothing sensible remains, return an empty steps list and explain in understanding.

Respond with JSON using the same format as the original plan: "understanding", "missing_information", "steps" (each with "tool", "purpose" and only the options that tool uses), "completion_criteria".
