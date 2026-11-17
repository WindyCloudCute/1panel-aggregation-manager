# P4 五类资源接口真实探测记录（1Panel v2.3.2）

- 探测时间：2026-11-16 20:40（Asia/Shanghai）
- 节点：`http://198.51.100.10:1000`（1Panel `v2.3.2`，Debian 13 LXC）
- 鉴权：HMAC-SHA256，`1Panel-Token` = 签名、`1Panel-Timestamp` = Unix 秒；密钥未写入本文件。
- 原始响应文件：`%TEMP%\p4probe\*.json`；以下为真实响应摘录与字段结论。
- 路由来源：GitHub `1Panel-dev/1Panel@dev-v2` 的 `agent/router/*.go` 与 `frontend/src/api/modules/*.ts`（见 `docs/research/p4-api-routes.md`）。

## 0. 节点上的真实资源量

| 资源 | 数量 | 说明 |
|---|---|---|
| 网站 | 0 | `dashboard/base/all/all` 的 `websiteNumber=0` |
| 应用（已安装） | 4 | uptime-kuma 等 |
| 容器 | 8 | Docker 容器（`/containers/search` total=8） |
| 数据库 | 5 | 均为远程 MySQL 实例 `MySQL` 下的库 |
| 计划任务 | 0 | `cronjobNumber=0` |

> 该节点足以验证「应用/容器/数据库」列表与空列表（网站/计划任务）行为；假面板需补网站与计划任务样例。

## 1. 网站

- 列表：`POST /api/v2/websites/search`
  - 请求体（`WebsiteSearch`）：`{ page, pageSize, name, orderBy, order, websiteGroupId }`
  - **`orderBy`、`order` 必填**；实测 `orderBy=created_at`、`order=descending` 通过（缺省时报 400 required）
  - 空节点响应：`{"code":200,"data":{"total":0,"items":null}}`
  - 每项（`WebsiteRes`）关键字段：`id, createdAt, protocol, primaryDomain, type, alias, remark, status, expireDate, sitePath, appName, runtimeName, sslExpireDate`
- 详情：`GET /api/v2/websites/{id}` → `WebsiteDTO`（含域名、SSL、运行环境等完整字段）
- 其他列表：`GET /api/v2/websites/list`（`WebsiteDTO[]`，无分页）

## 2. 应用（已安装）

- 列表：`POST /api/v2/apps/installed/search`
  - 请求体（`AppInstalledSearch`）：`{ page, pageSize, type?, unused?, all? }`；实测只传 `{page,pageSize}` 返回 200
  - 每项（`AppInstallDto`）关键字段：`id, name, appID, appDetailID, version, status, message, httpPort, httpsPort, path, canUpdate, icon, appName, ready, total, appKey, appType`
  - 实测 4 条，例如：
    ```json
    {"id":2,"name":"uptime-kuma","appID":59,"appDetailID":1117,"version":"1.23.17","status":"Running","message":"1Panel-uptime-kuma-p32P 容器不存在","httpPort":9010,"httpsPort":0,"path":"/1panel/1panel/apps/uptime-kuma/uptime-kuma"}
    ```
- 详情：`GET /api/v2/apps/installed/info/{appInstallId}`
  - 实测返回：`id, name, version, status, message, HttpPort, container, composePath, appKey, appPorts, env`

## 3. 容器

- 列表：`POST /api/v2/containers/search`
  - 请求体（`ContainerSearch`）：`{ page, pageSize, name, state, filters, orderBy, order }`
  - **`state`、`orderBy`、`order` 必填**；实测 `state=all`、`orderBy=name|state` 通过；`order=descending` 通过
  - 每项（`ContainerInfo`）字段（实测）：
    `containerID, name, imageID, imageName, createTime, state, runTime, network[], ports[], isFromApp, isFromCompose, appName, appInstallName, websites, isPinned, description`
- 详情：
  - `POST /api/v2/containers/info` body `{name}` → `ContainerHelper`（名称、镜像、网络、端口、env、资源限制等）；实测成功
  - `POST /api/v2/containers/inspect` body 需要 `{id, type}`（containerID + 类型），未确认 `type` 取值，P4 暂用 `/containers/info`
  - 统计：`GET /api/v2/containers/list/stats?ids=a,b`、`GET /api/v2/containers/stats/{id}`（P4 可选，不纳入首版）

## 4. 数据库（分引擎）

1Panel v2 的数据库分两层：**数据库实例**（MySQL/PostgreSQL/MongoDB，来自远程或应用）→ **库**。

