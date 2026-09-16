---
name: eazy-rag-task-archive
description: 任务总结归档（项目 ID: 3）——根据任务执行历史生成项目执行规范/注意事项/依赖关系补充内容并更新到项目信息，生成任务总结写入「任务总结」字段，提取关联任务/参考文件填充子表
effort: high
user-invocable: true
disable-model-invocation: false
agent: general-purpose
---

# 任务总结归档

此技能用于对 EAZY.RAG 知识库当前项目「小D图像」（项目 ID: 3）的指定任务进行总结归档：结合项目执行规范/注意事项/依赖关系与任务执行历史，分别生成三份文档的补充内容并合并更新到项目信息；并在置「已归档」前生成任务总结写入「任务总结」字段、提取任务关联信息与参考文件填充两个子表。

## 使用方法

```
/eazy-rag-task-archive {任务Id}
```

任务 ID 由参数 `{任务Id}` 提供或从上下文识别；未提供且无法识别时，按「确定目标任务」步骤读取当前模块下非已归档任务列表供用户选择。

或在聊天中输入「任务总结归档」触发。

## 功能

1. **获取项目信息**：调用 `mcp__eazy-rag__get_project`（projectId=3）→ 读取 executionSpecs / executionNotes / dependencies。
2. **确定目标任务**：用户提供任务 ID（或在当前模块任务列表中选择）；调用 `mcp__eazy-rag__get_task` 校验任务归属当前项目。
3. **获取任务执行历史**：
   - 调用 `mcp__eazy-rag__get_execution_plan`（taskId，默认版本）→ 最新版本执行计划正文 + 执行结果 + 该版本补充说明 + **返回的 relatedTaskIds / referencedFileIds**（供关联提取）；
   - 调用 `mcp__eazy-rag__list_execution_plan_versions`（taskId）→ 所有版本的元信息与补充说明（**不含执行计划正文**）。
4. **生成补充内容**：基于执行计划正文、执行结果、全部版本补充说明，分别为「执行规范 / 注意事项 / 依赖关系」提炼项目层面新增内容（增量补充，避免重复原文）。
5. **合并更新**：原始文档 + 补充内容 → 新的执行规范/注意事项/依赖关系；某份无补充内容则维持原样。调用 `mcp__eazy-rag__update_project`（projectId=3）写入三份新文档（覆盖前展示新旧差异并经用户确认）。
6. **【新增】生成任务总结**：基于执行计划正文、执行结果、全部版本补充说明，生成任务总结（任务目标 / 主要产出 / 关键经验 / 遗留事项），调用 `mcp__eazy-rag__update_task`（taskId={目标任务ID}，summary=任务总结）更新到「任务总结」字段——**须在置「已归档」前完成**（已归档任务不允许修改任务总结）。
7. **【新增】提取关联任务与参考文件**：
   - **关联任务**：从第 3 步 `get_execution_plan` 返回的 `relatedTaskIds` 提取关联历史任务，逐项调用 `mcp__eazy-rag__get_task`（taskId=关联任务Id）解析任务名称，组装为 `[{relatedTaskId, relatedTaskName, relationRemark}]`，调用 `mcp__eazy-rag__save_task_related_tasks`（taskId={目标任务ID}，relatedTasks=JSON 数组）**覆盖式**写入「任务关联任务」子表；
   - **参考文件**：从第 3 步 `get_execution_plan` 返回的 `referencedFileIds` 提取执行计划引用的参考文档，调用 `mcp__eazy-rag__list_references`（projectId=3）映射文档名称与类型，组装为 `[{referenceDocumentId, referenceDocumentName, fileType}]`，调用 `mcp__eazy-rag__save_task_reference_files`（taskId={目标任务ID}，referenceFiles=JSON 数组）**覆盖式**写入「任务参考文件」子表（仅取执行计划引用的文件，不并入任务维度参考文档）。
8. **更新任务状态**：调用 `mcp__eazy-rag__update_task`（taskId={目标任务ID}，status=Archived）将目标任务状态更新为「已归档」。

## 注意事项

- 所有 MCP 调用必须携带正确的项目 ID：3。
- **所有步骤非注明需要询问或确认时，无需询问和确认，直接执行**。
- 补充说明仅作分析输入，**不含执行计划正文**，不得将执行计划正文并入项目文档。
- 更新项目信息（覆盖 MD 字段）前须经用户确认，展示新旧差异。
- 无补充内容的三份文档之一应维持原样，勿清空或改写。
- **任务总结须在置「已归档」前写入**：`update_task` 对已归档任务禁止修改任务总结，故第 6 步须先于第 8 步执行。
- **子表覆盖式写入（整体替换、幂等）**：`save_task_related_tasks` / `save_task_reference_files` 为覆盖式保存，重复归档不产生重复记录。
- **参考文件仅取执行计划引用的文件**（`referencedFileIds`），不并入任务维度参考文档。
- 子表名称字段（relatedTaskName / referenceDocumentName）为归档时快照，后续源数据变化不影响展示。
- 归档完成后将任务状态更新为「已归档」（Archived）；「已归档」的任务不允许再进行编辑与执行。