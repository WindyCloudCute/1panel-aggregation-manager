# P20 写操作 API 契约：1Panel v2.3.2 路由/参数/行为调研

- 调研日期：2026-11-17
- 目标版本：**1Panel v2.3.2**（tag `v2.3.2`，commit `65243c68c463cc055ab044093f641ea5d2e9e28b`）
- 调研方式：官方源码快照（`codeload.github.com` tar.gz）本地阅读——后端路由 `agent/router/ro_*.go`、`core/router/ro_*.go`、DTO、Service、前端 `frontend/src/api/modules/*.ts` 与页面组件。**本轮未调用真实节点、未使用任何密钥**；真实节点只读接口的实测结论见 `docs/research/p4-api-routes.md` §7 与 `p3-api-probe.md`。
- 与 P4 文档的关系：`p4-api-routes.md` 已覆盖**只读**列表/详情/字段与通用契约，本文只写**写操作/CRUD/升级/面板设置**，通用封套、分页、鉴权不再重复。
- 禁止事项遵守：未修改源码/配置，未 git 提交，全文脱敏（无 API Key、无节点地址）。

> 重要前提：v2.3.2 中 `/api/v2/*`（除 `/api/v2/core/*`）由 core 的 Proxy 中间件转发给本机 agent，所以节点根地址 `/api/v2/...` 直接可用；**面板设置/认证在 `/api/v2/core/...`**。两套路由都接受 API Key HMAC（`SessionAuth`/`PasswordExpired` 中间件对 `API_AUTH` 直接放行，见 `core/middleware/session.go:15`、`core/middleware/password_expired.go`）。

---

## 0. 写操作通用契约（本文所有接口共用）

### 0.1 封套与错误

同 P4：`{"code":200,"message":"","data":...}`；HTTP 可能始终 200，**业务成败看 `code`**。参数校验失败 `code=400`（`message` 带「参数错误: ...」）；业务错误 `code=500`，部分带 `errorCode`（如 `ErrRecordExist`、`ErrNameIsExist`、`ErrSSLValid`）。

### 0.2 异步任务（Compose / 网站 / 应用升级 的关键）

写接口返回 success 往往只代表**任务已提交**，真正执行在后台 `task` 中：

| 接口 | 说明 |
|---|---|
| `POST /api/v2/tasks/search` | body `{status,type,taskID,page,pageSize}`（`SearchTaskLogReq`，`agent/app/dto/logs.go:9`），返回 `{total,items:[TaskDTO]}` |
| `POST /api/v2/tasks/read` | 按行读任务日志（`TaskLogReadReq`，字段未逐一核对，**待实测**） |
| `GET /api/v2/tasks/executing/count` | 当前执行中任务数 |

`Task` 字段（`agent/app/model/task.go`）：`id,name,type,operate,logFile,status,errorMsg,operationLogID,resourceID,currentStep,endAt,createdAt`；`status` 取值 `Waiting|Running|Success|Failed`（`agent/constant/status.go`）。

约定：客户端生成 `taskID`（uuid）放入请求 body，随后用 `tasks/search?taskID=` 或前端任务日志组件轮询。**P20 建议**：写操作内核 `OperationStepResult.TaskId` 记录该 taskID，前端“已提交 + 轮询 + 超时兜底”复用 P5/P10 模式。

### 0.3 大小写与幂等

- Go `encoding/json` 对字段名大小写不敏感，但**建议统一 camelCase**；个别字段源码就是混合大小写：`webSiteGroupID`（创建网站）、`IPV6`、`appInstallID`。
- 幂等冲突错误：Compose 项目名/路径已存在 → `ErrRecordExist`；网站 alias 已存在 → `ErrAliasIsExist`；反向代理同名文件存在 → `ErrNameIsExist`。

---

## 1. 应用升级（A：task-24 硬依赖）

### 1.1 上游读取接口（P4 已覆盖，这里只列升级所需）

| 用途 | 方法/路径 | body / 返回要点 |
|---|---|---|
| 已安装列表 | `POST /api/v2/apps/installed/search` | `{page,pageSize,type?,name?,tags?,update?,unused?,all?,sync?,checkUpdate?}`；item 含 `id,appId,appDetailId,name,appKey,version,status,httpPort,update,...` |
| 已安装详情 | `GET /api/v2/apps/installed/info/{appInstallId}` | 精简信息（composePath、appKey、ports、env 等） |
| 当前参数 | `GET /api/v2/apps/installed/params/{id}` | `{params[],rawCompose,cpuQuota,memoryLimit,memoryUnit,containerName,allowPort,restartPolicy,dockerCompose,webUI,type}`（详见 §5） |

### 1.2 可升级版本列表（版本来源）

```
POST /api/v2/apps/installed/update/versions
{ "appInstallID": 12 }                    // 可选：{ "appInstallID": 12, "updateVersion": "8.0.32" }
```

返回 `data`：`[{ "version": "8.0.33", "detailId": 57, "dockerCompose": "<整份 yml 文本>" }]`（按版本升序）。

过滤/排序逻辑（`agent/app/service/app_install.go:663 GetUpdateVersions`）：

1. 取同 `appId` 的全部 `app_details`，排除当前已装版本；
2. 排除 `ignore_upgrade` 记录（scope=version）与无权限应用（vllm 类）；
3. 大版本规则：应用 `CrossVersionUpdate=false` 时跳过跨大版本；
4. 仅保留比已装版本新的版本；MySQL 只保留同 major（`8.0.x`）；
5. `dockerCompose` = 目标版本 compose **合并当前 env/参数后**的结果（可直接给用户看 diff）；
6. 请求指定 `updateVersion` 且该版本 compose 缺失时，按 `downloadUrl` 同目录补拉 `docker-compose.yml`。

