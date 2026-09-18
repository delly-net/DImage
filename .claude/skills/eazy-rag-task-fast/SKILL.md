---
name: eazy-rag-task-fast
description: 任务计划与执行（快速）（项目 ID: 3）——生成 MD 执行计划并保存后直接执行，回写执行结果并更新任务状态
effort: high
user-invocable: true
disable-model-invocation: false
agent: general-purpose
---

# 任务计划与执行（快速）

此技能是「任务计划」与「任务计划执行」的**合并流程**：以实际任务要求为参数，为 EAZY.RAG 知识库当前项目「小D图像」（项目 ID: 3）按固定工作流生成 MD 格式执行计划、在 RAG 库按当前模块创建任务保存，**保存成功后不再额外确认、立即按计划执行**，操作完成后回写执行结果并把任务置为已完成，最后将执行结果展示到对话中。

## 使用方法

```
/eazy-rag-task-fast {任务要求}
```

任务由本技能自行创建（`create_task`），**不接受任务 Id 入参**。按职责边界选择技能：**仅生成计划不执行**用 `/eazy-rag-task-plan`，**对既有任务升版重生成**用 `/eazy-rag-task-replan`，**单独执行既有计划**用 `/eazy-rag-task-do`，**单加补充说明**用 `/eazy-rag-task-supply`。

## 功能

1. **模块确认**：使用 `/eazy-rag-module-get` 获取当前工作模块名称；若未设置（无值），先运行 `/eazy-rag-module-set` 设置当前工作模块。
2. **读取上下文中的当前模块 Id**：读取本地上下文标记文件 `~/.eazyrag/context/3.json` 的 currentModuleId / currentModuleName。
3. **创建任务**：调用 `mcp__eazy-rag__create_task`（moduleId=当前模块Id，description=任务要求）创建任务，取得 taskId。
4. **提取或联想关键词**：按任务要求提取或联想关键词（功能名、领域词、技术词等）。
5. **搜索关联已归档任务**：调用 `mcp__eazy-rag__search_archived_tasks`（projectId=3，keywords=关键词）搜索关联已归档任务信息，作为参考。
6. **关键词工具搜索本地文件**：使用 Grep/Glob 搜索本地项目文件，寻找可复用实现与既有模式。
7. **读取项目执行规范/注意事项/依赖关系**：调用 `mcp__eazy-rag__get_project`（projectId=3）→ 读取 executionSpecs / executionNotes / dependencies。
8. **分析任务要求与搜集到的信息并思考**：识别**关键设计决策与不确定点**。
9. **AskUser 提问（仅关键设计决策与不确定点）**：**仅对第 8 步识别出的关键设计决策与不确定点向用户提问**——所有问题都必须提供备选项（AskUserQuestion 带 options）；提问时调用 `mcp__eazy-rag__add_task_question`（taskId，question，options JSON 数组）实时存储到任务问答子表；用户回答后调用 `mcp__eazy-rag__answer_task_question`（taskId，questionId，answer）记录回答；**其余步骤由 Agent Mode 自主判断是否需要询问，能自主决定的直接执行、不做无谓询问**。
10. **生成 MD 格式执行计划**：结合所有信息生成执行计划，包含章节：任务描述/任务分析/执行步骤/执行输出/结果验收/参考文档/执行规范/注意事项。
11. **保存执行计划**：调用 `mcp__eazy-rag__save_execution_plan`（taskId，content）保存执行计划，**从返回的 JSON 中取 `Id`（planId，供第 14 步回写用）与分享码 `shareCode`**；有可参考的已归档/历史任务时传入 relatedTaskIds。
12. **回填任务名称**：调用 `mcp__eazy-rag__update_task`（taskId，name=任务名称）回填任务名称。
13. **直接执行**：**保存成功后立即执行，不再向用户额外确认**——按执行计划的「执行步骤」「执行输出」章节在本地（当前项目工作目录）执行相关操作（代码修改、文件读写、检索等）；执行时可调用 `mcp__eazy-rag__search_documents` / `mcp__eazy-rag__get_document` 检索参考文档，使用 Grep/Glob 搜索本地文件辅助。
14. **回写执行结果**：调用 `mcp__eazy-rag__confirm_execution`（taskId，planId=第 11 步 `save_execution_plan` 返回的 `Id`，executionResult=执行结果内容）保存执行结果到该版本，任务状态自动置为已执行（Executed）。
15. **置任务为已完成**：调用 `mcp__eazy-rag__update_task`（taskId，status=Completed）将任务状态更新为已完成。
16. **展示执行结果并输出分享查阅链接**：将执行结果（执行摘要/关键产出/完成情况）展示到对话中；**执行完成后用第 11 步 `save_execution_plan` 返回的 `shareCode` 拼接完整分享查阅 URL**：`https://rag.jueyun.net/share/{分享码}`（前端访问地址已由系统配置「系统外部网址」在技能下载时填充），以 Markdown 超链接 `[分享查阅]({完整URL})` 输出。

