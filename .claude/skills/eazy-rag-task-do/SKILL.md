---
name: eazy-rag-task-do
description: 任务计划执行（项目 ID: 3）——以任务Id为参数读取最新执行计划并执行，回写执行结果并更新任务状态
effort: high
user-invocable: true
disable-model-invocation: false
agent: general-purpose
---

# 任务计划执行

此技能用于对 EAZY.RAG 知识库当前项目「小D图像」（项目 ID: 3）中已有执行计划的任务，按最新执行计划执行相关操作，操作完成后将执行结果回写（confirm_execution），更新任务状态，并将执行结果展示到对话中。

## 使用方法

```
/eazy-rag-task-do {任务Id}
```

任务 ID 由参数 `{任务Id}` 提供或从上下文识别；未提供且无法识别时，按「确定目标任务 ID」步骤读取当前模块下非已归档任务列表供用户选择。

## 功能

1. **确定目标任务 ID**：任务 ID 由参数或上下文识别；若无法识别，使用 `/eazy-rag-module-get` 获取当前工作模块；若未设置（无值），先运行 `/eazy-rag-module-set` 设置当前工作模块；随后调用 `mcp__eazy-rag__list_tasks`（moduleId=当前模块Id）读取当前模块下**所有非已归档任务**列表，经 AskUser 询问用户选择目标任务。
2. **读取任务与最新执行计划**：
   - 调用 `mcp__eazy-rag__get_task`（taskId）→ 任务名称/描述/状态，校验任务归属当前项目且未归档；
   - 调用 `mcp__eazy-rag__get_execution_plan`（taskId，默认最新版本）→ 最新执行计划正文 + 执行结果 + 该版本补充说明 + **返回的 planId**（供回写执行结果使用）。
3. **判断可执行性**：
   - 若最新版本**含补充说明**（supplementaryNotes 非空）→ 正文已冻结，**不可直接执行，须先重新生成**：运行 `/eazy-rag-task-replan {任务Id}` 重新生成新版本执行计划，再重新执行本技能；
   - 若最新版本**已执行**（IsExecuted=true）→ 已有执行结果；经用户确认后可直接复用既有执行结果，或先重新生成计划再执行；
   - 否则 → 可执行，继续。
4. **按执行计划执行操作**：向用户展示待执行的执行计划，确认后根据「执行步骤」「执行输出」章节在本地（当前项目工作目录）执行相关操作（代码修改、文件读写、检索等）；执行时可调用 `mcp__eazy-rag__search_documents` / `mcp__eazy-rag__get_document` 检索参考文档，使用 Grep/Glob 搜索本地文件辅助。
5. **回写执行结果**：执行完成后调用 `mcp__eazy-rag__confirm_execution`（taskId，planId=最新执行计划的 planId，executionResult=执行结果内容）将 AI 执行结果保存到该执行计划版本，并把任务状态置为已执行（Executed）。
6. **更新任务状态**：回写执行结果后任务状态自动置为 Executed；若任务已全部完成，可调用 `mcp__eazy-rag__update_task`（taskId，status=Completed）将任务状态更新为已完成。
7. **展示执行结果**：将执行结果（执行摘要/关键产出/完成情况）展示到对话中；**执行完成后（含复用既有执行结果的情形，只要流程走到本步）同时输出分享查阅链接**：用第 2 步 `get_execution_plan` 返回的 `shareCode` 拼接 `https://rag.jueyun.net/share/{分享码}`，以 Markdown 超链接 `[分享查阅]({完整URL})` 输出。

## 注意事项

- 所有 MCP 调用必须携带正确的项目 ID：3。
- **所有步骤非注明需要询问或确认时，无需询问和确认，直接执行**。
- **目标任务必须属于当前项目且未归档**：调用 `mcp__eazy-rag__get_task` 校验任务归属；已归档任务不允许执行。
- **有补充说明必须先重新生成**：最新执行计划含补充说明（正文已冻结）时不可直接执行，须先经 `/eazy-rag-task-replan` 重新生成新版本后再执行。
- **先确定任务 ID 再操作**：任务 ID 由参数/上下文提供；无法识别时须读取模块下**非已归档任务**列表供用户选择，禁止猜测任务 ID。
- **执行前展示计划并确认**：向用户展示待执行的执行计划，确认后方可执行。
- **执行结果必须回写落库**：执行完成后必须调用 `confirm_execution` 回写执行结果（记录到执行计划并置任务状态为 Executed），不得仅展示而不落库；回写时 planId 取第 2 步 `get_execution_plan` 返回的 planId。
- **分享查阅链接**：分享码取自第 2 步 `get_execution_plan` 返回的 `shareCode`（**不要从 `confirm_execution` 取，它不返回分享码**），用它拼接分享查阅 URL `https://rag.jueyun.net/share/{分享码}` 并以超链接输出，供用户在浏览器查看刚执行的版本（前端访问地址已由系统配置「系统外部网址」在技能下载时填充）。**执行不会改变分享码**：`confirm_execution` 原地修改该版本，不新建版本、不重新生成分享码，故第 2 步取到的分享码执行后依然有效，**切勿重新生成**。
- **不影响计划生成技能职责**：本技能负责执行，不负责重新生成计划；需升版重生成时调用 `/eazy-rag-task-replan`。