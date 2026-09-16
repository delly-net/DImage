---
name: eazy-rag-task-supply
description: 添加任务计划补充说明（项目 ID: 3）——向任务最新执行计划添加补充说明
effort: low
user-invocable: true
disable-model-invocation: false
agent: general-purpose
---

# 添加任务计划补充说明

此技能用于向 EAZY.RAG 知识库当前项目「小D图像」（项目 ID: 3）中指定任务的最新执行计划添加一条补充说明，供版本级约束与后续调整记录；添加后该版本正文冻结为只读，修改需通过升版处理。

## 使用方法

```
/eazy-rag-task-supply {任务Id} {补充说明}
```

任务 ID 由参数 `{任务Id}` 提供或从上下文识别；未提供且无法识别时，按「确定目标任务 ID」步骤读取当前模块下非已归档任务列表供用户选择。

## 功能

1. **确定目标任务 ID**：任务 ID 由参数或上下文识别；若无法识别，使用 `/eazy-rag-module-get` 获取当前工作模块；若未设置（无值），先运行 `/eazy-rag-module-set` 设置当前工作模块；随后调用 `mcp__eazy-rag__list_tasks`（moduleId=当前模块Id）读取当前模块下**所有非已归档任务**列表，经 AskUser 询问用户选择目标任务。
2. **校验任务归属与状态**：调用 `mcp__eazy-rag__get_task`（taskId）→ 任务名称/描述/状态，校验任务归属当前项目且未归档（已归档任务不允许添加补充说明）。
3. **添加补充说明**：调用 `mcp__eazy-rag__add_task_supplementary_note`（taskId，content=`{补充说明}`）向任务**最新版本执行计划**添加一条补充说明；默认添加至最新版本，如需指定版本可传 `version`（可选）参数。
4. **展示结果**：将添加成功的补充说明记录（记录 ID、执行计划 ID、版本号、内容、创建时间）展示到对话中。

## 注意事项

- 所有 MCP 调用必须携带正确的项目 ID：3。
- **所有步骤非注明需要询问或确认时，无需询问和确认，直接执行**。
- **目标任务必须属于当前项目且未归档**：调用 `mcp__eazy-rag__get_task` 校验任务归属；已归档任务不允许添加补充说明。
- **补充说明内容不能为空**：`{补充说明}` 为必填参数。
- **添加位置为最新版本执行计划**：默认向最新版本添加补充说明；如需指定版本，可在调用 `add_task_supplementary_note` 时传 `version` 参数（可选）。
- **添加后该版本正文冻结为只读**：补充说明一经添加，该版本正文（Content）即冻结为只读，后续修改须通过 `save_execution_plan` 升版（Promote）创建新版本。
- **含补充说明版本禁止直接执行**：最新执行计划含补充说明后，任务计划执行技能 `/eazy-rag-task-do` 将判定不可直接执行，须先经 `/eazy-rag-task-replan` 升版生成新版本后再执行。