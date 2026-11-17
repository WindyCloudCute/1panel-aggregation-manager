# P4 只读资源清单：1Panel v2.3.2 API 路由与字段调研

- 调研日期：2026-11-16
- 目标版本：**1Panel v2.3.2**（tag `v2.3.2`，commit `65243c68c463cc055ab044093f641ea5d2e9e28b`，分支 `release-2.3.2` 同 commit）
- 调研方式：下载该 commit 的官方源码快照（`codeload.github.com/1Panel-dev/1Panel/tar.gz/65243c68...`，5.19 MB）+ 阅读后端路由/DTO/Service + 仓库内 OpenAPI（Swagger 2.0，756 个 path）+ 前端调用代码。**未调用任何真实节点、未使用任何密钥**。
- 交叉验证：与队友已对真实节点（v2.3.2）探测到的 5 条结果逐条对照，见「§7 交叉验证」。
- 禁止事项遵守：未修改设计/源码，未 git 提交。

> 实现位置说明：v2.3.2 起 REST 路由注册在 **`agent/router/ro_*.go`**（不是 `core/router`），由 `core` 的 `Proxy()` 中间件把 `/api/v2/*`（除 `/api/v2/core/*`）转发给本机 agent。因此**节点自身的 `/api/v2/...` 即可直接访问这些接口**（与真实探测结果一致）。
> 源码依据：[core/init/router/router.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/core/init/router/router.go)、[core/init/router/proxy.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/core/init/router/proxy.go)。

---

## 0. 通用契约（五类资源共用）

### 0.1 请求

- Base path：`/api/v2`（[core/cmd/server/main.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/core/cmd/server/main.go) 中 `@BasePath /api/v2`）。
- 列表/检索一律 **POST + JSON body**（没有 GET 版列表）；详情多混合 GET/POST。
- 鉴权：沿 P3 结论，HMAC-SHA256 头 `1Panel-Token: <hex 签名>`、`1Panel-Timestamp: <Unix 秒>`，签名 = `HMAC_SHA256(apiKey, "1panel:"+timestamp)` 小写 hex（见 `docs/research/p3-api-probe.md` §鉴权）。
- 分页/排序参数名固定为 **`page`、`pageSize`、`orderBy`、`order`**（JSON camelCase；Go `encoding/json` 对字段名大小写不敏感，但 .NET 端建议统一发 camelCase）。
  - `page`、`pageSize`：`validate:"required,number"` → **必填且 ≥1**（不是 offset 语义）。
  - `order` 合法值：**`"null"` / `"ascending"` / `"descending"`**（Mongo/JS 社区常见的 `asc/desc` 在本版本 **不合法**，会导致参数校验失败；源码另有 `constant.Asc="asc"` 但 `Order` 的 `oneof` 只接受前三个）。
  - 源码：[agent/app/dto/common_req.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/common_req.go)、[agent/constant/status.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/constant/status.go)。

### 0.2 响应封套

统一结构 `dto.Response`（[agent/app/dto/common_res.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/common_res.go)、[agent/app/api/v2/helper/helper.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/api/v2/helper/helper.go)）：

```json
{ "code": 200, "message": "", "data": { ... } }
```

- 成功：`code=200`，`message=""`，`data` 为 `PageResult` 或对象；`data==nil` 时返回 `{}`。
- 分页 `data`：`{ "total": <int64>, "items": [ ... ] }`（`dto.PageResult`，`total` 是过滤后的总条数，不是当前页长度）。
- 参数校验失败：**body 里** `{"code":400,"message":"参数错误: ...","data":{}}`；源码 `helper.ErrorWithDetail` 一律 `ctx.JSON(http.StatusOK, res)`，即**HTTP 状态码可能仍是 200**。见「§8 不确定项 U1」。
- 业务错误码字段：`errorCode`（`omitempty`），并非所有错误都带。

### 0.3 详情接口一览（五类是否"按 id 可取"）