> 版本数据来自节点本地 `app_details`（由 `POST /api/v2/apps/sync/remote|sync/local` 同步），不是实时远程查询。

### 1.3 升级执行

```
POST /api/v2/apps/installed/op
{
  "installId": 12,
  "operate": "upgrade",
  "detailId": 57,                 // 1.2 返回的 detailId
  "backup": true,
  "pullImage": true,
  "deleteImage": false,
  "dockerCompose": "",            // 可选；非空则覆盖目标编排（前端仅在用户勾选“使用新 compose”时回传）
  "taskID": "4f0c...uuid"
}
```

- `operate` 全枚举（`agent/constant/app.go:37`）：`start|stop|restart|delete|sync|backup|update|rebuild|upgrade|reload|favorite`。
- 全字段（`AppInstalledOperate`）：`installId*`、`backupId`、`detailId`、`operate*`、`forceDelete`、`deleteBackup`、`deleteDB`、`backup`、`pullImage`、`dockerCompose`、`taskID`、`deleteImage`、`favorite`。
- 服务端行为（`app_install.go:298`）：组装 `AppInstallUpgrade{installId,detailId,backup,pullImage,deleteImage,dockerCompose,taskID}` → `upgradeInstall` 异步任务（备份 → 拉镜像 → 重建容器）；失败信息在 `Task.errorMsg`。**无自动回滚**。
- 升级前校验：没有独立“预检”接口；前端流程 = 取版本列表（无版本则禁用升级）→ 用户勾选备份/拉镜像 → 提交。服务端仅校验参数与 detail 存在。
- 前端实际请求体（`frontend/src/views/app-store/installed/upgrade/index.vue:123`）：`{detailId,operate:'upgrade',installId,backup:true,pullImage:true,deleteImage:false,version,dockerCompose,taskID}`；其中 `version` 是展示用，服务端不读。

### 1.4 批量升级可行性

**1Panel 无批量升级接口**。批量 = 逐节点 × 逐应用调用同一 `POST apps/installed/op`（每次新 `taskID`）。

- PanelFleet 已有实现可复用：`app.update`（单节点 High / 多节点 Critical）与 `app.batch_upgrade`（批量 Critical，节点串行、同节点应用串行，任一失败停止后续）。
- 建议并发：节点间 2–3 并发；**同一节点内串行**（升级重建容器会抢端口/磁盘，且 1Panel 任务并发限制未知，**待实测**）。

### 1.5 现状差异（供 task-24 对照）

PanelFleet 已实现 `AppStoreRoutes.InstalledUpdateVersions` 与 `app.update` 请求体，与上面源码契约一致；task-24 若要补“版本列表/升级确认页”，契约按 §1.1–1.4 即可，无需新接口。

---

## 2. 网站（B：建站/更新/详情/域名/SSL/反向代理）

鉴权同上。网站创建/删除/批量操作均为**异步任务**（带 `taskID`），启停为同步（但会 reload nginx）。

### 2.1 路由总表

| 功能 | 方法/路径 | 请求要点 |
|---|---|---|
| 创建 | `POST /api/v2/websites` | `WebsiteCreate`（§2.2） |
| 更新基础信息 | `POST /api/v2/websites/update` | `{id*,primaryDomain*,remark,webSiteGroupID,expireDate,IPV6,favorite}` |
| 删除 | `POST /api/v2/websites/del` | `{id*,deleteApp,deleteBackup,forceDelete,deleteDB}` |
| 启停 | `POST /api/v2/websites/operate` | `{id*,operate:'start'|'stop'}` |
| 批量启停/分组/SSL | `POST /api/v2/websites/batch/operate`、`/batch/group`、`/batch/ssl` | `{ids[],operate,taskID}` / `{ids[],groupID}` / 批量 HTTPS |
| 默认站点 | `POST /api/v2/websites/default/server` | `{id*}` |
| 运行目录 | `POST /api/v2/websites/dir` | `{id*}` → `{dirs[],user,userGroup,msg}` |
| 修改运行目录 | `POST /api/v2/websites/dir/update` | `{id*,siteDir}` |
| 目录权限 | `POST /api/v2/websites/dir/permission` | `{id*,user*,group*}` |
| 域名列表 | `GET /api/v2/websites/domains/{websiteId}` | — |
| 域名新增/删除/更新 | `POST /api/v2/websites/domains`、`/domains/del`、`/domains/update` | §2.5 |
| HTTPS 配置读/写 | `GET|POST /api/v2/websites/{id}/https` | `WebsiteHTTPSOp`（§2.6） |
| 反向代理列表 | `POST /api/v2/websites/proxies` | `{id*}` → `WebsiteProxyConfig[]` |
| 反向代理增改/删/启停 | `POST /api/v2/websites/proxies/update`、`/proxies/delete`、`/proxies/status` | §2.7 |
| 代理缓存清理 | `POST /api/v2/websites/proxy/clear` | `{id*,...}`（字段**待实测**） |
| SSL 证书 CRUD/申请 | `/api/v2/websites/ssl/*` | §2.6 |

### 2.2 创建网站 `POST /api/v2/websites`（`WebsiteCreate`，`agent/app/dto/request/website.go:16`）

