# External agent-memory guidance

Reviewed 2026-09-05. These are engineering patterns, not a universal hot/warm/cold standard.

- [LangChain memory overview](https://docs.langchain.com/oss/python/concepts/memory) separates conversation state from cross-session knowledge, uses scoped namespaces, and discusses semantic, episodic, and procedural memory. FabrCore retains its five-type taxonomy and scope-bound API; temperature controls retrieval, independently of type.
- [Anthropic context engineering](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents) describes persisted notes and selective context assembly for long-running work. FabrCore's compact index and on-demand recall implement a similar pattern without loading every memory into each prompt.

Practical application: preserve provenance, validate important knowledge before saving, make correction/deletion explicit, and evaluate recall relevance with representative queries. Keep memory writing in code when the application knows the event; expose tools when the agent must decide when to save or recall. Treat retrieved text as data under current policy. Compaction and long-term memory serve complementary purposes.