| 资源 | 详情接口 | 备注 |
|---|---|---|
| 网站 | `GET /api/v2/websites/{id}` | 存在，返回完整 `WebsiteDTO` |
| 应用（已安装） | `GET /api/v2/apps/installed/info/{appInstallId}` | 存在，返回精简 `AppInstallInfo`；另有 `/params/{id}` |
| 容器 | `POST /api/v2/containers/info`（body `{"name":...}`）/ `POST /api/v2/containers/inspect`（body `{"id":...,"type":"container"}`） | 不是 `GET .../{id}`，而是 POST+name / POST+容器 ID |
| 数据库 | **未发现**「实例内单个数据库」的按 id 详情接口 | 列表字段足够；实例级有 `POST /databases/common/info`；远程库连接有 `GET /databases/db/{name}` |
| 计划任务 | `POST /api/v2/cronjobs/load/info`（body `{"id":N}`） | 存在，返回编辑表单 `CronjobOperate` |

---

## 1. 网站 Websites

### 1.1 列表

- **`POST /api/v2/websites/search`**
- 路由：[agent/router/ro_website.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/router/ro_website.go) → `baseApi.PageWebsite`
- 请求结构：`request.WebsiteSearch`（[agent/app/dto/request/website.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/request/website.go)）

| 字段 | 类型 | 必填 | 说明 / 合法取值 |
|---|---|---|---|
| `page` | int | ✅ | ≥1 |
| `pageSize` | int | ✅ | ≥1（前端默认 20） |
| `order` | string | ✅ | `null` \| `ascending` \| `descending` |
| `orderBy` | string | ✅ | `primary_domain` \| `type` \| `status` \| `createdAt` \| `expire_date` \| `created_at` \| `favorite` |
| `name` | string | ❌ | 模糊匹配：主域名/别名 + 站点域名（`WithSearchKeyword`，含域名表命中） |
| `type` | string | ❌ | 精确匹配站点类型：`static` / `proxy` / `runtime` / `subsite` / `deployment` / `stream`（无 validator，取值来自 [agent/constant/website.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/constant/website.go)） |
| `websiteGroupId` | int | ❌ | 分组过滤，`0` 或省略=全部分组 |

> 服务器实现会额外追加 `created_at desc, id desc` 作为次级排序（[agent/app/service/website.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/service/website.go) `PageWebsite`）。

- 响应：`data.items[]` 为 `response.WebsiteRes`（**不是**存储模型 `Website`）

| 字段 | 类型 | 示例 / 说明 |
|---|---|---|
| `id` | uint | `3` |
| `createdAt` | string(RFC3339) | `"2026-11-10T14:20:12+08:00"` |
| `protocol` | string | `HTTP` / `HTTPS` / `TCP/UDP` |
| `primaryDomain` | string | `"example.com"` |
| `type` | string | `static`/`proxy`/`runtime`/`subsite`/`deployment`/`stream` |
| `alias` | string | 站点名（1Panel 内部标识） |
| `remark` | string | 备注 |
| `status` | string | `Running` / `Stopped` |
| `expireDate` | string(RFC3339) | 到期日（默认 `9999-12-31`） |
| `sitePath` | string | 站点目录 |
| `appName` | string | 当 `type=deployment` 时 = 所装应用名，否则空 |
| `runtimeName` | string | 当 `type=runtime` 时 = 运行环境名 |
| `runtimeType` | string | 运行环境类型（php/node/python/...） |
| `appInstallId` | uint | 关联的已安装应用 ID |
| `sslExpireDate` | string(RFC3339) | 证书到期；无证书为零值时间 |
| `sslStatus` | string | `init` / `error` / `ready` / `applying` / `applyError`（由到期时间推导） |
| `childSites` | string[] | 子站（`type=subsite` 时有意义） |
| `parentSite` | string | 父站主域名 |
| `favorite` | bool | 收藏 |
| `IPV6` | bool | 注意 JSON 键是大写 `IPV6` |

示例（由 struct 推导，非真实节点响应）：

```json
{"code":200,"message":"","data":{"total":2,"items":[
  {"id":3,"createdAt":"2026-11-10T14:20:12+08:00","protocol":"HTTPS","primaryDomain":"example.com",
   "type":"static","alias":"example.com","remark":"","status":"Running","expireDate":"9999-12-31T00:00:00+08:00",
   "sitePath":"/opt/1panel/apps/openresty/openresty/www/sites/example.com","appName":"","runtimeName":"",
   "sslExpireDate":"2026-12-31T00:00:00+08:00","sslStatus":"ready","appInstallId":0,
   "childSites":[],"parentSite":"","runtimeType":"","favorite":false,"IPV6":true}
]}}
```

### 1.2 详情

