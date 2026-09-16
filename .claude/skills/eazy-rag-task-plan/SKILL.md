---
name: eazy-rag-task-plan
description: 任务计划（项目 ID: 3）——以实际任务要求为参数，按工作流生成 MD 执行计划并在 RAG 库创建任务保存
effort: high
user-invocable: true
disable-model-invocation: false
agent: general-purpose
---

# 任务计划

此技能用于根据实际任务要求，为 EAZY.RAG 知识库当前项目「小D图像」（项目 ID: 3）自动生成 MD 格式执行计划，并在 RAG 库按当前模块创建任务保存。执行计划落库前须向用户展示确认。

## 使用方法

```
/eazy-rag-task-plan {任务要求}
```

## 功能

1. **模块确认**：使用 `/eazy-rag-module-get` 获取当前工作模块名称；若未设置（无值），先运行 `/eazy-rag-module-set` 设置当前工作模块。
2. **读取上下文中的当前模块 Id**：读取本地上下文标记文件 `~/.eazyrag/context/3.json` 的 currentModuleId / currentModuleName。
3. **创建任务**：根据当前模块 Id 调用 `mcp__eazy-rag__create_task`（moduleId=当前模块Id，description=任务要求）创建任务。
4. **提取或联想关键词**：按任务要求提取或联想关键词（功能名、领域词、技术词等）。
5. **搜索关联已归档任务**：调用 `mcp__eazy-rag__search_archived_tasks`（projectId=3，keywords=关键词）搜索关联已归档任务信息，作为参考。
6. **关键词工具搜索本地文件**：直接使用关键词工具（Grep/Glob）搜索本地项目文件，寻找可复用实现与既有模式。
7. **读取项目执行规范/注意事项/依赖关系**：调用 `mcp__eazy-rag__get_project`（projectId=3）→ 读取 executionSpecs / executionNotes / dependencies。
8. **分析任务要求与搜集到的信息并思考**：识别关键设计决策与不确定点。
9. **AskUser 提问**：对无法确定的信息向用户提问——**所有问题都必须提供备选项**（AskUserQuestion 带 options）；提问时调用 `mcp__eazy-rag__add_task_question`（taskId，question，options JSON 数组）将问题实时存储到任务问答子表；用户回答后调用 `mcp__eazy-rag__answer_task_question`（taskId，questionId，answer）记录回答；所有提问经操作日志在**执行历史**中展示。
10. **生成 MD 格式执行计划**：结合所有信息生成执行计划，包含章节：任务描述/任务分析/执行步骤/执行输出/结果验收/参考文档/执行规范/注意事项。
11. **更新任务并保存执行计划**：根据当前模块 Id 调用 `mcp__eazy-rag__save_execution_plan`（taskId，content）保存执行计划，**从返回的 JSON 中获取分享码（shareCode）** → 调用 `mcp__eazy-rag__update_task`（taskId，name=任务名称）回填任务名称。
12. **展示并分享**：向用户展示执行计划，**用分享码拼接完整分享查阅 URL**：`https://rag.jueyun.net/share/{分享码}`（前端访问地址已由系统配置「系统外部网址」在技能下载时填充），以 Markdown 超链接 `[分享查阅]({完整URL})` 输出；**仅展示，不执行**（不调用 `mcp__eazy-rag__confirm_execution`）。

## 注意事项

- 所有 MCP 调用必须携带正确的项目 ID：3。
- **所有步骤非注明需要询问或确认时，无需询问和确认，直接执行**。
- **先创建任务再提问**：问答子表按任务挂载（TaskItemId 外键），AskUser 提问前必须先调用 `create_task` 获取任务 ID。
- **提问必须提供备选项**：所有 AskUserQuestion 问题均须带 options，禁止无选项提问。
- **问答实时入库 + 执行历史展示**：提问 → `add_task_question`；用户回答 → `answer_task_question`；两者均实时写操作日志（Feature=taskPlanQa），管理员可在操作日志界面查看全部提问。
- 生成的执行计划必须包含 任务描述/任务分析/执行步骤/执行输出/结果验收/参考文档/执行规范/注意事项 全部章节。
- **分享查阅链接**：保存执行计划后务必从 `save_execution_plan` 返回中获取分享码（shareCode），用它拼接分享查阅 URL `https://rag.jueyun.net/share/{分享码}` 并以超链接输出，供用户在浏览器查看执行计划（前端访问地址已由系统配置「系统外部网址」在技能下载时填充）。
- **仅展示，不执行**：本技能只负责生成与保存执行计划，不执行任务、不调用 `confirm_execution`；执行由用户确认后另行处理。