# P3 聚合接口真实探测记录（1Panel v2.3.2）

- 探测时间：2026-11-16 14:20（Asia/Shanghai）
- 节点：`http://198.51.100.10:1000`，1Panel `v2.3.2`，Debian 13（LXC）
- 鉴权：HMAC-SHA256；请求头 `1Panel-Token: <十六进制签名>`、`1Panel-Timestamp: <Unix 秒>`；签名 = `HMAC_SHA256(apiKey, "1panel:" + timestamp)` 小写 hex。**注意 `1Panel-Token` 放的是签名，不是 API Key 原文。**
- 密钥：探测时通过环境变量注入，未写入本文件。
- 原始响应文件（本次运行）：`%TEMP%\p3probe\raw\dashboard_*.json`；下述 JSON 由原始响应美化输出，字段名/类型/值与真实一致。

## 端点汇总

| 端点 | HTTP | Content-Type | 耗时 | data 大小 |
|---|---|---|---|---|
| `GET /api/v2/dashboard/base/all/all` | 200 | application/json; charset=utf-8 | 510 ms | 2869 chars |
| `GET /api/v2/dashboard/base/os` | 200 | application/json; charset=utf-8 | 84 ms | 206 chars |
| `GET /api/v2/dashboard/current/node` | 200 | application/json; charset=utf-8 | 84 ms | 421 chars |
| `GET /api/v2/dashboard/current/all/all` | 200 | application/json; charset=utf-8 | 95 ms | 1474 chars |
| `GET /api/v2/dashboard/base/io/net` | 200 | application/json; charset=utf-8 | 92 ms | 2807 chars |
| `GET /api/v2/dashboard/current/io/net` | 200 | application/json; charset=utf-8 | 96 ms | 1411 chars |

## 结论（对 P3 设计的影响）

1. **`GET /api/v2/dashboard/base/all/all` 一次返回全部总览所需数据**：主机信息、4 类资源计数（网站/应用/数据库/计划任务）与 `currentInfo`（CPU/内存/负载/磁盘/网络/运行时长/采样时间）。P3 每节点只需 1 次请求。
2. `base/all/all` 不返回 1Panel 版本号；版本继续使用 P2 节点记录里的 `PanelVersion`（来自 `POST /settings/search`）。
3. `os` 是内核类别（`linux`），`platform` 是发行版标识（`debian`），展示名优先用 `prettyDistro`（如 `Debian GNU/Linux 13`），回退 `platform`。
4. 磁盘为多分区（本机 `/` 为 zfs 60.3%，`/data/Routing` 为 cifs 94.7%）；卡片展示「使用率最高的分区 + 路径」，汇总按 Σused/Σtotal 加权。
5. `currentInfo` 还含 `cpuDetailedPercent`、`swapMemory*`、`ioRead/WriteBytes`、`netBytesSent/Recv`、`gpuData` 等；P3 只用 CPU/内存/负载/磁盘/uptime/shotTime，其余保留给 P4/P5。

## GET /api/v2/dashboard/base/all/all

- HTTP 200，application/json; charset=utf-8，510 ms