- **`GET /api/v2/websites/{id}`**（`{id}` 为数字网站 ID，路径参数名 `:id`）→ `response.WebsiteDTO`
  - 结构 = `model.Website`（内嵌）+ 扩展字段。关键字段：`id`、`createdAt`、`updatedAt`、`protocol`、`primaryDomain`、`type`、`alias`、`remark`、`status`、`httpConfig`、`expireDate`、`proxy`、`proxyType`、`siteDir`、`errorLog`/`accessLog`、`defaultServer`、`IPV6`、`rewrite`、`webSiteGroupId`、`webSiteSSLId`、`runtimeID`、`appInstallId`、`ftpId`、`parentWebsiteID`、`user`、`group`、`dbType`、`dbID`、`favorite`、`streamPorts`、`domains[]`、`webSiteSSL{}`、`errorLogPath`、`accessLogPath`、`sitePath`、`appName`、`runtimeName`、`runtimeType`、`openBaseDir`、`algorithm`、`udp`、`servers[]`。
  - 源码：[agent/app/dto/response/website.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/response/website.go)、[agent/app/model/website.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/model/website.go)。
  - ⚠️ `webSiteSSL` 内含证书 PEM/私钥字段（`privateKey`/`pem`），只读展示不要落库/落日志。
- 备选（无分页全量）：**`GET /api/v2/websites/list`** → `WebsiteDTO[]`（含 domains/SSL，体积大；适合一次性快照，不适合 5 类聚合的首选）。

---

## 2. 应用（已安装） Apps Installed

### 2.1 列表

- **`POST /api/v2/apps/installed/search`**
- 路由：[agent/router/ro_app.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/router/ro_app.go) → `baseApi.SearchAppInstalled`
- 请求结构：`request.AppInstalledSearch`（[agent/app/dto/request/app.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/request/app.go)）

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `page` | int | ✅ | ≥1 |
| `pageSize` | int | ✅ | ≥1（前端有 `pageSize:10000` 的用法，无上限校验） |
| `name` | string | ❌ | 名称模糊匹配 |
| `tags` | string[] | ❌ | 应用标签 key 过滤 |
| `type` | string | ❌ | 应用类型过滤，**仅 `all:true` 时生效**（`Page` 分支未使用该字段） |
| `unused` | bool | ❌ | 仅 `all:true` 且 `type` 非空时：排除已被网站使用的应用 |
| `all` | bool | ❌ | `true` → 不分页返回全部匹配项，且 `total=len(items)` |
| `sync` | bool | ❌ | 返回前同步已装应用状态 |
| `checkUpdate` | bool | ❌ | 顺带检查可升级版本（较慢，P4 不用） |
| `update` | bool | ❌ | 仅返回有更新的应用 |

> 该接口 **没有 `orderBy`/`order` 参数**（服务端固定按 `favorite desc, sort_order asc` 排序）；探测中该接口 200 正确。

- 响应 `data.items[]` = `response.AppInstallDTO`（与真实探测字段完全吻合：`id,name,appID,appDetailID,version,status,message,httpPort,httpsPort,path...`）

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | uint | 安装记录 ID（`appInstallId`） |
| `name` | string | 安装名（容器名） |
| `appID` | uint | 应用 ID |
| `appDetailID` | uint | 应用版本详情 ID |
| `version` | string | 版本 |
| `status` | string | `Running`/`Stopped`/`Installing`/`InstallErr`/`Upgrading` 等（`constant.Status*`） |
| `message` | string | 异常信息（正常为空） |
| `httpPort` / `httpsPort` | int | 端口 |
| `path` | string | 安装目录 |
| `canUpdate` | bool | 是否可升级 |
| `icon` | string | 图标 URL |
| `appName` | string | 应用显示名 |
| `ready` / `total` | int | 就绪/全部容器数 |
| `appKey` | string | 应用 key（如 `mysql`） |
| `appType` | string | 应用分类类型（如 `runtime`/`php`/`database`） |
| `appStatus` | string | 应用状态 |
| `dockerCompose` | string | compose 文件内容（大字段，聚合列表可忽略） |
| `webUI` | string | Web 入口 |
| `createdAt` | string(RFC3339) | 安装时间 |
| `favorite` | bool | 收藏 |
| `sortOrder` | int | 自定义排序 |
| `app` | object | `{website,document,github}` |
| `container` | string | 容器名 |
| `isEdit` / `linkDB` | bool | 可编辑 / 关联数据库 |
| `resourceKeys` | string[] | 资源键 |
| `serviceName` | string | compose 服务名 |
| `env` | object | 环境变量 map |