必填：`type`、`alias`、`webSiteGroupID`；域名/SSL/端口按类型条件必填。

| 字段 | 类型 | 说明 |
|---|---|---|
| `type` | string* | `deployment`（应用商店应用）/`runtime`（运行环境）/`static`/`proxy`/`subsite`/`stream` |
| `alias` | string* | 站点标识，**不能为 `default`**；中文会转 Punycode；全局唯一（重复 `ErrAliasIsExist`） |
| `remark` | string | 备注 |
| `webSiteGroupID` | uint* | 分组 id（`POST /api/v2/groups/search` `{type:"website"}` 获取） |
| `IPV6` | bool | 是否监听 IPv6 |
| `domains` | `[{domain*,port,ssl}]` | 非 stream 类型必填；`ssl:true` 或 `enableSSL` 时要求配证书 |
| `appType` | string | `new` / `installed`，type=deployment 时用 |
| `appInstall` | object | `new` 时内联安装参数（appId/name/appDetailId/params/version…） |
| `appInstallID` | uint | `installed` 时已装应用 id |
| `runtimeID` | uint | type=runtime 时运行环境 id（`POST /api/v2/runtimes/search` 取） |
| `port` | int | runtime 类型端口（node/js/java/go/python/dotnet **必须非 0**） |
| `proxyType` | string | runtime=本地 PHP：`unix`/`tcp` |
| `proxyProtocol`/`proxyAddress` | string | type=proxy 时前端拼成 `proxy` |
| `proxy` | string | 反向代理目标，如 `http://127.0.0.1:8080` |
| `siteDir` | string | 子站目录（subsite） |
| `parentWebsiteID` | uint | 父站（subsite） |
| `templateOutputID` | uint | static 复制模板输出（可选） |
| `streamPorts` | string | stream 必填，逗号分隔端口 |
| `name`/`algorithm`/`servers[]`/`UDP` | — | stream 配置 |
| `ftpUser`/`ftpPassword` | string | FTP；**前端对 ftpPassword 做 base64**，后端 `DecodeString` |
| `createDb`/`dbName`/`dbUser`/`dbPassword`/`dbHost`/`DBFormat` | — | 建站同时建库 |
| `enableSSL`/`websiteSSLID` | bool/uint | 启用 SSL 时必须已有证书且 `pem/privateKey` 非空，否则 `ErrSSLValid` |
| `taskID` | string | 客户端 uuid，用于任务日志 |

服务端行为（`agent/app/service/website.go:281 CreateWebsite`）：

- 默认 HTTP 端口取 openresty 实际 `HttpPort`；`expireDate` 默认 `WebsiteDefaultExpireDate`。
- 非 stream：`primaryDomain` = 第一个 domain（端口非默认时拼 `domain:port`）；协议默认 `HTTP`，启用 SSL 则 `HTTPS` 且自动生成 HTTPS 配置（`HTTPToHTTPS`、TLS1.3/1.2、hsts=true）。
- 类型分支：`deployment` 用已装/新建应用的反代地址（agent 类应用拒绝）；`runtime` 生成 PHP-FPM socket 或 `127.0.0.1:port`；`subsite` 用父站+目录；`static` 可复制模板；`proxy` 直接用 `proxy`；`stream` 用 `streamPorts`。
- **异步**：`task.NewTaskWithOps(TaskCreate, TaskScopeWebsite, taskID)`，接口返回只代表已提交；失败原因在任务 `errorMsg`。
- 示例（静态站）：

```json
POST /api/v2/websites
{
  "type": "static",
  "alias": "demo-site",
  "remark": "P20 示例",
  "webSiteGroupID": 1,
  "IPV6": false,
  "domains": [{ "domain": "demo.example.com", "port": 80, "ssl": false }],
  "taskID": "3c2c9c1e-...."
}
```

### 2.3 更新网站 `POST /api/v2/websites/update`

`WebsiteUpdate{id*, primaryDomain*, remark, webSiteGroupID, expireDate, IPV6, favorite}`；`webSiteGroupID` 为 0 时不改分组，`expireDate` 空串不改；`IPV6` 变化会改写 nginx 监听。**不含域名与 SSL**（走 §2.5/§2.6）。

### 2.4 详情与列表字段

- 列表：`POST /api/v2/websites/search`，body `{page*,pageSize*,orderBy*,order*,name?,websiteGroupId?,type?}`；`orderBy` oneof `primary_domain|type|status|createdAt|expire_date|created_at|favorite`，`order` oneof `null|ascending|descending`。返回 `items: WebsiteRes[]`：
  `id,createdAt,protocol,primaryDomain,type,alias,remark,status,expireDate,sitePath,appName,runtimeName,sslExpireDate,sslStatus,appInstallId,childSites[],parentSite,runtimeType,favorite,IPV6`。
- 详情：`GET /api/v2/websites/{id}` → `WebsiteDTO` = `model.Website` 全字段 + `errorLogPath,accessLogPath,sitePath,appName,runtimeName,runtimeType,siteDir,openBaseDir,algorithm,UDP,servers[]`。
- `model.Website` 字段（`agent/app/model/website.go`）：`protocol,primaryDomain,type,alias,remark,status,httpConfig,expireDate,proxy,proxyType,siteDir,errorLog,accessLog,defaultServer,IPV6,rewrite,webSiteGroupId,webSiteSSLId,runtimeID,appInstallId,ftpId,parentWebsiteID,user,group,dbType,dbID,favorite,streamPorts,domains[],webSiteSSL`。
- ⚠️ **详情不加载 `domains`**（`model.Website.Domains` 是 `gorm:"-:migration"`，`GetWebsite` 未填充）→ 取域名必须另调 `GET /websites/domains/{id}`。
- 状态值：`Running`/`Stopped`；SSL 状态：`init|error|ready|applying|applyError`。

