# ADR-0002 使用 ASP.NET Core + Blazor Server 作为控制台形态

- 状态：提议（P0）
- 日期：2026-11-15
- 关联：[架构设计](../01-架构设计.md)

## 背景

工具需要跨节点实时展示、后续可能扩展监控与操作；环境只有 .NET 10 SDK，优先单技术栈、易部署。

候选：Blazor Server Web、Avalonia 桌面、Web API + React/Vue、CLI。

## 决定

采用 **ASP.NET Core（net10.0）+ Blazor Server（Interactive Server）+ SignalR**，单进程交付，浏览器访问。

## 理由

- 单技术栈，无需 Node/前端构建链；
- SignalR 天然支持 Fan-out 增量推送与实时刷新；
- 跨平台，Windows 可前台运行或装服务，后续可容器化；
- 与后端 OIDC、Cookie、Antiforgery 集成路径最短。

## 后果

- 优点：开发效率高、实时能力好、部署简单。
- 代价：桌面原生感弱；长连接对反向代理有要求；断线重连与状态恢复需处理（P2/P3）。
- 若未来要求桌面分发，可复用 Domain/Client/Application，仅替换 Web 层为 Avalonia；不影响核心架构。