实测样例（`page:1,pageSize:10`，真实节点）：

```json
{"code":200,"message":"","data":{"total":4,"items":[
  {"id":1,"name":"mysql","appID":37,"appDetailID":92,"version":"8.4.6","status":"Running",
   "message":"","httpPort":3306,"httpsPort":0,"path":"/opt/1panel/apps/mysql/mysql", "...":"..."}
]}}
```

### 2.2 详情

- **`GET /api/v2/apps/installed/info/{appInstallId}`** → `response.AppInstallInfo`
  - 字段：`id`、`name`、`version`、`status`、`message`、**`HttpPort`（JSON 键首字母大写，与列表的 `httpPort` 不一致，见 U2）**、`container`、`composePath`、`appKey`、`appPorts []int`、`env map`。
- **`GET /api/v2/apps/installed/params/{appInstallId}`** → `response.AppConfig`（安装参数表单，含 label/参数定义）。
- 轻量清单（无分页）：**`GET /api/v2/apps/installed/list`** → `dto.AppInstallInfo[]`，仅 `{id, key, name}`。

---

## 3. 容器 Containers

### 3.1 列表

- **`POST /api/v2/containers/search`**（注意：**无 GET 版本**，探测的 `GET /containers/search` 404 属预期）
- 路由：[agent/router/ro_container.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/router/ro_container.go) → `baseApi.SearchContainer`
- 请求结构：`dto.PageContainer`（[agent/app/dto/container.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/container.go)）

| 字段 | 类型 | 必填 | 说明 / 合法取值 |
|---|---|---|---|
| `page` / `pageSize` | int | ✅ | ≥1 |
| `state` | string | ✅ | **`all`** \| `created` \| `running` \| `paused` \| `restarting` \| `removing` \| `exited` \| `dead`（"全部"必须传 `"all"`，传空串会校验失败） |
| `orderBy` | string | ✅ | `name` \| `createdAt` \| `state`（服务端实现：`name` 按名称，其余按创建时间） |
| `order` | string | ✅ | `null` \| `ascending` \| `descending` |
| `name` | string | ❌ | 模糊匹配 **容器名 或 镜像名** |
| `filters` | string | ❌ | Docker label 过滤值（拼成 `label=<filters>`） |
| `excludeAppStore` | bool | ❌ | `true` 时排除「应用商店安装」产生的容器（label `createdBy=Apps`） |

- 响应 `data.items[]` = `dto.ContainerInfo`

| 字段 | 类型 | 说明 |
|---|---|---|
| `containerID` | string | Docker 容器 ID（短/长 ID，**不是整数**） |
| `name` | string | 容器名（已去掉 `/` 前缀） |
| `imageID` | string | 镜像 ID（去掉 `sha256:` 前缀后的段） |
| `imageName` | string | 镜像名 `repo:tag` |
| `createTime` | string | **`"2006-01-02 15:04:05"`** 格式（非 RFC3339） |
| `state` | string | `created`/`running`/`paused`/`restarting`/`removing`/`exited`/`dead` |
| `runTime` | string | Docker 状态文本，如 `"Up 3 hours"` |
| `network` | string[] | 容器 IP 列表（后端是 `[]string`；前端 TS 写成网络对象数组，属于前端类型错误） |
| `ports` | string[] | 简化端口串，如 `"0.0.0.0:8080->80/tcp"`、`"80/tcp"` |
| `isFromApp` | bool | 是否由应用商店安装 |
| `isFromCompose` | bool | 是否 compose 项目 |
| `appName` / `appInstallName` | string | 关联应用名 / 安装名 |
| `websites` | string[] | 关联站点主域名 |
| `isPinned` / `description` | bool / string | 置顶与备注（来自面板"容器描述"设置） |

> `total` = 过滤后总数；结果集内 **置顶项排最前**（在分页切片之后才做置顶排序，故仅影响当前页内顺序）。

### 3.2 详情