### 2.5 域名管理

| 操作 | 方法/路径 | body |
|---|---|---|
| 列表 | `GET /api/v2/websites/domains/{websiteId}` | — → `[{id,websiteId,domain,port,ssl}]` |
| 新增 | `POST /api/v2/websites/domains` | `{websiteID*, domains:[{domain*,port,ssl}]}` |
| 删除 | `POST /api/v2/websites/domains/del` | `{id*}`（单个） |
| 更新（仅 SSL 开关） | `POST /api/v2/websites/domains/update` | `{id*, ssl: bool}` |

### 2.6 SSL 证书与 HTTPS

路由（`agent/router/ro_website_ssl.go`）：

| 操作 | 方法/路径 | body / 返回 |
|---|---|---|
| 列表（分页） | `POST /api/v2/websites/ssl/search` | `{page*,pageSize*,acmeAccountID?,domain?,orderBy?,order?}`；`orderBy` oneof `created_at|updated_at|expire_date` |
| 列表（全量） | `POST /api/v2/websites/ssl/list` | `{acmeAccountID?}` |
| 创建证书记录 | `POST /api/v2/websites/ssl` | `WebsiteSSLCreate`，见下；返回**回显请求 + 新 `ID`**（status 初始 `init`） |
| 详情 | `GET /api/v2/websites/ssl/{id}` | `WebsiteSSLDTO`（含 `logPath`） |
| 某站证书 | `GET /api/v2/websites/ssl/website/{websiteId}` | 列表 |
| 申请（签发） | `POST /api/v2/websites/ssl/obtain` | `{ID*,skipDNSCheck?,nameservers[]}`；**同步长请求**，状态变 `applying`；已在申请中报 `InExecuting` |
| 解析检查 | `POST /api/v2/websites/ssl/resolve` | `{acmeAccountId, websiteSSLId}` → `[{resolve,value,domain,err}]` |
| 更新 | `POST /api/v2/websites/ssl/update` | `WebsiteSSLUpdate` |
| 删除 | `POST /api/v2/websites/ssl/del` | `{ids[]}` |
| 上传（粘贴/本地） | `POST /api/v2/websites/ssl/upload` | `{type:'paste'|'local',privateKey,certificate,privateKeyPath,certificatePath,...}` |
| 多节点推送 | `POST /api/v2/websites/ssl/push` | `{id*,pushNode,nodes,taskID*,sync}` |
| 下载/导入/上传文件 | `/ssl/download`、`/ssl/import`、`/ssl/upload/file` | 见源码 |

`WebsiteSSLCreate`（`agent/app/dto/request/website_ssl.go`）：`primaryDomain*,otherDomains,provider*,acmeAccountId*,dnsAccountId,autoRenew,keyType,apply,pushDir,dir,ID,description,disableCNAME,skipDNS,nameserver1/2,execShell,shell,pushNode,nodes,isIp`。
- `provider` 取值（`agent/constant/website.go`）：`http`、`dnsAccount`、`dnsManual`、`manual`、`selfSigned`、`fromMaster`。
- `WebsiteSSL` 字段：`primaryDomain,privateKey,pem,domains(string),certURL,type,provider,organization,dnsAccountId,acmeAccountId,caId,autoRenew,expireDate,startDate,status,message,keyType,pushDir,dir,description,skipDNS,nameserver1/2,disableCNAME,execShell,shell,masterSslId,nodes,pushNode,privateKeyPath,certPath,isIP`。
- ⚠️ **列表/详情返回 `privateKey`、`pem` 明文**（客户端目前未映射）。P20 聚合 DTO 严禁映射这两个字段。
- `apply` 字段后端未见使用：前端实际流程是**先 create 拿到 ID → resolve 检查 DNS → obtain 申请**；部署到站点走下面的 `POST /websites/{id}/https`。
- 部署/启用 HTTPS：`POST /api/v2/websites/{id}/https` body `WebsiteHTTPSOp{websiteId*,enable,websiteSSLId,type:'existed'|'auto'|'manual',privateKey,certificate,privateKeyPath,certificatePath,importType,httpConfig:'HTTPSOnly'|'HTTPAlso'|'HTTPToHTTPS',SSLProtocol[],algorithm,hsts,hstsIncludeSubDomains,httpsPorts[],http3}`；批量走 `POST /websites/batch/ssl`。

### 2.7 反向代理

| 操作 | 方法/路径 | body |
|---|---|---|
| 列表 | `POST /api/v2/websites/proxies` | `{id*}`（网站 id） → `WebsiteProxyConfig[]` |
| 新增/编辑 | `POST /api/v2/websites/proxies/update` | `{id*,operate:'create'|'edit',name*,match*,proxyPass*,proxyHost*,modifier,enable,cache,cacheTime,cacheUnit,serverCacheTime,serverCacheUnit,SNI,proxySSLName,sslVerify,replaces{},cors,allowOrigins,allowMethods,allowHeaders,allowCredentials,preflight,content?}` |
| 删除 | `POST /api/v2/websites/proxies/delete` | `{id*, name*}` |
| 启停 | `POST /api/v2/websites/proxies/status` | `{id*, name*, status:'enable'|'disable'}` |