```json
{
    "code":  200,
    "message":  "",
    "data":  {
                 "websiteNumber":  0,
                 "agentNumber":  0,
                 "databaseNumber":  5,
                 "cronjobNumber":  0,
                 "appInstalledNumber":  4,
                 "hostname":  "node-a",
                 "os":  "linux",
                 "platform":  "debian",
                 "platformFamily":  "debian",
                 "platformVersion":  "13.7",
                 "prettyDistro":  "Debian GNU/Linux 13",
                 "kernelArch":  "x86_64",
                 "kernelVersion":  "7.0.14-28-pve",
                 "virtualizationSystem":  "{\"hostname\":\"node-a\",\"uptime\":489439,\"bootTime\":1794320412,\"procs\":65,\"os\":\"linux\",\"platform\":\"debian\",\"platformFamily\":\"debian\",\"platformVersion\":\"13.7\",\"kernelVersion\":\"7.0.14-28-pve\",\"kernelArch\":\"x86_64\",\"virtualizationSystem\":\"lxc\",\"virtualizationRole\":\"guest\",\"hostId\":\"00000000-0000-0000-0000-000000000000\"}",
                 "ipV4Addr":  "198.51.100.10",
                 "systemProxy":  "noProxy",
                 "cpuCores":  8,
                 "cpuLogicalCores":  2,
                 "cpuModelName":  "AMD Ryzen 7 2700X Eight-Core Processor",
                 "cpuMhz":  3700,
                 "quickJump":  [
                                   {
                                       "id":  6,
                                       "name":  "Agent",
                                       "alias":  "",
                                       "title":  "aiTools.agents.agent",
                                       "detail":  "0",
                                       "recommend":  1,
                                       "isShow":  true,
                                       "router":  "/ai/agents/agent"
                                   },
                                   {
                                       "id":  1,
                                       "name":  "Website",
                                       "alias":  "",
                                       "title":  "menu.website",
                                       "detail":  "0",
                                       "recommend":  10,
                                       "isShow":  true,
                                       "router":  "/websites"
                                   },
                                   {
                                       "id":  2,
                                       "name":  "Database",
                                       "alias":  "",
                                       "title":  "home.database",
                                       "detail":  "5",
                                       "recommend":  30,
                                       "isShow":  true,
                                       "router":  "/databases"
                                   },
                                   {
                                       "id":  4,
                                       "name":  "AppInstalled",
                                       "alias":  "",
                                       "title":  "home.appInstalled",
                                       "detail":  "4",
                                       "recommend":  70,
                                       "isShow":  true,
                                       "router":  "/apps/installed"
                                   }
                               ],
                 "currentInfo":  {
                                     "uptime":  489414,
                                     "timeSinceUptime":  "2026-11-10 14:20:12",
                                     "runningTime":  {
                                                         "days":  5,
                                                         "hours":  15,
                                                         "minutes":  56,
                                                         "seconds":  54
                                                     },
                                     "procs":  65,
                                     "load1":  3.96,
                                     "load5":  1.99,
                                     "load15":  1.74,
                                     "loadUsagePercent":  132,
                                     "cpuPercent":  [
                                                        18.181818181818176,
                                                        11.111111111111116
                                                    ],
                                     "cpuUsedPercent":  15.000000000000002,
                                     "cpuUsed":  0.30000000000000004,
                                     "cpuTotal":  2,
                                     "cpuDetailedPercent":  [
                                                                5,
                                                                10,
                                                                0,
                                                                85,
                                                                0,
                                                                0,
                                                                0,
                                                                0
                                                            ],
                                     "memoryTotal":  2147483648,
                                     "memoryUsed":  1506727936,
                                     "memoryFree":  301993984,
                                     "memoryShard":  5636096,
                                     "memoryCache":  343840768,
                                     "memoryAvailable":  640755712,
                                     "memoryUsedPercent":  70.1624870300293,
                                     "swapMemoryTotal":  0,
                                     "swapMemoryAvailable":  0,
                                     "swapMemoryUsed":  0,
                                     "swapMemoryUsedPercent":  0,
                                     "ioReadBytes":  2612165399040,
                                     "ioWriteBytes":  828348052480,
                                     "ioCount":  456301447,
                                     "ioReadTime":  38526820,
                                     "ioWriteTime":  36573725,
                                     "diskData":  [
                                                      {
                                                          "path":  "/",
                                                          "type":  "zfs",
                                                          "device":  "rpool/data/subvol-101-disk-0",
                                                          "total":  34359738368,
                                                          "free":  13632012288,
                                                          "used":  20727726080,
                                                          "usedPercent":  60.32562255859375,
                                                          "inodesTotal":  26861704,
                                                          "inodesUsed":  236576,
                                                          "inodesFree":  26625128,
                                                          "inodesUsedPercent":  0.8807185128687294
                                                      },
                                                      {
                                                          "path":  "/data/Routing",
                                                          "type":  "cifs",
                                                          "device":  "//198.51.100.1/Data",
                                                          "total":  1967846068224,
                                                          "free":  104215789568,
                                                          "used":  1863630278656,
                                                          "usedPercent":  94.70406800354787,
                                                          "inodesTotal":  0,
                                                          "inodesUsed":  0,
                                                          "inodesFree":  0,
                                                          "inodesUsedPercent":  0
                                                      }
                                                  ],
                                     "netBytesSent":  1745965510,
                                     "netBytesRecv":  3042477433,
                                     "gpuData":  null,
                                     "npuData":  null,
                                     "xpuData":  null,
                                     "topCPUItems":  null,
                                     "topMemItems":  null,
                                     "shotTime":  "2026-11-16T06:17:06.184526165Z"
                                 }
             }
}
```

`data` 字段：