- **`POST /api/v2/containers/info`**，body `{"name":"<容器名>"}` → `dto.ContainerOperate`（重建/编辑表单：`name,image,hostname,domainName,dns,networks,ports(自定义),volumes,env,labels,restartPolicy,workingDir,cmd,entrypoint,cpuShares,memory,...`）。适合展示配置，不是只读 inspect 视图。
- **`POST /api/v2/containers/inspect`**，body `{"id":"<容器ID>","type":"container"}` → Docker inspect 原文（`data` 为 JSON 字符串；`type` 另支持 `"compose"`，用 compose 名 + `detail` 文件路径）。
- 实时指标：`GET /api/v2/containers/stats/{id}` → `dto.ContainerStats`（`cpuPercent,memory,cache,ioRead,ioWrite,networkRX,networkTX,shotTime`）；`GET /api/v2/containers/list/stats` → 列表项指标。
- 轻量清单：`POST /api/v2/containers/list`（body `{}`）→ `dto.ContainerOptions[]`，仅 `{name,state}`。
- 计数：`GET /api/v2/containers/status` → `dto.ContainerStatus`（各类 state 计数 + `containerCount/composeCount/imageCount/...`）。

---

## 4. 数据库 Databases

> 1Panel 的"数据库"不是一个平铺资源，而是 **按引擎、按实例**组织：先枚举实例，再按实例列出库；Redis 只有实例级状态。另有独立的"远程数据库连接"资源。

### 4.1 实例枚举（列表前置步骤）

- **`GET /api/v2/databases/db/list/{type}`** → `dto.DatabaseOption[]`
  - `type` 取值（来自 [agent/constant/app.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/constant/app.go)）：`mysql` / `mariadb` / `postgresql` / `redis` / `mongodb` / `mysql-cluster` / `postgresql-cluster` / `redis-cluster`
  - 每项：`{id,type,from(local|remote),database(实例名),version,address}`
- 备选（跨实例平铺、无分页）：**`GET /api/v2/databases/db/item/{type}`** → `dto.DatabaseItem[]`，每项 `{id, from, database(实例名), name(库名)}`。

### 4.2 MySQL / MariaDB 实例内的库列表

- **`POST /api/v2/databases/search`** → `SearchMysql`
- 请求结构：`dto.MysqlDBSearch`（[agent/app/dto/database.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/database.go)）

| 字段 | 类型 | 必填 | 说明 / 合法取值 |
|---|---|---|---|
| `page` / `pageSize` | int | ✅ | ≥1 |
| `database` | string | ✅ | **MySQL 实例名**（= 实例 app install 名，如 `"mysql"`），不是库名 |
| `orderBy` | string | ✅ | `name` \| `createdAt` |
| `order` | string | ✅ | `null` \| `ascending` \| `descending` |
| `info` | string | ❌ | 库名模糊匹配 |

- 响应 `data.items[]` = `dto.MysqlDBInfo`
  - `{id uint, createdAt RFC3339, name(库名), from(local|remote), mysqlName(实例名), format(字符集), collation(排序规则), isDelete bool, description string}`
  - 服务端会把 `username`/`password`/`permission` 清空后返回（列表不下发凭据）。

### 4.3 PostgreSQL / MongoDB 实例内的库列表

- **`POST /api/v2/databases/pg/search`** → 请求 = `dto.PostgresqlDBSearch`，字段与 MySQL 完全同构（`page/pageSize/database/orderBy/order/info`，`orderBy ∈ {name,createdAt}`）。
  - 响应 items = `dto.PostgresqlDBInfo`：`{id,createdAt,name,from,postgresqlName,format,username,password,superUser,isDelete,description}`
- **`POST /api/v2/databases/mongodb/search`** → `dto.MongodbDBSearch`（同上）。
  - 响应 items = `dto.MongodbDBInfo`：`{id,createdAt,name,from,mongodbName,username,password,isDelete,description}`

### 4.4 Redis

- **无数据库列表接口**。可用：`POST /api/v2/databases/redis/status`（实例状态）、`GET /api/v2/databases/redis/check`、`POST /databases/redis/conf`。
  - P4 对 Redis 只能展示"实例 + 状态"，不能列 db0~db15。

### 4.5 远程数据库连接（另一类资源，按需使用）

- 列表：**`POST /api/v2/databases/db/search`**，请求 `dto.DatabaseSearch`：`page,pageSize,orderBy(name|createdAt),order,type(引擎),info`；items = `dto.DatabaseInfo`（`{id,createdAt,name,from,type,version,address,port,initialDB,username,password,ssl,rootCert,clientKey,clientCert,skipVerify,timeout,description}`）。
- 详情：**`GET /api/v2/databases/db/{name}`** → `dto.DatabaseInfo`（`:name` 为连接名）。⚠️ 含密码字段。