- 读取 `GetProxies`（`website_proxy.go:262`）：扫描站点 `proxy/*.conf`（enable=true）与 `*.bak`（enable=false），解析为上述结构；`id` 字段实际是 **websiteID**（不是代理 id）。
- 新建用默认模板 `agent/cmd/server/nginx_conf/proxy.conf`（`location ^~ /test { proxy_pass http://1panel.cloud/; ... }`），`applyLocationProxyPass` 写入 `proxy_pass`，`proxy_set_header Host` 取 `proxyHost`，`location` 由 `modifier+match` 组合；cache/CORS 写 `add_header`/`sub_filter`。
- 冲突：同名 `.conf`/`.bak` 已存在 → `ErrNameIsExist`。
- 修改/删除直接改 nginx 配置并 reload（**Medium-High**）。

### 2.8 默认站点 / 运行目录

- 默认站点：`POST /api/v2/websites/default/server` `{id}` → 切换默认 server（改 nginx 配置）。
- 运行目录读取：`POST /api/v2/websites/dir` `{id}` → `{dirs[],user,userGroup,msg}`（`WebsiteDirConfig`）。
- 修改运行目录：`POST /api/v2/websites/dir/update` `{id*,siteDir}`；目录权限：`POST /api/v2/websites/dir/permission` `{id*,user*,group*}`。

---

## 3. Compose 项目（C：增删改/启停）

路由（`agent/router/ro_container.go`）：

| 操作 | 方法/路径 | body |
|---|---|---|
| 列表 | `POST /api/v2/containers/compose/search` | `{page*,pageSize*,info?,excludeAppStore?}`（`SearchWithPage`） |
| 创建 | `POST /api/v2/containers/compose` | `ComposeCreate`（见下） |
| 校验（不创建） | `POST /api/v2/containers/compose/test` | 同 `ComposeCreate`，返回 `true/false` |
| 启停/重建/删除 | `POST /api/v2/containers/compose/operate` | `ComposeOperation` |
| 编辑 | `POST /api/v2/containers/compose/update` | `ComposeUpdate` |
| 读取 .env | `POST /api/v2/containers/compose/env` | `{path*}` |
| 置顶 | `POST /api/v2/containers/compose/pin` | `{name*, isPinned}` |
| 清理日志 | `POST /api/v2/containers/compose/clean/log` | `{name*,path*,detailPath*}` |

### 3.1 创建 `ComposeCreate`（`agent/app/dto/container.go:311`）

| 字段 | 类型 | 说明 |
|---|---|---|
| `taskID` | string | 客户端 uuid（异步任务日志） |
| `name` | string | 项目名；`from=path` 时可选（从 yml 内 `name:`/`COMPOSE_PROJECT_NAME` 解析） |
| `dirName` | string | 落盘目录名，缺省用 `name` |
| `from` | string* | `edit`（新建文件）/`path`（已有目录）/`template`（模板） |
| `file` | string | yml 内容；`from=edit|template` 时写入 `{DataDir}/docker/compose/{dirName}/docker-compose.yml` |
| `path` | string | `from=path` 时的 compose 目录 |
| `template` | uint | 模板 id（**语义待实测**，前端创建表单默认 `null`） |
| `env` | string | `.env` 内容，写同目录 `.env` |
| `forcePull` | bool | 创建前强制拉镜像 |

- 名称规则：前端 `/^[a-z0-9][a-z0-9_-]{0,255}$/`（后端 `re.ComposeNamePattern`）；会转小写；非法字符/命令注入 → `ErrCmdIllegal`。
- 重复：项目名或目标路径已存在记录 → `ErrRecordExist`。
- **异步**：创建后 `task.TaskCreate` → `compose.UpWithTask(path, task, forcePull, name)`；接口返回 success 只代表已提交。
- 前端流程：`testCompose(form)` 通过后再 `upCompose(form)`，随后打开 taskID 日志。

示例：

```json
POST /api/v2/containers/compose
{
  "taskID": "0b4a...uuid",
  "name": "demo",
  "dirName": "demo",
  "from": "edit",
  "file": "services:\n  web:\n    image: nginx:alpine\n    ports: [\"8080:80\"]\n",
  "env": "TZ=Asia/Shanghai",
  "forcePull": false
}
```

### 3.2 启停/删除 `ComposeOperation`（`agent/app/dto/container.go:319`）

`{name*, path?, operation*: up|start|restart|stop|down|delete|rebuild, withFile?, force?}`

- `up/start/restart/stop/down/rebuild`：**同步执行**（`compose.Operate`），`rebuild` = down+up；返回后状态已变更（仍建议刷新列表）。
- `delete`：先 `down` 删容器，再删记录；`path` 为空时只删记录；`withFile=true` 会 `RemoveAll(compose 目录)`（**危险，Critical**：会删用户 compose 文件与同目录内容）；`force=true` 忽略 down 失败。
- 删除语义：容器按 `com.docker.compose.project` label 清理。

### 3.3 编辑 `ComposeUpdate`（异步 TaskUpdate）

`{taskID, name*, path*, detailPath, content*, env?, forcePull?}` — 覆盖写 `detailPath`（真实 yml 路径）为 `content`，写 `.env`，`compose.UpWithTask`；失败自动用旧文件回滚（recreateCompose）。

> `path` 与 `detailPath` 的区别：列表返回的 `path` 是项目目录/配置文件路径，前端编辑时同时携带 `detailPath`；**待实测** 两者在多层 compose 下的精确差异（列表 `configFile`/`workdir` 可辅助判断）。

