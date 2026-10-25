Answer the question using only the evidence below.

<evidence>
{{evidence}}
</evidence>

Question: {{question}}

Respond with JSON:
- "status": "answered" if the evidence fully answers it, "partial" if only partly, "not_found" if the evidence does not address it.
- "answer": the answer in plain prose. After every sentence that uses evidence, add the source id in square brackets, for example [S2]. If status is "not_found", say that the workspace notes do not cover this.
- "citations": every source id you used, for example ["S1","S2"]. Use only ids that appear in the evidence.
- "inferences": short statements that are your own reasoning rather than something the evidence states. Use an empty list if there are none.
