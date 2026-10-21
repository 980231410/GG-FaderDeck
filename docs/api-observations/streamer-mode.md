# Sonar Streamer 模式 API 观测（GG 116.0.0 / Sonar 1.104.0）

> 本文档记录的是对**公开分发的第三方软件**在本地运行时行为的观察结果（HTTP 接口的形状），
> 属于事实性信息，不包含该软件的源代码或资源。相关商标归其所有者，本项目与其无关联。

逆向来源：`C:\Program Files\SteelSeries\GG\apps\sonar\SoundStage.Api.dll` 路由模板字符串 +
`C:\Program Files\SteelSeries\GG\resources\app.asar` 客户端调用点，全部结论随后在本机
`GET/PUT` 往返验证（Sonar 端口由 GG 动态分配（本机实测落在 5xxxx 段），不可硬编码）。

## 读取（已实测 200）

| 路由 | 说明 |
| --- | --- |
| `GET /mode` | 当前模式：`"classic"` 或 `"stream"`（JSON 字符串） |
| `GET /volumeSettings/streamer` | 主播混音全量文档，见下方结构 |
| `GET /streamRedirections` | 三条重定向：`streaming` / `monitoring` / `mic` |
| `GET /streamRedirections/isStreamMonitoringEnabled` | 直播监听开关（bool） |
| `GET /streamRedirections/isStreamMonitoringLocked` | 直播监听是否被 GG 锁定（bool） |
| `GET /deviceOut` | `{selectedDeviceOut, deviceOutSettings:[{volume,mute,feature}]}` |
| `GET /deviceOut/audioPath` | 当前输出路径（如 `"HeadphoneOut"`） |
| `GET /deviceOut/volume/{feature}`、`GET /deviceOut/mute/{feature}` | 单个硬件输出状态 |

`/volumeSettings/streamer` 结构（每个通道同时携带两套主播混音）：

```json
{
  "masters": {
    "stream": { "streaming": { "volume": 1.0, "muted": false },
                "monitoring": { "volume": 1.0, "muted": false } },
    "classic": { "volume": 0.0, "muted": false }
  },
  "devices": {
    "game":        { "stream": { "streaming": { ... }, "monitoring": { ... } }, "classic": { ... } },
    "chatRender":  { ... }, "chatCapture": { ... }, "media": { ... }, "aux": { ... }
  }
}
```

`GET /streamRedirections` 每条记录：

```json
{ "streamRedirectionId": "streaming",
  "deviceId": "{0.0.0.00000000}.{...}",
  "status": [ { "role": "chatCapture", "isEnabled": true }, ... ],
  "isRunning": true }
```

- `streaming` = 送给直播的混音，输出设备是 Sonar 的 “Stream” 虚拟渲染端点。
- `monitoring` = 自己在耳机里听到的混音，输出设备是当前硬件输出。
- `mic` 的 `status` 为空数组（话筒不走通道开关）。

## 写入（已实测 200，且回读生效）

| 路由 | 说明 |
| --- | --- |
| `PUT /mode/{mode}` | `classic` / `stream`；返回新模式的 JSON 字符串 |
| `PUT /volumeSettings/streamer/{mix}/master/volume/{0..1}` | 混音主音量 |
| `PUT /volumeSettings/streamer/{mix}/{role}/volume/{0..1}` | 通道音量 |
| `PUT /volumeSettings/streamer/{mix}/master/isMuted/{bool}` | 混音主静音 |
| `PUT /volumeSettings/streamer/{mix}/{role}/isMuted/{bool}` | 通道静音 |
| `PUT /streamRedirections/{mix}/redirections/{role}/isEnabled/{bool}` | 通道是否进入该混音 |
| `PUT /streamRedirections/isStreamMonitoringEnabled/{bool}` | 直播监听开关 |
| `PUT /deviceOut/volume/{feature}/{0..1}`、`PUT /deviceOut/mute/{feature}/{bool}` | 硬件输出音量/静音 |
| `PUT /deviceOut/audioPath/{feature}` | 切换输出路径 |

约定：

- `{mix}` ∈ `streaming` | `monitoring`（与 `streamRedirectionId` 同名）。
- `{role}` ∈ `game` | `chatRender` | `chatCapture` | `media` | `aux`（与 `status[].role` 同名）。
- **路径大小写与经典模式不同**：主播混音用 `volume` / `isMuted`，经典模式用 `Volume` / `Mute`。
- 音量必须用 `InvariantCulture` 的 `"R"` 格式，布尔必须小写。
- 写入响应只回显被改动的通道子集（不是全量文档），因此每次写入后都应回读
  `/volumeSettings/streamer` 或 `/deviceOut` 确认，避免 UI 显示与实际不一致。
- 直接 HTTP 写入不会触发 `/sock` 的 `SONAR_EVENT_VOLUME_DATA` 广播，主播混音只能靠轮询
  或本地回显保持同步（经典模式走虚拟端点的路径不受影响）。

## 未暴露 / 不可用

- `/volumeSettings`、`/systemSettings`、`/audioInitialization`、`/quickset`、`/steelseriesDevice`、
  `/fallbackSettings`、`/status`、`/presets`（无通道后缀）均为 404。
- `GET /volumeSettings/streamer/{mix}` 为 404：`{mix}` 只存在于 `PUT` 叶子路由上。
- `GET /deviceOut/audioPath/{feature}` 为 405：`audioPath` 只有集合级 GET 与叶子级 PUT。
- `/swagger`、`/openapi`、根路径全部 404（虽然进程内加载了 Swashbuckle，但没有开启终结点）。