### 3.4 列表字段 `ComposeInfo`

`name,createdAt,createdBy,containerCount,runningCount,configFile,workdir,composeFileExists,isPinned,path,containers[{containerID,name,createTime,state,ports[]}],env`。
⚠️ `env` 含明文密码/密钥，**聚合 DTO 不映射**。

---

## 4. 面板设置与认证（D，`/api/v2/core/*`）

> API Key HMAC 可访问：`SessionAuth` 对 `API_AUTH` 放行；`PasswordExpired` 也放行且特批 `/core/auth*`、`/core/settings/search`、`/search/base`。

### 4.1 读取

| 用途 | 方法/路径 | 返回 |
|---|---|---|
| 全量设置 | `POST /api/v2/core/settings/search` | `SettingInfo`（下列字段） |
| 基础设置 | `POST /api/v2/core/settings/search/base` | 子集（无 ssl/sslType/allowIPs 等） |
| 面板 SSL | `GET /api/v2/core/settings/ssl/info` | `{domain,timeout,rootPath,cert,key,sslID}` |
| 监听地址 | `GET /api/v2/core/settings/interface` | `[]string` |
| 升级信息 | `GET /api/v2/core/settings/upgrade`（另有 `/upgrade/releases`、`/upgrade/notes`） | 版本信息（字段见 `docs/13`，**待实测**） |
| 当前用户/API 状态 | `GET /api/v2/core/auth/current` | `CurrentUserInfo`（含 `apiInterfaceStatus,apiKey,ipWhiteList,apiTrustedProxies,apiKeyValidityTime,mfaStatus,mfaInterval,role,permissions,nodeRoles`） |

`SettingInfo` 字段（`core/app/dto/setting.go`）：`systemVersion,developerMode,upgradeBackupCopies,sessionTimeout,port,ipv6,bindAddress,panelName,edition,theme,menuTabs,menuAccordion,language,docSource,serverPort,ssl,sslType,bindDomain,passkeyTrustedProxies,allowIPs,allowIPTrustedProxies,securityEntrance,dashboardMemoVisible,dashboardSimpleNodeVisible,expirationDays,complexityVerification,appStoreVersion,appStoreLastModified,appStoreSyncStatus,hideMenu,noAuthSetting,proxyUrl,proxyType,proxyPort,proxyUser,proxyPasswd,proxyPasswdKeep`。

⚠️ `CurrentUserInfo.apiKey` 是明文 API Key；`SettingInfo.proxyPasswd*` 是代理密码。聚合层严禁下发。

### 4.2 更新

| 功能 | 方法/路径 | body | 重启面板？ | 风险 |
|---|---|---|---|---|
| 通用设置 | `POST /api/v2/core/settings/update` | `{key*, value(string)}`，key 白名单见下 | 否（个别键清会话） | Low–High |
| 面板端口 | `POST /api/v2/core/settings/port/update` | `{serverPort*:1..65535}` | **是**（1s 后异步重启，连接立即中断） | High/Critical |
| 监听地址/IPv6 | `POST /api/v2/core/settings/bind/update` | `{ipv6, bindAddress}` | **是**（同上） | High/Critical |
| 面板 SSL | `POST /api/v2/core/settings/ssl/update` | `{sslType*:'self'|'select'|'import'|'import-paste'|'import-local', domain, ssl*:'Enable'|'Disable'|'Mux', cert, key, sslID}` | **是**（状态变化时重启） | Critical |
| 终端设置 | `POST /api/v2/core/settings/terminal/update` | `{showTerminalButton:'Enable'|'Disable',lineHeight,letterSpacing,fontSize,fontFamily,backgroundColor,foregroundColor,cursorBlink,cursorStyle,scrollback,scrollSensitivity}`（pointer+omitempty） | 否 | Low |
| 菜单 | `POST /api/v2/core/settings/menu/update` / `/menu/default` | 字段未核（**待实测**，预期 `{menuTabs,menuAccordion}`） | 否 | Low |
| 代理设置 | `POST /api/v2/core/settings/proxy/update` | 字段未核（**待实测**，预期 `{proxyUrl,proxyType,proxyPort,proxyUser,proxyPasswd,proxyPasswdKeep}`） | 否 | Medium |
| 升级面板 | `POST /api/v2/core/settings/upgrade` | `{version}` | 重启/升级流程（见 `docs/13`） | Critical |

`settings/update` 的 key 白名单（`core/init/validator/validator.go:35-59`，共 24 个）：
`PanelName, Theme, MenuTabs, MenuAccordion, Language, SessionTimeout, ExpirationDays, DeveloperMode, UpgradeBackupCopies, SecurityEntrance, BindDomain, AllowIPs, AllowIPTrustedProxies, PasskeyTrustedProxies, ComplexityVerification, NoAuthSetting, DashboardMemoVisible, DashboardSimpleNodeVisible, Edition, DocSource, AppStoreLastModified, ScriptSync, HideMenu`。

行为（`core/app/service/setting.go`）：