### 4.6 实例级信息与详情

- **`POST /api/v2/databases/common/info`**，body `{"type":"mysql","name":"<实例名>"}` → `dto.DBBaseInfo {name, containerName, port}`。
- **`POST /api/v2/databases/status`**，body `dto.OperationWithNameAndType {type:"mysql", name:"<实例名>"}`（`type` 必填）→ 实例运行状态。
- **实例内单个数据库（MySQL/PG/Mongo 记录）没有按 id 的详情接口** → 列表字段足够（P4 详情页可用列表行数据 + 实例 base info）。

---

## 5. 计划任务 Cronjobs

### 5.1 列表

- **`POST /api/v2/cronjobs/search`**
- 路由：[agent/router/ro_cronjob.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/router/ro_cronjob.go) → `baseApi.SearchCronjob`
- 请求结构：`dto.PageCronjob`（[agent/app/dto/cronjob.go](https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/cronjob.go)）

| 字段 | 类型 | 必填 | 说明 / 合法取值 |
|---|---|---|---|
| `page` / `pageSize` | int | ✅ | ≥1 |
| `orderBy` | string | ✅ | `name` \| `status` \| `createdAt` |
| `order` | string | ✅ | `null` \| `ascending` \| `descending` |
| `info` | string | ❌ | 任务名模糊匹配 |
| `groupIDs` | uint[] | ❌ | 分组过滤；省略/空=全部（前端"全部分组"传 `[0, searchGroupID]`） |

- 响应 `data.items[]` = `dto.CronjobInfo`

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | uint | 任务 ID |
| `name` | string | 任务名 |
| `type` | string | 任务类型：`backupApp`/`backupWebsite`/`backupDatabase`/`backupDirectory`/`cleanLog`/`shell`/`curl`/`snapshot`/`clean`/... |
| `groupID` | uint | 分组 ID（0=默认） |
| `specCustom` | bool | 是否自定义 cron 表达式 |
| `spec` | string | cron 表达式 |
| `executor` | string | `shell`/`curl` 等执行器 |
| `scriptMode` / `script` | string | 脚本模式/内容 |
| `command` | string | 命令 |
| `containerName` / `user` | string | 执行容器/用户 |
| `scriptID` | uint | 脚本库 ID |
| `appID` / `website` / `dbType` / `dbName` / `url` | string | 备份/清理对象 |
| `exclusionRules` | string | 排除规则（正则） |
| `isDir` / `sourceDir` | bool/string | 目录任务 |
| `retainCopies` / `retryTimes` / `timeout` | int | 保留份数/重试/超时 |
| `ignoreErr` | bool | 忽略错误 |
| `snapshotRule` | object | `{withImage, ignoreAppIDs[]}` |
| `sourceAccounts` / `downloadAccount` | string[]/string | 备份账号（解析后名称） |
| `sourceAccountIDs` / `downloadAccountID` | string/uint | 原始账号 ID |
| `status` | string | **`Enable` / `Disable` / `Pending`** |
| `lastRecordStatus` | string | 最近一次执行：`Success`/`Failed`/`Running`/`Waiting`/`WaitingRestart`/空 |
| `lastRecordTime` | string | `"2006-01-02 15:04:05"`，无记录时为 `"-"` |
| `alertCount` | uint | 告警次数 |
| `secret` / `args` | string | 密钥/参数 |

### 5.2 详情

- **`POST /api/v2/cronjobs/load/info`**，body `{"id":N}` → `dto.CronjobOperate`（编辑表单，字段与 `CronjobInfo` 基本同构，含 `script`/`command`/`spec`/备份对象等；不含运行记录）。
- 执行记录：**`POST /api/v2/cronjobs/search/records`**，body `dto.SearchRecord {page,pageSize,cronjobID,startTime,endTime,status}` → items `dto.Record {id,cronjobID,taskID,startTime,records,status,message,targetPath,interval,file}`。

---

## 6. 前端调用方式（用于核对参数默认值）