- 实例列表（按引擎）：
  - `GET /api/v2/databases/db/list/mysql`
  - `GET /api/v2/databases/db/list/postgresql`
  - `GET /api/v2/databases/db/list/mongodb`
  - 返回 `DatabaseOption[]`：`{id, from, type, database, version, address}`
  - 实测 MySQL：`[{"id":1,"type":"mysql","from":"remote","database":"MySQL","version":"8.x","address":"198.51.100.11"}]`；PG/Mongo 为 `data:null`
- 库列表：
  - MySQL：`POST /api/v2/databases/search`，body `{page,pageSize,database:"<实例名>",orderBy,order}`（`database` 为**实例名**，必填）
  - PostgreSQL：`POST /api/v2/databases/pg/search`，body `{page,pageSize,database:"<实例名>",orderBy,order}`
  - MongoDB：`POST /api/v2/databases/mongodb/search`，body `{page,pageSize,database:"<实例名>",orderBy,order}`
  - 实测 MySQL：`total=5`，例如 `{"id":5,"name":"word_rcscdx","mysqlName":"MySQL","format":"utf8mb4","collation":"utf8mb4_0900_ai_ci","from":"remote","description":""}`
  - MySQL 库字段：`id, createdAt, name, mysqlName, from, format, collation, username, password, permission, isDelete, description`
  - PG 库字段（TS 类型）：`id, createdAt, name, postgresqlName, from, format, username, password, superUser, isDelete, description`
  - MongoDB 库字段（TS 类型）：`id, createdAt, name, mongodbName, from, username, password, isDelete, description`
- 说明：`/api/v2/databases/db/search` 是「外部数据库连接」清单（`DatabaseInfo`，含 address/port/type），**不是** MySQL/PG/Mongo 下的库；实测 total=0。P4 若展示库，必须走「实例列表 → 每实例库列表」。

## 5. 计划任务

- 列表：`POST /api/v2/cronjobs/search`
  - 请求体（`Cronjob.Search`）：`{ page, pageSize, info, groupIDs, orderBy?, order? }`
  - 实测 `orderBy` 合法值包含 `name`、`status`、`createdAt`；`created_at` 非法（400 oneof）
  - 空节点响应 `{"code":200,"data":{"total":0,"items":null}}`
  - 每项（`CronjobInfo`）关键字段：`id, name, type, groupID, spec, specs[], specObjs[], executor, isExecutorCustom, script, scriptMode, command, inContainer, containerName, user, appID, website, dbType, dbName, url, retainCopies, ignoreErr, retryTimes, timeout, status, alertCount, alertMethod, scopes[], args`
- 详情：`POST /api/v2/cronjobs/load/info` body `{id}` → `CronjobOperate`（完整可编辑字段）
- 执行记录：`POST /api/v2/cronjobs/search/records`（P4 可选，不纳入首版）

## 6. 排序/分页参数汇总

| 资源 | 列表接口 | 分页字段 | 排序字段（实测/TS） | 过滤 |
|---|---|---|---|---|
| 网站 | `POST /websites/search` | `page/pageSize` | `orderBy`(name/created_at 等) + `order` | `name`、`websiteGroupId` |
| 应用 | `POST /apps/installed/search` | `page/pageSize` | 无 | `type`、`unused`、`all` |
| 容器 | `POST /containers/search` | `page/pageSize` | `orderBy`(name/state) + `order` | `name`、`state`、`filters` |
| 数据库 | `POST /databases/{...}/search` | `page/pageSize` | `orderBy`(name) + `order` | `info`、`database`(实例名) |
| 计划任务 | `POST /cronjobs/search` | `page/pageSize` | `orderBy`(name/status/createdAt) + `order` | `info`、`groupIDs` |

- `order` 实测值：`descending` 可用；`ascending` 为对应方向（源码头文件确认）。
- 容器 `state` 实测 `all` 可用；常见取值 `all/running/stopped`（实现时以真实节点为准，先只传 `all`）。

## 7. 对 P4 设计的影响

1. 每节点每类资源 1 次列表调用（网站/应用/容器/计划任务）；**数据库为 1~3 次实例列表 + 每实例 1 次库列表**（实例数通常很少）。
2. 列表字段已足够展示表格；详情接口只对网站/应用/容器/计划任务追加，数据库详情用列表字段。
3. 空结果用 `items:null` 表示，DTO 需容忍 null。
4. 容器列表返回 `ports[]`、`network[]` 结构不一，展示时只取字符串形式或简单拼接。
5. 计划任务 `spec/specs` 与 `status` 是主要展示字段；`type` 区分脚本/备份/网站等。