- `SessionTimeout`→会话超时生效；`ExpirationDays`→密码过期同步；`BindDomain` 非空→**清 session + 关终端 + 清 passkey**；`Language`→i18n；`Edition`→同步多节点；`HideMenu` 规范化；API 凭据类 key 直接拒绝。
- `SecurityEntrance`：值格式 `^[a-zA-Z0-9]{5,116}$`，不能等于已有单段路由名（`public`、`assets` 等）→ `checkEntrancePattern`；修改后**必须用新入口访问 UI**（API 根地址不受影响）。
- `UpdatePort`：先 `ScanPort` 校验占用 → 更新本地代理端口 → 写 `serverPort` → 清 passkey → 1s 后 `RestartPanel`；**当前 HTTP 连接会断**，客户端需重连并等待面板恢复（不要立即判节点离线）。
- `UpdateSSL`：`Disable` 清 cookie、删 1panel 证书、重启；`Enable` 按 sslType 写 `1panel/secret/server.crt|key`（`self` 用 1Panel CA 签发 10 年 EC256），校验证书，状态变化时重启。
- `UpdateBindInfo`：写 `ipv6/bindAddress` 后 1s 重启。

### 4.3 认证 / API 接口 / MFA / 密码

| 功能 | 方法/路径 | body |
|---|---|---|
| 当前用户 | `GET /api/v2/core/auth/current` | — |
| 改用户名/密码 | `POST /api/v2/core/auth/current/update` | `{name*, password, oldPassword}` |
| 过期改密 | `POST /api/v2/core/auth/expired/reset` | `{oldPassword*, newPassword*}` |
| 开启 MFA | `POST /api/v2/core/auth/mfa` | `{title*, interval*}` → 返回 secret + 二维码 |
| 绑定 MFA | `POST /api/v2/core/auth/mfa/bind` | `{secret, code, interval}` |
| 关闭 MFA | `POST /api/v2/core/auth/mfa/close` | —（字段**待实测**） |
| API 接口开关/白名单 | `POST /api/v2/core/auth/api/update` | `ApiInterfaceConfig{apiInterfaceStatus, apiKey, ipWhiteList, apiTrustedProxies, apiKeyValidityTime}`（需 API Key session） |
| 重置 API Key | `POST /api/v2/core/auth/api/generate` | 生成新 key，`Cache-Control: no-store` |
| 多 Key 管理 | `POST /api/v2/core/auth/api/keys/search|create|update|status|revoke` | `keys` CRUD（**字段待实测**，P20 首版可选） |

⚠️ API Key 相关操作 = 凭据风险 **Critical**：不得放进批量操作；PanelFleet 侧只允许“本节点自身 API Key 配置”，且需二次确认 + 审计（`docs/09` 内核）。

### 4.4 agent 侧设置（与 core 区分）

- `GET /api/v2/settings/basedir` → 数据目录字符串；`GET /api/v2/settings/website/dir` → 网站根目录。
- `POST /api/v2/settings/update` → `{key*: 'SystemIP'|'DockerSockPath'|'FileRecycleBin', value}`。
- `GET /api/v2/settings/search` → agent 设置（`dockerSockPath,systemIP,localTime,timeZone,ntpSite,defaultNetwork,defaultIO,monitorStatus,monitorInterval,monitorStoreDays,appStoreVersion,fileRecycleBin,localSSHConnShow,firewallPortWhiteList` 等）。

---

## 5. 附录：`apps/detail` 与 `installed/params` 字段差异（E）

| 维度 | `GET /api/v2/apps/detail/{appId}/{version}/{type}` | `GET /api/v2/apps/installed/params/{id}` |
|---|---|---|
| 用途 | 商店详情/安装页：某版本的**表单定义** | 已装实例：**当前参数值** + 原始 compose |
| 返回结构 | `AppDetailDTO{appId,version,params(解析后表单字段数组),dockerCompose,status,lastVersion,lastModified,downloadUrl,downloadCallbackUrl,update,enable,image,hostMode,architectures,memoryRequired,gpuSupport}` | `AppConfig{params[]AppParam, rawCompose, cpuQuota, memoryLimit, memoryUnit, containerName, allowPort, restartPolicy, dockerCompose, webUI, type}` |
| `params` 元素 | `type,labelZh,labelEn,label,description,required,default,envKey,disabled,edit,rule,multiple,child,values[{label,value}]`（`AppFormFields`） | `value,edit,key,rule,labelZh,labelEn,type,values,showValue,required,multiple,label`（`AppParam`） |
| 语义 | 默认值、校验规则、多语言标签、子字段；runtime 类型来自版本目录 `data.yml` 的 `additionalProperties` | 当前值；`edit=true` 才可改；`rawCompose` 为安装时原始 compose |

安装/升级时提交的 `params` 是 `map[string]interface{}`（key=envKey），不是上述数组。

---

## 6. 实现建议（PanelFleet 写操作内核复用）

1. **全部走现有内核**（`docs/09`）：实现 `IOperationHandler`（`Kind` + `PlanAsync` + `ExecuteAsync`），风险用 `OperationRisk{Low,Medium,High,Critical}`，`OperationStepResult.TaskId` 记录 taskID；批量由内核按节点循环、`PartiallySucceeded` 汇总。
2. 现有 Kind 已覆盖：`container.*`、`compose.up|stop|restart|down`、`app.install|update|uninstall|params_update|batch_upgrade`、`website.create|update|start|stop|php_version`、`domain.create|update|delete|primary`、`proxy.create|update|delete|status`、`ssl.request|apply|deploy|disable`、`database.*`、`cronjob.*`、`panel.update`。
3. P20 需补的 Kind 与建议风险：
   - `compose.create`（Medium）、`compose.update`（Medium）、`compose.delete`（withFile=true 时 **Critical**，否则 High）、`compose.operate`（rebuild/up/down：Medium）；
   - `panel.setting_update`（按键分档：名称/主题/语言/菜单 Low；SecurityEntrance/BindDomain **High**；AllowIPs/DeveloperMode Medium）；
   - `panel.port_update`、`panel.bind_update`、`panel.ssl_update`（**Critical**：面板会重启、连接中断）；
   - `panel.api_update`、`panel.api_generate`、`panel.mfa_*`（**Critical**，凭据/登录安全）；
   - `website.delete`（High，注意 `deleteApp/deleteBackup/deleteDB` 选项）、`ssl.deploy` 已存在（Medium）。