| 资源 | 前端 API 函数（`frontend/src/api/modules/*.ts`） | 实际请求默认值（`frontend/src/views/**`） |
|---|---|---|
| 网站 | `searchWebsites` → `POST /websites/search`；`getWebsite(id)` → `GET /websites/${id}` | `{page, pageSize:20, orderBy:'favorite', order:'descending', websiteGroupId:0, type:''}`；表头排序映射 `primaryDomain→primary_domain`、`expireDate→expire_date`（`views/website/website/index.vue`） |
| 应用 | `searchAppInstalled` → `POST apps/installed/search`；`getAppInstalledByID` → `GET apps/installed/info/${id}` | `{page:1, pageSize:10000, name:'', tags:[], update:false, sync:false}`（`views/app-store/installed/index.vue`） |
| 容器 | `searchContainer` → `POST /containers/search` | `{state:'all', orderBy:'name', order:'ascending', pageSize:20}`（`views/container/container/index.vue`） |
| 数据库 | `searchMysqlDBs` → `POST /databases/search`；`listDatabaseOptions` → `GET /databases/db/list/${type}` | `{page, pageSize:20, info, database:<当前实例>, orderBy:'createdAt', order:'null'}`（`views/database/mysql/index.vue`） |
| 计划任务 | `searchCronjobs` → `POST /cronjobs/search` | `{page, pageSize:20, info, groupIDs, orderBy:'createdAt', order:'null'}`（`views/cronjob/cronjob/index.vue`） |

> 前端部分接口会带 `?operateNode=<节点名>` 查询参数或 `CurrentNode` 头，这是 **core 多节点代理**用的；PanelFleet 直连各自节点，**不需要**这些参数。

---

## 7. 交叉验证（与真实节点探测结果对照）

| 探测结果（队友） | 源码结论 | 一致性 |
|---|---|---|
| `POST /apps/installed/search {page:1,pageSize:10}` → 200，`data.total=4`，items 含 `id,name,appID,appDetailID,version,status,message,httpPort,httpsPort,path` | `SearchAppInstalled` → `dto.PageResult{total,items:[]AppInstallDTO}`，字段逐一吻合 | ✅ |
| `POST /websites/search` 缺 `OrderBy`/`Order` 报 400，结构名 `WebsiteSearch` | `request.WebsiteSearch` 的 `order`/`orderBy` 带 `validate:"required"`；validator 报错信息会带字段名 | ✅（`400` 见 U1：应为 body `code`） |
| `POST /databases/search` 报 `MysqlDBSearch` 要求 `Database`/`OrderBy`/`Order` | `dto.MysqlDBSearch` 三字段均 required | ✅ |
| `POST /cronjobs/search` 要求 `OrderBy`/`Order`，结构名 `PageCronjob` | `dto.PageCronjob` 两字段 required | ✅ |
| `POST /containers/search` 要求 `State`/`OrderBy`/`Order`，结构名 `PageContainer` | `dto.PageContainer` 三字段 required，`state` 枚举含 `all` | ✅ |
| `GET /containers/search`、`GET /websites`、`GET /databases` → 404 | 三者只有 POST（创建/检索）；`GET /websites` 无路由（详情是 `GET /websites/{id}`、全量是 `GET /websites/list`） | ✅ |

补充交叉：P3 已实测 `GET /api/v2/dashboard/base/all/all` 返回 `websiteNumber/appNumber/databaseNumber/cronjobNumber`，与本文件 5 类资源对应，可作总览计数（见 `docs/research/p3-api-probe.md`）。

---

## 8. 不确定项（需真实节点二次确认，或在 P4 实现时做防御）

- **U1 参数错误的 HTTP 状态码**：源码 `helper.ErrorWithDetail` 统一 `ctx.JSON(http.StatusOK, res)`，即 HTTP 200 + body `code:400`；但探测记录写的是"报 400"。建议 PanelFleet 判定**以 body `code` 为准**，不要只看 HTTP 状态码。验证方式（后续 P4 联调）：发一次缺 `orderBy` 的 `POST /api/v2/websites/search` 并打印 HTTP 状态码。
- **U2 `GET /apps/installed/info/{id}` 的 `HttpPort` 键名**：JSON tag 写成 `json:"HttpPort"`（首字母大写），与列表的 `httpPort` 不一致。.NET 反序列化需大小写不敏感，或分别标注 `[JsonPropertyName]`。
- **U3 `databases/db/list/{type}` 的 `type` 语义**：`type` 直接进 `WithTypeList(dbType)`；`mysql` 是否同时覆盖 `mariadb`（逗号分隔/集合解析）未逐行确认。建议按 `mysql`、`mariadb` 各调一次合并去重。
- **U4 网站列表里的 `childSites`/`parentSite`/`sslStatus` 边界值**：`sslStatus` 由到期时间推导（无证书时 `init`）；真实节点空库可能返回零值时间（`0001-01-01T00:00:00Z`），需前端格式化兜底。
- **U5 `containers/search` 的 `filters`**：是 label 过滤值字符串，P4 只读清单建议不传。
- **U6 权限**：API Key 对应角色是否具备这 5 类资源的读权限（RBAC）未验证；若 403/`code:403`，需在节点侧给 API Key 授只读角色。
- **U7 版本漂移**：本文锁定 v2.3.2 源码；后续节点升级（v2.4+ 已有 `release-2.4.x`/`2.5.x`）可能增字段。建议 P4 客户端对未知字段宽容（忽略），对必填检索字段做"缺省补齐"（见 §9）。

