---
name: Feature request
about: Propose a new command, staging tag, front-end, or capability
title: ""
labels: enhancement
---

**The need**
What story or workflow can't you express today?

**Proposal**
If it's a new command or staging tag, sketch its shape:

```json
{ "op": "your_op", "field": "..." }
```

or `# your_tag: args`. If it's a new authoring front-end, name the tool.

**Why in the container, not the host**
LVN keeps the runtime content-agnostic. Explain why this belongs in the format
or transcoder rather than a game-specific `ILvnStage` implementation.
