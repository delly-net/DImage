---
name: eazy-rag-module-get
description: 获取当前RAG知识库工作模块上下文（项目 ID: 3）——读取项目当前工作模块
effort: medium
user-invocable: true
disable-model-invocation: false
agent: general-purpose
---

# 获取当前RAG知识库模块信息

此技能用于获取 EAZY.RAG 知识库当前项目「小D图像」（项目 ID: 3）的当前工作模块上下文。

## 使用方法

```
/eazy-rag-module-get
```

或在聊天中输入「获取当前模块」触发。

## 功能

1. **读取上下文**：读取本地上下文标记文件 `~/.eazyrag/context/3.json`（JSON：currentModuleId/currentModuleName/updatedAt）。
2. **返回当前模块**：若文件存在，返回当前工作模块信息（模块 ID、模块名、更新时间）。
3. **未设置提示**：若文件不存在，提示「尚未设置当前工作模块」，可运行 `/eazy-rag-module-set` 设置。

## 注意事项

- 上下文标记文件由「设置上下文RAG知识库模块信息」技能（/eazy-rag-module-set）写入。
- 本技能为只读，不修改上下文。