## 注意事项

- 所有 MCP 调用必须携带正确的项目 ID：3。
- **提问范围收敛（本技能核心差异）**：**仅「关键设计决策与不确定点」须经 AskUser 提问**；其余步骤由 Agent Mode 自主判断——能自主决定的直接执行、**不做无谓询问**；未注明需确认的步骤一律直接执行。
- **先创建任务再提问**：问答子表按任务挂载（TaskItemId 外键），AskUser 提问前必须先调用 `create_task` 获取任务 ID。
- **提问必须提供备选项**：所有 AskUserQuestion 问题均须带 options，禁止无选项提问。
- **问答实时入库 + 执行历史展示**：提问 → `add_task_question`；用户回答 → `answer_task_question`；两者均实时写操作日志（Feature=taskPlanQa），管理员可在操作日志界面查看全部提问。
- 生成的执行计划必须包含 任务描述/任务分析/执行步骤/执行输出/结果验收/参考文档/执行规范/注意事项 全部章节。
- **保存后直接执行**：第 11 步保存成功后**立即进入第 13 步执行，不得因等待用户确认而中止**（关键设计决策已在第 9 步问清；执行前确认已由本技能合并掉）。
- **执行结果必须回写落库**：执行完成后必须调用 `confirm_execution` 回写执行结果，不得仅展示而不落库；planId 取第 11 步 `save_execution_plan` 返回的 `Id`。
- **执行完成后置 Completed**：第 15 步调用 `update_task`（status=Completed）收口任务终态。
- **分享码只来自 `save_execution_plan`**（**不要从 `confirm_execution` 取，它不返回分享码**）；**执行不会改变分享码**——`confirm_execution` 原地修改该版本、不新建版本、不重新生成分享码，故第 11 步取到的分享码执行后依然有效，**切勿重新生成**。
- **分享查阅链接**：分享码取自第 11 步 `save_execution_plan` 返回的 `shareCode`（**不要从 `confirm_execution` 取，它不返回分享码**），用它拼接分享查阅 URL `https://rag.jueyun.net/share/{分享码}` 并以超链接输出，供用户在浏览器查看刚执行的版本（前端访问地址已由系统配置「系统外部网址」在技能下载时填充）。**执行不会改变分享码**：`confirm_execution` 原地修改该版本，不新建版本、不重新生成分享码，故第 11 步取到的分享码执行后依然有效，**切勿重新生成**。
- **与既有技能的职责边界**：本技能 = 「新建任务 + 一次跑完」；**仅生成计划不执行**用 `/eazy-rag-task-plan`，**对既有任务升版重生成**用 `/eazy-rag-task-replan`，**单独执行既有计划**用 `/eazy-rag-task-do`，**单加补充说明**用 `/eazy-rag-task-supply`。