---

## 9. P4 建议调用序列（每节点）

1. 网站：`POST /api/v2/websites/search` body `{page,pageSize,orderBy:"created_at",order:"descending"}`
2. 应用：`POST /api/v2/apps/installed/search` body `{page,pageSize}`（无需排序字段；需要全量可 `{page:1,pageSize:10000}` 或 `all:true`）
3. 容器：`POST /api/v2/containers/search` body `{page,pageSize,state:"all",orderBy:"name",order:"ascending"}`
4. 数据库：
   - `GET /api/v2/databases/db/list/mysql`（及 `postgresql`、`mongodb`）拿实例列表；
   - 对每个实例 `POST /api/v2/databases/search` / `pg/search` / `mongodb/search`，body `{page,pageSize,database:<实例名>,orderBy:"createdAt",order:"null"}`；
   - Redis 只取实例状态 `POST /databases/redis/status`。
5. 计划任务：`POST /api/v2/cronjobs/search` body `{page,pageSize,orderBy:"createdAt",order:"null"}`

> 所有必填字段即使"没有偏好"也要显式发送：`order:"null"`、`orderBy` 用上表默认值、`state:"all"`、`database:<实例名>`。只读清单不要调用任何 `POST /del`、`/operate`、`/update`、`/op` 类接口。

---

## 10. 来源清单（均固定到 commit `65243c68c463cc055ab044093f641ea5d2e9e28b`）

后端路由：
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/router/ro_website.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/router/ro_app.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/router/ro_container.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/router/ro_database.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/router/ro_cronjob.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/core/init/router/router.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/core/init/router/proxy.go>

请求 / 响应 DTO：
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/common_req.go>（PageInfo）
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/common_res.go>（PageResult / Response）
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/request/website.go>（WebsiteSearch）
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/response/website.go>（WebsiteRes / WebsiteDTO）
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/request/app.go>（AppInstalledSearch）
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/response/app.go>（AppInstallDTO / AppInstallInfo）
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/container.go>（PageContainer / ContainerInfo / ContainerOperate）
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/database.go>（MysqlDBSearch / MysqlDBInfo / DatabaseOption / DatabaseInfo）
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/database_postgresql.go>（PostgresqlDBSearch / PostgresqlDBInfo）
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/cronjob.go>（PageCronjob / CronjobInfo / CronjobOperate / SearchRecord / Record）
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/api/v2/helper/helper.go>（统一响应/校验）

Service 实现（排序、过滤、置顶等行为）：
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/service/website.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/service/app_install.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/service/container.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/service/database.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/service/database_mysql.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/service/cronjob.go>

OpenAPI / 版本：
- 仓库内 Swagger：<https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/core/cmd/server/docs/swagger.json>（Swagger 2.0，756 paths；节点上可访问 `/1panel/swagger/index.html`）
- tag v2.3.2：<https://github.com/1Panel-dev/1Panel/tree/v2.3.2>，commit：<https://github.com/1Panel-dev/1Panel/commit/65243c68c463cc055ab044093f641ea5d2e9e28b>

前端：
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/frontend/src/api/modules/website.ts>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/frontend/src/api/modules/app.ts>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/frontend/src/api/modules/container.ts>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/frontend/src/api/modules/database.ts>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/frontend/src/api/modules/cronjob.ts>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/frontend/src/api/interface/index.ts>（ReqPage / ResPage）
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/frontend/src/views/website/website/index.vue>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/frontend/src/views/app-store/installed/index.vue>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/frontend/src/views/container/container/index.vue>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/frontend/src/views/database/mysql/index.vue>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/frontend/src/views/cronjob/cronjob/index.vue>