| 字段 | JSON 类型 | 示例 |
|---|---|---|
| `websiteNumber` | Int32 | 0 |
| `agentNumber` | Int32 | 0 |
| `databaseNumber` | Int32 | 5 |
| `cronjobNumber` | Int32 | 0 |
| `appInstalledNumber` | Int32 | 4 |
| `hostname` | String | node-a |
| `os` | String | linux |
| `platform` | String | debian |
| `platformFamily` | String | debian |
| `platformVersion` | String | 13.7 |
| `prettyDistro` | String | Debian GNU/Linux 13 |
| `kernelArch` | String | x86_64 |
| `kernelVersion` | String | 7.0.14-28-pve |
| `virtualizationSystem` | String | {"hostname":"node-a","uptime":489439,"bootTime":... |
| `ipV4Addr` | String | 198.51.100.10 |
| `systemProxy` | String | noProxy |
| `cpuCores` | Int32 | 8 |
| `cpuLogicalCores` | Int32 | 2 |
| `cpuModelName` | String | AMD Ryzen 7 2700X Eight-Core Processor |
| `cpuMhz` | Int32 | 3700 |
| `quickJump` | array[4] |  |
| `currentInfo` | object |  |

`data.currentInfo` 字段：

| 字段 | JSON 类型 | 示例 |
|---|---|---|
| `uptime` | Int32 | 489414 |
| `timeSinceUptime` | String | 2026-11-10 14:20:12 |
| `runningTime` | object |  |
| `procs` | Int32 | 65 |
| `load1` | Decimal | 3.96 |
| `load5` | Decimal | 1.99 |
| `load15` | Decimal | 1.74 |
| `loadUsagePercent` | Int32 | 132 |
| `cpuPercent` | array[2] |  |
| `cpuUsedPercent` | Decimal | 15.000000000000002 |
| `cpuUsed` | Decimal | 0.30000000000000004 |
| `cpuTotal` | Int32 | 2 |
| `cpuDetailedPercent` | array[8] |  |
| `memoryTotal` | Int64 | 2147483648 |
| `memoryUsed` | Int32 | 1506727936 |
| `memoryFree` | Int32 | 301993984 |
| `memoryShard` | Int32 | 5636096 |
| `memoryCache` | Int32 | 343840768 |
| `memoryAvailable` | Int32 | 640755712 |
| `memoryUsedPercent` | Decimal | 70.1624870300293 |
| `swapMemoryTotal` | Int32 | 0 |
| `swapMemoryAvailable` | Int32 | 0 |
| `swapMemoryUsed` | Int32 | 0 |
| `swapMemoryUsedPercent` | Int32 | 0 |
| `ioReadBytes` | Int64 | 2612165399040 |
| `ioWriteBytes` | Int64 | 828348052480 |
| `ioCount` | Int32 | 456301447 |
| `ioReadTime` | Int32 | 38526820 |
| `ioWriteTime` | Int32 | 36573725 |
| `diskData` | array[2] |  |
| `netBytesSent` | Int32 | 1745965510 |
| `netBytesRecv` | Int64 | 3042477433 |
| `gpuData` | null |  |
| `npuData` | null |  |
| `xpuData` | null |  |
| `topCPUItems` | null |  |
| `topMemItems` | null |  |
| `shotTime` | String | 2026-11-16T06:17:06.184526165Z |

## GET /api/v2/dashboard/base/os

- HTTP 200，application/json; charset=utf-8，84 ms

```json
{
    "code":  200,
    "message":  "",
    "data":  {
                 "os":  "linux",
                 "platform":  "debian",
                 "platformFamily":  "debian",
                 "kernelArch":  "amd64",
                 "kernelVersion":  "7.0.14-28-pve",
                 "prettyDistro":  "Debian GNU/Linux 13",
                 "diskSize":  13632012288
             }
}
```

`data` 字段：

| 字段 | JSON 类型 | 示例 |
|---|---|---|
| `os` | String | linux |
| `platform` | String | debian |
| `platformFamily` | String | debian |
| `kernelArch` | String | amd64 |
| `kernelVersion` | String | 7.0.14-28-pve |
| `prettyDistro` | String | Debian GNU/Linux 13 |
| `diskSize` | Int64 | 13632012288 |

## GET /api/v2/dashboard/current/node

- HTTP 200，application/json; charset=utf-8，84 ms

```json
{
    "code":  200,
    "message":  "",
    "data":  {
                 "load1":  3.96,
                 "load5":  1.99,
                 "load15":  1.74,
                 "loadUsagePercent":  132,
                 "cpuUsedPercent":  15.000000000000002,
                 "cpuUsed":  0.30000000000000004,
                 "cpuTotal":  2,
                 "cpuDetailedPercent":  [
                                            5,
                                            10,
                                            0,
                                            85,
                                            0,
                                            0,
                                            0,
                                            0
                                        ],
                 "memoryTotal":  2147483648,
                 "memoryAvailable":  640943104,
                 "memoryUsed":  1506540544,
                 "memoryUsedPercent":  70.15376091003418,
                 "swapMemoryTotal":  0,
                 "swapMemoryAvailable":  0,
                 "swapMemoryUsed":  0,
                 "swapMemoryUsedPercent":  0
             }
}
```

`data` 字段：

| 字段 | JSON 类型 | 示例 |
|---|---|---|
| `load1` | Decimal | 3.96 |
| `load5` | Decimal | 1.99 |
| `load15` | Decimal | 1.74 |
| `loadUsagePercent` | Int32 | 132 |
| `cpuUsedPercent` | Decimal | 15.000000000000002 |
| `cpuUsed` | Decimal | 0.30000000000000004 |
| `cpuTotal` | Int32 | 2 |
| `cpuDetailedPercent` | array[8] |  |
| `memoryTotal` | Int64 | 2147483648 |
| `memoryAvailable` | Int32 | 640943104 |
| `memoryUsed` | Int32 | 1506540544 |
| `memoryUsedPercent` | Decimal | 70.15376091003418 |
| `swapMemoryTotal` | Int32 | 0 |
| `swapMemoryAvailable` | Int32 | 0 |
| `swapMemoryUsed` | Int32 | 0 |
| `swapMemoryUsedPercent` | Int32 | 0 |

## GET /api/v2/dashboard/current/all/all

- HTTP 200，application/json; charset=utf-8，95 ms

```json
{
    "code":  200,
    "message":  "",
    "data":  {
                 "uptime":  489415,
                 "timeSinceUptime":  "2026-11-10 14:20:12",
                 "runningTime":  {
                                     "days":  5,
                                     "hours":  15,
                                     "minutes":  56,
                                     "seconds":  55
                                 },
                 "procs":  65,
                 "load1":  3.96,
                 "load5":  1.99,
                 "load15":  1.74,
                 "loadUsagePercent":  132,
                 "cpuPercent":  [
                                    18.181818181818176,
                                    11.111111111111116
                                ],
                 "cpuUsedPercent":  15.000000000000002,
                 "cpuUsed":  0.30000000000000004,
                 "cpuTotal":  2,
                 "cpuDetailedPercent":  [
                                            5,
                                            10,
                                            0,
                                            85,
                                            0,
                                            0,
                                            0,
                                            0
                                        ],
                 "memoryTotal":  2147483648,
                 "memoryUsed":  1506540544,
                 "memoryFree":  302178304,
                 "memoryShard":  5636096,
                 "memoryCache":  343843840,
                 "memoryAvailable":  640943104,
                 "memoryUsedPercent":  70.15376091003418,
                 "swapMemoryTotal":  0,
                 "swapMemoryAvailable":  0,
                 "swapMemoryUsed":  0,
                 "swapMemoryUsedPercent":  0,
                 "ioReadBytes":  2612200550912,
                 "ioWriteBytes":  828348060672,
                 "ioCount":  456310011,
                 "ioReadTime":  38527327,
                 "ioWriteTime":  36573727,
                 "diskData":  [
                                  {
                                      "path":  "/",
                                      "type":  "zfs",
                                      "device":  "rpool/data/subvol-101-disk-0",
                                      "total":  34359738368,
                                      "free":  13632012288,
                                      "used":  20727726080,
                                      "usedPercent":  60.32562255859375,
                                      "inodesTotal":  26861704,
                                      "inodesUsed":  236576,
                                      "inodesFree":  26625128,
                                      "inodesUsedPercent":  0.8807185128687294
                                  },
                                  {
                                      "path":  "/data/Routing",
                                      "type":  "cifs",
                                      "device":  "//198.51.100.1/Data",
                                      "total":  1967846068224,
                                      "free":  104215789568,
                                      "used":  1863630278656,
                                      "usedPercent":  94.70406800354787,
                                      "inodesTotal":  0,
                                      "inodesUsed":  0,
                                      "inodesFree":  0,
                                      "inodesUsedPercent":  0
                                  }
                              ],
                 "netBytesSent":  1745969798,
                 "netBytesRecv":  3042485141,
                 "gpuData":  null,
                 "npuData":  null,
                 "xpuData":  null,
                 "topCPUItems":  null,
                 "topMemItems":  null,
                 "shotTime":  "2026-11-16T06:17:07.454461807Z"
             }
}
```

`data` 字段：

| 字段 | JSON 类型 | 示例 |
|---|---|---|
| `uptime` | Int32 | 489415 |
| `timeSinceUptime` | String | 2026-11-10 14:20:12 |
| `runningTime` | object |  |
| `procs` | Int32 | 65 |
| `load1` | Decimal | 3.96 |
| `load5` | Decimal | 1.99 |
| `load15` | Decimal | 1.74 |
| `loadUsagePercent` | Int32 | 132 |
| `cpuPercent` | array[2] |  |
| `cpuUsedPercent` | Decimal | 15.000000000000002 |
| `cpuUsed` | Decimal | 0.30000000000000004 |
| `cpuTotal` | Int32 | 2 |
| `cpuDetailedPercent` | array[8] |  |
| `memoryTotal` | Int64 | 2147483648 |
| `memoryUsed` | Int32 | 1506540544 |
| `memoryFree` | Int32 | 302178304 |
| `memoryShard` | Int32 | 5636096 |
| `memoryCache` | Int32 | 343843840 |
| `memoryAvailable` | Int32 | 640943104 |
| `memoryUsedPercent` | Decimal | 70.15376091003418 |
| `swapMemoryTotal` | Int32 | 0 |
| `swapMemoryAvailable` | Int32 | 0 |
| `swapMemoryUsed` | Int32 | 0 |
| `swapMemoryUsedPercent` | Int32 | 0 |
| `ioReadBytes` | Int64 | 2612200550912 |
| `ioWriteBytes` | Int64 | 828348060672 |
| `ioCount` | Int32 | 456310011 |
| `ioReadTime` | Int32 | 38527327 |
| `ioWriteTime` | Int32 | 36573727 |
| `diskData` | array[2] |  |
| `netBytesSent` | Int32 | 1745969798 |
| `netBytesRecv` | Int64 | 3042485141 |
| `gpuData` | null |  |
| `npuData` | null |  |
| `xpuData` | null |  |
| `topCPUItems` | null |  |
| `topMemItems` | null |  |
| `shotTime` | String | 2026-11-16T06:17:07.454461807Z |

`data.currentInfo.diskData[0]`：

```json
{"code":200,"message":"","data":{"uptime":489415,"timeSinceUptime":"2026-11-10 14:20:12","runningTime":{"days":5,"hours":15,"minutes":56,"seconds":55},"procs":65,"load1":3.96,"load5":1.99,"load15":1.74,"loadUsagePercent":132,"cpuPercent":[18.181818181818176,11.111111111111116],"cpuUsedPercent":15.000000000000002,"cpuUsed":0.30000000000000004,"cpuTotal":2,"cpuDetailedPercent":[5,10,0,85,0,0,0,0],"m
```

## GET /api/v2/dashboard/base/io/net

- HTTP 200，application/json; charset=utf-8，92 ms

```json
{
    "code":  200,
    "message":  "",
    "data":  {
                 "websiteNumber":  0,
                 "agentNumber":  0,
                 "databaseNumber":  5,
                 "cronjobNumber":  0,
                 "appInstalledNumber":  4,
                 "hostname":  "node-a",
                 "os":  "linux",
                 "platform":  "debian",
                 "platformFamily":  "debian",
                 "platformVersion":  "13.7",
                 "prettyDistro":  "Debian GNU/Linux 13",
                 "kernelArch":  "x86_64",
                 "kernelVersion":  "7.0.14-28-pve",
                 "virtualizationSystem":  "{\"hostname\":\"node-a\",\"uptime\":489440,\"bootTime\":1794320412,\"procs\":65,\"os\":\"linux\",\"platform\":\"debian\",\"platformFamily\":\"debian\",\"platformVersion\":\"13.7\",\"kernelVersion\":\"7.0.14-28-pve\",\"kernelArch\":\"x86_64\",\"virtualizationSystem\":\"lxc\",\"virtualizationRole\":\"guest\",\"hostId\":\"00000000-0000-0000-0000-000000000000\"}",
                 "ipV4Addr":  "198.51.100.10",
                 "systemProxy":  "noProxy",
                 "cpuCores":  8,
                 "cpuLogicalCores":  2,
                 "cpuModelName":  "AMD Ryzen 7 2700X Eight-Core Processor",
                 "cpuMhz":  3700,
                 "quickJump":  [
                                   {
                                       "id":  6,
                                       "name":  "Agent",
                                       "alias":  "",
                                       "title":  "aiTools.agents.agent",
                                       "detail":  "0",
                                       "recommend":  1,
                                       "isShow":  true,
                                       "router":  "/ai/agents/agent"
                                   },
                                   {
                                       "id":  1,
                                       "name":  "Website",
                                       "alias":  "",
                                       "title":  "menu.website",
                                       "detail":  "0",
                                       "recommend":  10,
                                       "isShow":  true,
                                       "router":  "/websites"
                                   },
                                   {
                                       "id":  2,
                                       "name":  "Database",
                                       "alias":  "",
                                       "title":  "home.database",
                                       "detail":  "5",
                                       "recommend":  30,
                                       "isShow":  true,
                                       "router":  "/databases"
                                   },
                                   {
                                       "id":  4,
                                       "name":  "AppInstalled",
                                       "alias":  "",
                                       "title":  "home.appInstalled",
                                       "detail":  "4",
                                       "recommend":  70,
                                       "isShow":  true,
                                       "router":  "/apps/installed"
                                   }
                               ],
                 "currentInfo":  {
                                     "uptime":  489415,
                                     "timeSinceUptime":  "2026-11-10 14:20:12",
                                     "runningTime":  {
                                                         "days":  5,
                                                         "hours":  15,
                                                         "minutes":  56,
                                                         "seconds":  55
                                                     },
                                     "procs":  65,
                                     "load1":  3.96,
                                     "load5":  1.99,
                                     "load15":  1.74,
                                     "loadUsagePercent":  132,
                                     "cpuPercent":  [
                                                        18.181818181818176,
                                                        11.111111111111116
                                                    ],
                                     "cpuUsedPercent":  15.000000000000002,
                                     "cpuUsed":  0.30000000000000004,
                                     "cpuTotal":  2,
                                     "cpuDetailedPercent":  [
                                                                5,
                                                                10,
                                                                0,
                                                                85,
                                                                0,
                                                                0,
                                                                0,
                                                                0
                                                            ],
                                     "memoryTotal":  2147483648,
                                     "memoryUsed":  1507153920,
                                     "memoryFree":  301563904,
                                     "memoryShard":  5636096,
                                     "memoryCache":  343844864,
                                     "memoryAvailable":  640329728,
                                     "memoryUsedPercent":  70.18232345581055,
                                     "swapMemoryTotal":  0,
                                     "swapMemoryAvailable":  0,
                                     "swapMemoryUsed":  0,
                                     "swapMemoryUsedPercent":  0,
                                     "ioReadBytes":  0,
                                     "ioWriteBytes":  0,
                                     "ioCount":  0,
                                     "ioReadTime":  0,
                                     "ioWriteTime":  0,
                                     "diskData":  [
                                                      {
                                                          "path":  "/",
                                                          "type":  "zfs",
                                                          "device":  "rpool/data/subvol-101-disk-0",
                                                          "total":  34359738368,
                                                          "free":  13632012288,
                                                          "used":  20727726080,
                                                          "usedPercent":  60.32562255859375,
                                                          "inodesTotal":  26861704,
                                                          "inodesUsed":  236576,
                                                          "inodesFree":  26625128,
                                                          "inodesUsedPercent":  0.8807185128687294
                                                      },
                                                      {
                                                          "path":  "/data/Routing",
                                                          "type":  "cifs",
                                                          "device":  "//198.51.100.1/Data",
                                                          "total":  1967846068224,
                                                          "free":  104215789568,
                                                          "used":  1863630278656,
                                                          "usedPercent":  94.70406800354787,
                                                          "inodesTotal":  0,
                                                          "inodesUsed":  0,
                                                          "inodesFree":  0,
                                                          "inodesUsedPercent":  0
                                                      }
                                                  ],
                                     "netBytesSent":  0,
                                     "netBytesRecv":  0,
                                     "gpuData":  null,
                                     "npuData":  null,
                                     "xpuData":  null,
                                     "topCPUItems":  null,
                                     "topMemItems":  null,
                                     "shotTime":  "2026-11-16T06:17:07.806641164Z"
                                 }
             }
}
```

`data` 字段：

| 字段 | JSON 类型 | 示例 |
|---|---|---|
| `websiteNumber` | Int32 | 0 |
| `agentNumber` | Int32 | 0 |
| `databaseNumber` | Int32 | 5 |
| `cronjobNumber` | Int32 | 0 |
| `appInstalledNumber` | Int32 | 4 |
| `hostname` | String | node-a |
| `os` | String | linux |
| `platform` | String | debian |
| `platformFamily` | String | debian |
| `platformVersion` | String | 13.7 |
| `prettyDistro` | String | Debian GNU/Linux 13 |
| `kernelArch` | String | x86_64 |
| `kernelVersion` | String | 7.0.14-28-pve |
| `virtualizationSystem` | String | {"hostname":"node-a","uptime":489440,"bootTime":... |
| `ipV4Addr` | String | 198.51.100.10 |
| `systemProxy` | String | noProxy |
| `cpuCores` | Int32 | 8 |
| `cpuLogicalCores` | Int32 | 2 |
| `cpuModelName` | String | AMD Ryzen 7 2700X Eight-Core Processor |
| `cpuMhz` | Int32 | 3700 |
| `quickJump` | array[4] |  |
| `currentInfo` | object |  |

`data.currentInfo` 字段：

| 字段 | JSON 类型 | 示例 |
|---|---|---|
| `uptime` | Int32 | 489415 |
| `timeSinceUptime` | String | 2026-11-10 14:20:12 |
| `runningTime` | object |  |
| `procs` | Int32 | 65 |
| `load1` | Decimal | 3.96 |
| `load5` | Decimal | 1.99 |
| `load15` | Decimal | 1.74 |
| `loadUsagePercent` | Int32 | 132 |
| `cpuPercent` | array[2] |  |
| `cpuUsedPercent` | Decimal | 15.000000000000002 |
| `cpuUsed` | Decimal | 0.30000000000000004 |
| `cpuTotal` | Int32 | 2 |
| `cpuDetailedPercent` | array[8] |  |
| `memoryTotal` | Int64 | 2147483648 |
| `memoryUsed` | Int32 | 1507153920 |
| `memoryFree` | Int32 | 301563904 |
| `memoryShard` | Int32 | 5636096 |
| `memoryCache` | Int32 | 343844864 |
| `memoryAvailable` | Int32 | 640329728 |
| `memoryUsedPercent` | Decimal | 70.18232345581055 |
| `swapMemoryTotal` | Int32 | 0 |
| `swapMemoryAvailable` | Int32 | 0 |
| `swapMemoryUsed` | Int32 | 0 |
| `swapMemoryUsedPercent` | Int32 | 0 |
| `ioReadBytes` | Int32 | 0 |
| `ioWriteBytes` | Int32 | 0 |
| `ioCount` | Int32 | 0 |
| `ioReadTime` | Int32 | 0 |
| `ioWriteTime` | Int32 | 0 |
| `diskData` | array[2] |  |
| `netBytesSent` | Int32 | 0 |
| `netBytesRecv` | Int32 | 0 |
| `gpuData` | null |  |
| `npuData` | null |  |
| `xpuData` | null |  |
| `topCPUItems` | null |  |
| `topMemItems` | null |  |
| `shotTime` | String | 2026-11-16T06:17:07.806641164Z |

## GET /api/v2/dashboard/current/io/net

- HTTP 200，application/json; charset=utf-8，96 ms

```json
{
    "code":  200,
    "message":  "",
    "data":  {
                 "uptime":  489416,
                 "timeSinceUptime":  "2026-11-10 14:20:12",
                 "runningTime":  {
                                     "days":  5,
                                     "hours":  15,
                                     "minutes":  56,
                                     "seconds":  56
                                 },
                 "procs":  65,
                 "load1":  3.96,
                 "load5":  1.99,
                 "load15":  1.74,
                 "loadUsagePercent":  132,
                 "cpuPercent":  [
                                    18.181818181818176,
                                    11.111111111111116
                                ],
                 "cpuUsedPercent":  15.000000000000002,
                 "cpuUsed":  0.30000000000000004,
                 "cpuTotal":  2,
                 "cpuDetailedPercent":  [
                                            5,
                                            10,
                                            0,
                                            85,
                                            0,
                                            0,
                                            0,
                                            0
                                        ],
                 "memoryTotal":  2147483648,
                 "memoryUsed":  1507244032,
                 "memoryFree":  301473792,
                 "memoryShard":  5636096,
                 "memoryCache":  343844864,
                 "memoryAvailable":  640239616,
                 "memoryUsedPercent":  70.18651962280273,
                 "swapMemoryTotal":  0,
                 "swapMemoryAvailable":  0,
                 "swapMemoryUsed":  0,
                 "swapMemoryUsedPercent":  0,
                 "ioReadBytes":  0,
                 "ioWriteBytes":  0,
                 "ioCount":  0,
                 "ioReadTime":  0,
                 "ioWriteTime":  0,
                 "diskData":  [
                                  {
                                      "path":  "/",
                                      "type":  "zfs",
                                      "device":  "rpool/data/subvol-101-disk-0",
                                      "total":  34359738368,
                                      "free":  13632012288,
                                      "used":  20727726080,
                                      "usedPercent":  60.32562255859375,
                                      "inodesTotal":  26861704,
                                      "inodesUsed":  236576,
                                      "inodesFree":  26625128,
                                      "inodesUsedPercent":  0.8807185128687294
                                  },
                                  {
                                      "path":  "/data/Routing",
                                      "type":  "cifs",
                                      "device":  "//198.51.100.1/Data",
                                      "total":  1967846068224,
                                      "free":  104215789568,
                                      "used":  1863630278656,
                                      "usedPercent":  94.70406800354787,
                                      "inodesTotal":  0,
                                      "inodesUsed":  0,
                                      "inodesFree":  0,
                                      "inodesUsedPercent":  0
                                  }
                              ],
                 "netBytesSent":  0,
                 "netBytesRecv":  0,
                 "gpuData":  null,
                 "npuData":  null,
                 "xpuData":  null,
                 "topCPUItems":  null,
                 "topMemItems":  null,
                 "shotTime":  "2026-11-16T06:17:08.162426097Z"
             }
}
```

`data` 字段：

| 字段 | JSON 类型 | 示例 |
|---|---|---|
| `uptime` | Int32 | 489416 |
| `timeSinceUptime` | String | 2026-11-10 14:20:12 |
| `runningTime` | object |  |
| `procs` | Int32 | 65 |
| `load1` | Decimal | 3.96 |
| `load5` | Decimal | 1.99 |
| `load15` | Decimal | 1.74 |
| `loadUsagePercent` | Int32 | 132 |
| `cpuPercent` | array[2] |  |
| `cpuUsedPercent` | Decimal | 15.000000000000002 |
| `cpuUsed` | Decimal | 0.30000000000000004 |
| `cpuTotal` | Int32 | 2 |
| `cpuDetailedPercent` | array[8] |  |
| `memoryTotal` | Int64 | 2147483648 |
| `memoryUsed` | Int32 | 1507244032 |
| `memoryFree` | Int32 | 301473792 |
| `memoryShard` | Int32 | 5636096 |
| `memoryCache` | Int32 | 343844864 |
| `memoryAvailable` | Int32 | 640239616 |
| `memoryUsedPercent` | Decimal | 70.18651962280273 |
| `swapMemoryTotal` | Int32 | 0 |
| `swapMemoryAvailable` | Int32 | 0 |
| `swapMemoryUsed` | Int32 | 0 |
| `swapMemoryUsedPercent` | Int32 | 0 |
| `ioReadBytes` | Int32 | 0 |
| `ioWriteBytes` | Int32 | 0 |
| `ioCount` | Int32 | 0 |
| `ioReadTime` | Int32 | 0 |
| `ioWriteTime` | Int32 | 0 |
| `diskData` | array[2] |  |
| `netBytesSent` | Int32 | 0 |
| `netBytesRecv` | Int32 | 0 |
| `gpuData` | null |  |
| `npuData` | null |  |
| `xpuData` | null |  |
| `topCPUItems` | null |  |
| `topMemItems` | null |  |
| `shotTime` | String | 2026-11-16T06:17:08.162426097Z |

`data.currentInfo.diskData[0]`：

```json
{"code":200,"message":"","data":{"uptime":489416,"timeSinceUptime":"2026-11-10 14:20:12","runningTime":{"days":5,"hours":15,"minutes":56,"seconds":56},"procs":65,"load1":3.96,"load5":1.99,"load15":1.74,"loadUsagePercent":132,"cpuPercent":[18.181818181818176,11.111111111111116],"cpuUsedPercent":15.000000000000002,"cpuUsed":0.30000000000000004,"cpuTotal":2,"cpuDetailedPercent":[5,10,0,85,0,0,0,0],"m
```