4. **异步处理**：对 Compose/网站/应用升级/面板升级，统一“提交 → 记 taskID → 轮询 `tasks/search` → 超时给‘已提交’兜底”；失败读 `errorMsg`，不要用 HTTP 状态判断成功。
5. **脱敏**：`WebsiteSSL.privateKey/pem`、`ComposeInfo.env`、`AppInstall.env`、`CurrentUserInfo.apiKey`、`SettingInfo.proxyPasswd*` 禁止进入聚合 DTO/日志/审计详情；审计只记参数摘要（域名、名称、版本、节点）。
6. **面板重启类操作**：执行后节点进入“重启中”，UI 需退避重连（例如 1s/3s/10s），成功后刷新 `core/settings/search` 校验；失败时提示“可能已生效但连接未恢复”，不要自动重试（端口/SSL 变更是非幂等）。
7. **幂等/冲突**：创建类先做本地预检（名称/域名/端口），服务端 `code!=200` 原文透出（`ErrRecordExist`/`ErrAliasIsExist`/`ErrNameIsExist`/`ErrSSLValid`）。
8. **批量**：应用升级可批量（内核已有 `app.batch_upgrade`）；**Compose/网站/SSL 批量只做“逐节点循环 + 单项结果”，不做跨节点事务**；面板设置/端口/SSL/API Key 一律禁止批量。

---

## 7. 不确定项 / 待实测

| # | 项 | 现状 | 建议 |
|---|---|---|---|
| U1 | `ComposeCreate.template` 语义（模板 id 与文件来源的关系） | 源码 create 只读取 `file` 落盘；`template` 字段未被 `loadPath` 使用 | 首版只支持 `from=edit`/`path`，`template` 传 0；如需模板需实测前端模板页请求 |
| U2 | `POST /containers/compose/pin` 的 `isPinned` 语义 | `{name,isPinned}` 直传 | 可先用，失败不影响主流程 |
| U3 | `GET /core/settings/upgrade` 返回字段 | 仅知前端 `loadUpgradeInfo` 读取 | 面板升级已有 `docs/13`，用时再核对 |
| U4 | `POST /core/settings/menu/update`、`/proxy/update` 请求体 | 未核到 handler 入参结构 | 实现前实测或用 `SettingInfo` 现有值整体回写 |
| U5 | `POST /tasks/read` 请求字段 | 未核 | 首版用 `tasks/search` + 日志 SSE/任务页兜底 |
| U6 | Compose `path` 与 `detailPath` 多层 compose 差异 | 列表含 `configFile/workdir` | 编辑页把两者都回传（前端即如此） |
| U7 | 真实节点写操作结果 | 本轮**未实测**（无 Key 注入，Key 禁止落盘） | 由 Lead 决定是否用只读接口复核 `settings/search`、`compose/search` 的字段外形；所有写操作仍需契约测试/假面板 |
| U8 | `WebsiteSSLCreate.apply` | 后端未读取 | 前端保持 create→obtain 两步 |
| U9 | 网站 `operate` 是否接受 `restart`/`reload` | 源码常量有 `start|stop|setHttps`，前端只用 start/stop | 只发 start/stop |
| U10 | 面板端口/SSL 变更后 API 是否立即可用 | 异步重启，1s 后断连 | 客户端按“重启中”处理，见 §6.6 |

---

## 8. 来源清单（commit `65243c68c463cc055ab044093f641ea5d2e9e28b`）

后端路由：
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/router/ro_website.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/router/ro_website_ssl.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/router/ro_container.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/router/ro_app.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/router/ro_setting.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/core/router/ro_setting.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/core/router/ro_base.go>

DTO / Service：
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/request/website.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/request/website_ssl.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/request/app.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/container.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/response/website.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/dto/response/website_ssl.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/service/website.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/service/website_proxy.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/service/website_ssl.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/service/container_compose.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/service/app_install.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/core/app/dto/setting.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/core/app/service/setting.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/core/init/validator/validator.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/core/middleware/session.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/core/middleware/password_expired.go>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/agent/app/model/task.go>

前端：
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/frontend/src/api/modules/website.ts>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/frontend/src/api/modules/app.ts>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/frontend/src/api/modules/container.ts>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/frontend/src/api/modules/setting.ts>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/frontend/src/views/website/website/create/index.vue>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/frontend/src/views/app-store/installed/upgrade/index.vue>
- <https://github.com/1Panel-dev/1Panel/blob/65243c68c463cc055ab044093f641ea5d2e9e28b/frontend/src/views/container/compose/index.vue>

关联文档：`docs/research/p4-api-routes.md`（只读契约）、`docs/research/p3-api-probe.md`（真实节点鉴权/字段实测）、`docs/09-P5-写操作内核详细设计.md`、`docs/10-应用商店详细设计.md`、`docs/13-1Panel更新详细设计.md`、`docs/14-网站管理详细设计.md`、`docs/11-容器管理详细设计.md`。
